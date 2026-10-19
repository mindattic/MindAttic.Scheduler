# Pester tests for install.ps1. Run: Invoke-Pester .\tests
# The integration block registers real tasks, but only under a scratch folder
# (\MindAttic\WatchDog\Tests\), and deletes them afterwards. No elevation needed.

BeforeAll {
    . (Join-Path $PSScriptRoot '..\install.ps1')
    $script:Scratch = Join-Path ([IO.Path]::GetTempPath()) ('watchdog-tests-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Scratch | Out-Null

    function Write-Manifest([object[]]$Jobs) {
        $path = Join-Path $script:Scratch ([guid]::NewGuid().ToString('N') + '.json')
        ConvertTo-Json -InputObject @{ jobs = @($Jobs) } -Depth 10 | Set-Content -LiteralPath $path -Encoding UTF8
        $path
    }
}

AfterAll {
    Remove-Item -LiteralPath $script:Scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'jobs.json' {
    It 'is a valid manifest' {
        { Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json') } | Should -Not -Throw
    }

    It 'runs Automata''s tick every 5 minutes and at logon, replacing Automata''s own task' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'Automata.Tick'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Match 'automata-runner\.exe" tick$'
        @($job.Schedule | Where-Object { $_.every -eq '00:05:00' }).Count | Should -Be 1
        @($job.Schedule | Where-Object { $_.atLogon -eq $true }).Count | Should -Be 1
        ($job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }) | Should -Contain '\MindAttic\Automata\MindAttic.Automata.Tick'
    }
}

Describe 'Read-JobManifest validation' {
    It 'accepts every, daily and atLogon schedules, single or as a list' {
        $m = Write-Manifest @(
            @{ name = 'A'; command = 'a.exe'; schedule = @{ every = '00:05:00' } },
            @{ name = 'B'; command = 'b.exe'; schedule = @(@{ daily = '02:30' }, @{ atLogon = $true }) }
        )
        $jobs = Read-JobManifest $m
        $jobs.Count | Should -Be 2
        @($jobs[1].Schedule).Count | Should -Be 2
    }

    It 'expands environment variables in the command' {
        $m = Write-Manifest @(@{ name = 'A'; command = '%SystemRoot%\x.exe'; schedule = @{ atLogon = $true } })
        (Read-JobManifest $m)[0].Command | Should -Be "$env:SystemRoot\x.exe"
    }

    It 'rejects <case>' -ForEach @(
        @{ case = 'a duplicate name';      jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true } }, @{ name = 'a'; command = 'b'; schedule = @{ atLogon = $true } }) }
        @{ case = 'a missing command';     jobs = @(@{ name = 'A'; schedule = @{ atLogon = $true } }) }
        @{ case = 'a missing schedule';    jobs = @(@{ name = 'A'; command = 'a' }) }
        @{ case = 'two kinds in one entry'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true; daily = '01:00' } }) }
        @{ case = 'a bad daily time';      jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ daily = '25:00' } }) }
        @{ case = 'a sub-minute interval'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ every = '00:00:30' } }) }
        @{ case = 'a backslash in a name'; jobs = @(@{ name = 'A\B'; command = 'a'; schedule = @{ atLogon = $true } }) }
        @{ case = 'replacing a WatchDog task'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true }; replaces = @('\MindAttic\WatchDog\Other') }) }
    ) {
        { Read-JobManifest (Write-Manifest $jobs) } | Should -Throw
    }
}

Describe 'Split-TaskFullName' {
    It 'splits a foldered path' {
        $r = Split-TaskFullName '\MindAttic\Automata\MindAttic.Automata.Tick'
        $r.Path | Should -Be '\MindAttic\Automata\'
        $r.Name | Should -Be 'MindAttic.Automata.Tick'
    }
    It 'treats a bare name as a root-level task' {
        $r = Split-TaskFullName 'MindAttic LghubWatchdog'
        $r.Path | Should -Be '\'
        $r.Name | Should -Be 'MindAttic LghubWatchdog'
    }
}

