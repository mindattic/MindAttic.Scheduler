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

    It 'runs the Launcher nightly backup at midnight keeping 30, replacing the Launcher''s own task' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'Launcher.NightlyBackup'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Match 'MindAttic\.Launcher\.exe" backup --keep 30$'
        @($job.Schedule).Count | Should -Be 1
        $job.Schedule[0].daily | Should -Be '00:00'
        $replaced = $job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }
        $replaced | Should -Contain '\MindAttic\Launcher\MindAtticLauncherNightlyBackup'
        $replaced | Should -Contain '\MindAtticLauncherNightlyBackup'
    }

    It 'runs IdiotProof''s research scan hourly, capped at 30 minutes, replacing IdiotProof''s own task and its stale root copy' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'IdiotProof.ResearchScan'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Be '"C:\Apps\IdiotProof\ResearchScanner\idiotproof-research-scanner.exe"'
        $job.WorkingDirectory | Should -Be 'C:\Apps\IdiotProof\ResearchScanner'
        @($job.Schedule).Count | Should -Be 1
        @($job.Schedule)[0].every | Should -Be '01:00:00'
        $job.TimeLimit | Should -Be ([TimeSpan]::FromMinutes(30))
        $replaced = $job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }
        $replaced | Should -Contain '\MindAttic\IdiotProof\IdiotProof Research Scan'
        $replaced | Should -Contain '\IdiotProof Research Scan'
    }

    It 'refreshes Jellyfin posters daily at midnight from the repo folder, replacing JellyFinPoster''s own task' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'JellyFinPoster.Refresh'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Match '^pythonw "[^"]+\\jellyfin_poster\.py"$'
        $job.WorkingDirectory | Should -Be 'D:\Projects\MindAttic\JellyFinPoster'
        @($job.Schedule | Where-Object { $_.daily -eq '00:00' }).Count | Should -Be 1
        $replaced = $job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }
        $replaced | Should -Contain '\MindAttic\JellyFinPoster\JellyfinPosterRefresh'
        $replaced | Should -Contain '\JellyfinPosterRefresh'
    }

    It 'runs the Orchestrator daemon at logon and keeps it running, replacing the Orchestrator''s own task' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'Orchestrator.Daemon'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Be '"C:\Apps\MindAttic.Orchestrator\MindAttic.Orchestrator.exe" --daemon'
        $job.WorkingDirectory | Should -Be 'C:\Apps\MindAttic.Orchestrator'
        @($job.Schedule).Count | Should -Be 1
        $job.Schedule[0].atLogon | Should -BeTrue
        $job.KeepRunning | Should -BeTrue
        $job.TimeLimit | Should -Be ([TimeSpan]::Zero)
        ($job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }) | Should -Contain '\MindAttic\Orchestrator\Orchestrator daemon'
    }

    It 'runs UnstuckTaskbar at logon and keeps it running, replacing UnstuckTaskbar''s own logon task' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'UnstuckTaskbar'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Be '"C:\Apps\Unstuck\UnstuckTaskbar.exe"'
        $job.WorkingDirectory | Should -Be 'C:\Apps\Unstuck'
        @($job.Schedule).Count | Should -Be 1
        $job.Schedule[0].atLogon | Should -BeTrue
        $job.KeepRunning | Should -BeTrue
        $job.TimeLimit | Should -Be ([TimeSpan]::Zero)
        ($job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }) | Should -Contain '\MindAttic\UnstuckTaskbar\Logon'
    }

    It 'guards against the G HUB agent leak hourly with the repo''s own script, replacing the root-level LghubWatchdog task' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'Lghub.LeakGuard'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Be 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\Projects\MindAttic\MindAttic.WatchDog\scripts\restart-lghub-agent.ps1" -LimitGB 2'
        $job.Command | Should -Not -Match 'LOCALAPPDATA|AppData'
        Test-Path (Join-Path $PSScriptRoot '..\scripts\restart-lghub-agent.ps1') | Should -BeTrue
        @($job.Schedule).Count | Should -Be 1
        @($job.Schedule)[0].every | Should -Be '01:00:00'
        $job.TimeLimit | Should -Be ([TimeSpan]::FromMinutes(2))
        ($job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }) | Should -Contain '\MindAttic LghubWatchdog'
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
        @{ case = 'keepRunning with a timeLimit'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true }; keepRunning = $true; timeLimit = '01:00:00' }) }
        @{ case = 'a non-boolean keepRunning'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true }; keepRunning = 'yes' }) }
    ) {
        { Read-JobManifest (Write-Manifest $jobs) } | Should -Throw
    }
}

Describe 'New-JobAction' {
    It 'runs a console program through conhost --headless' {
        $cmd = "`"$env:SystemRoot\System32\cmd.exe`" /c exit 0"
        $a = New-JobAction ([pscustomobject]@{ Command = $cmd; WorkingDirectory = $null })
        $a.Execute | Should -Match 'conhost\.exe$'
        $a.Arguments | Should -Be "--headless $cmd"
    }
    It 'starts a GUI-subsystem program directly, so the task tracks it' {
        $exe = "$env:SystemRoot\System32\charmap.exe"
        $a = New-JobAction ([pscustomobject]@{ Command = "`"$exe`" --daemon"; WorkingDirectory = 'C:\x' })
        $a.Execute | Should -Be $exe
        $a.Arguments | Should -Be '--daemon'
        $a.WorkingDirectory | Should -Be 'C:\x'
    }
    It 'treats a program that isn''t there as a console program' {
        Test-GuiProgram 'C:\does\not\exist.exe' | Should -BeFalse
        (New-JobAction ([pscustomobject]@{ Command = 'C:\does\not\exist.exe'; WorkingDirectory = $null })).Execute | Should -Match 'conhost\.exe$'
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
            Get-Tasks $path | ForEach-Object { Stop-ScheduledTask -TaskPath $path -TaskName $_.TaskName -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskPath $path -TaskName $_.TaskName -Confirm:$false }
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

    It 'keeps a keepRunning job running: no time limit, restarted on failure, started on install' {
        $m = Write-Manifest @(@{ name = 'Daemon'; command = 'cmd.exe /c ping -n 120 127.0.0.1'; schedule = @{ atLogon = $true }; keepRunning = $true })
        try {
            $out = Install-WatchDogJobs -Manifest $m -TaskPath $script:Folder
            $out | Should -Contain "  started $($script:Folder)Daemon"
            $d = Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Daemon'
            $d.State | Should -Be 'Running'
            $d.Settings.ExecutionTimeLimit | Should -Be 'PT0S'
            $d.Settings.RestartCount | Should -Be 3
            $d.Settings.RestartInterval | Should -Be 'PT1M'
            # Rerunning leaves the running instance alone.
            Install-WatchDogJobs -Manifest $m -TaskPath $script:Folder | Should -Not -Contain "  started $($script:Folder)Daemon"
        }
        finally {
            Stop-ScheduledTask -TaskPath $script:Folder -TaskName 'Daemon' -ErrorAction SilentlyContinue
            # Put the folder back the way the -WhatIf test expects it.
            Install-WatchDogJobs -Manifest (Write-Manifest @(@{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } })) -TaskPath $script:Folder | Out-Null
        }
    }

    It 'changes nothing under -WhatIf' {
        $m = Write-Manifest @()
        Install-WatchDogJobs -Manifest $m -TaskPath $script:Folder -WhatIf | Out-Null
        (Get-Tasks $script:Folder).TaskName | Should -Be 'Tick'
    }
}