Describe 'Install-WatchDogJobs against the real Task Scheduler' {
    BeforeAll {
        $script:Folder = '\MindAttic\WatchDog\Tests\'
        $script:OldFolder = '\MindAttic\WatchDogTestsOld\'
        $script:Cmd = 'cmd.exe /c exit 0'
        function Get-Tasks($path) { @(Get-ScheduledTask -TaskPath $path -ErrorAction SilentlyContinue | Where-Object TaskPath -eq $path) }
        function Remove-Folder($path) {
            Get-Tasks $path | ForEach-Object { Unregister-ScheduledTask -TaskPath $path -TaskName $_.TaskName -Confirm:$false }
            try {
                $svc = New-Object -ComObject Schedule.Service; $svc.Connect()
                $p = $path.TrimEnd('\'); $svc.GetFolder((Split-Path $p -Parent)).DeleteFolder((Split-Path $p -Leaf), 0)
            } catch { }
        }
        Remove-Folder $script:Folder
        Remove-Folder $script:OldFolder
    }

    AfterAll {
        Remove-Folder $script:Folder
        Remove-Folder $script:OldFolder
    }

    It 'registers each job headless under the folder, retires what it replaces, and is idempotent' {
        # An old copy, registered the way an app used to do it.
        Register-ScheduledTask -TaskPath $script:OldFolder -TaskName 'Legacy' `
            -Action (New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c exit 0') `
            -Trigger (New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME") | Out-Null

        $m = Write-Manifest @(
            @{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' }; replaces = @("$($script:OldFolder)Legacy") },
            @{ name = 'Nightly'; command = $script:Cmd; workingDirectory = '%TEMP%'; schedule = @{ daily = '03:15' } }
        )

        Install-WatchDogJobs -Manifest $m -TaskPath $script:Folder | Out-Null
        Install-WatchDogJobs -Manifest $m -TaskPath $script:Folder | Out-Null   # second run: no change, no error

        $tasks = Get-Tasks $script:Folder
        ($tasks.TaskName | Sort-Object) -join ',' | Should -Be 'Nightly,Tick'
        Get-Tasks $script:OldFolder | Should -BeNullOrEmpty

        $tick = Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Tick'
        $tick.Actions[0].Execute | Should -Match 'conhost\.exe$'
        $tick.Actions[0].Arguments | Should -Be "--headless $($script:Cmd)"
        $tick.Principal.LogonType | Should -Be 'Interactive'
        $tick.Triggers[0].Repetition.Interval | Should -Be 'PT5M'
        $tick.Triggers[0].Repetition.Duration | Should -BeNullOrEmpty   # indefinitely
        $tick.Settings.MultipleInstances | Should -Be 'IgnoreNew'

        $nightly = Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Nightly'
        $nightly.Actions[0].WorkingDirectory | Should -Be $env:TEMP
    }

    It 'unregisters jobs dropped from the manifest but leaves owner subfolders alone' {
        $sub = "$($script:Folder)Owner\"
        Register-ScheduledTask -TaskPath $sub -TaskName 'Runtime' `
            -Action (New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c exit 0') `
            -Trigger (New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME") | Out-Null
        try {
            $m = Write-Manifest @(@{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } })
            Install-WatchDogJobs -Manifest $m -TaskPath $script:Folder | Out-Null

            (Get-Tasks $script:Folder).TaskName | Should -Be 'Tick'
            (Get-Tasks $sub).TaskName | Should -Be 'Runtime'
        }
        finally { Remove-Folder $sub }
    }

    It 'changes nothing under -WhatIf' {
        $m = Write-Manifest @()
        Install-WatchDogJobs -Manifest $m -TaskPath $script:Folder -WhatIf | Out-Null
        (Get-Tasks $script:Folder).TaskName | Should -Be 'Tick'
    }
}
