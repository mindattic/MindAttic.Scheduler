# Pester tests for scheduler.ps1 (scheduler job add / cancel / list). Run: Invoke-Pester .\tests
# The integration block registers real tasks, but only under a scratch root
# (\MindAttic\Scheduler\Tests\), and deletes them afterwards. No elevation needed.

BeforeAll {
    $script:Scheduler = Join-Path $PSScriptRoot '..\scheduler.ps1'
    . $script:Scheduler
}

Describe 'Read-JobArguments' {
    It 'splits positionals, valued flags, switches and repeated flags' {
        $p = Read-JobArguments @('Name', '--owner', 'Psst', '--replaces', 'A', '--json', '--replaces', '\X\B')
        $p.Positional | Should -Be @('Name')
        $p.Flags['owner'] | Should -Be @('Psst')
        $p.Flags['replaces'] | Should -Be @('A', '\X\B')
        $p.Flags['json'] | Should -Be @($true)
    }

    It 'rejects a flag with no value' {
        { Read-JobArguments @('Name', '--owner') } | Should -Throw '*needs a value*'
    }
}

Describe 'Invoke-SchedulerCommand validation' {
    It 'rejects <case>' -ForEach @(
        @{ case = 'no verb';                  argv = @('job') }
        @{ case = 'an unknown verb';          argv = @('job', 'frob') }
        @{ case = 'add without --owner';      argv = @('job', 'add', 'A', '--every', '00:05:00', '--run', 'x') }
        @{ case = 'add without --run';        argv = @('job', 'add', 'A', '--owner', 'O', '--every', '00:05:00') }
        @{ case = 'add with --at and --every'; argv = @('job', 'add', 'A', '--owner', 'O', '--every', '00:05:00', '--at', '2999-01-01T00:00', '--run', 'x') }
        @{ case = 'add with neither';         argv = @('job', 'add', 'A', '--owner', 'O', '--run', 'x') }
        @{ case = 'add at a past time';       argv = @('job', 'add', 'A', '--owner', 'O', '--at', '2001-01-01T00:00', '--run', 'x') }
        @{ case = 'add every 30 seconds';     argv = @('job', 'add', 'A', '--owner', 'O', '--every', '00:00:30', '--run', 'x') }
        @{ case = 'a backslash in a name';    argv = @('job', 'add', 'A\B', '--owner', 'O', '--every', '00:05:00', '--run', 'x') }
        @{ case = 'a backslash in an owner';  argv = @('job', 'list', '--owner', 'O\P') }
        @{ case = 'an unknown option';        argv = @('job', 'list', '--owner', 'O', '--frob', 'x') }
        @{ case = 'replacing a Scheduler task'; argv = @('job', 'add', 'A', '--owner', 'O', '--every', '00:05:00', '--run', 'x', '--replaces', '\MindAttic\Scheduler\Automata.Tick') }
    ) {
        { Invoke-SchedulerCommand $argv } | Should -Throw
    }
}

Describe 'ConvertTo-FireTime' {
    It 'reads ISO local times' {
        $now = [datetime]'2026-01-01T00:00'
        ConvertTo-FireTime '2026-10-19T09:30' $now | Should -Be ([datetime]'2026-10-19T09:30')
        ConvertTo-FireTime '2026-10-19 09:30:15' $now | Should -Be ([datetime]'2026-10-19T09:30:15')
    }
}

Describe 'ConvertTo-ScheduleHuman for arranged jobs (M.SCHD-T42)' {
    It 'shows an auto entry''s arranged slot' {
        ConvertTo-ScheduleHuman @([pscustomobject]@{ daily = 'auto' }) 'Backup' @{ Backup = '01:15' } | Should -Be 'auto (01:15)'
    }
    It 'says when an auto entry has no slot yet' {
        ConvertTo-ScheduleHuman @([pscustomobject]@{ daily = 'auto' }) 'Backup' @{ Other = '01:15' } | Should -Be 'auto (not arranged yet)'
        ConvertTo-ScheduleHuman @([pscustomobject]@{ daily = 'Auto' }) 'Backup' $null | Should -Be 'auto (not arranged yet)'
    }
    It 'keeps daysOfWeek on an auto entry' {
        ConvertTo-ScheduleHuman @([pscustomobject]@{ daily = 'auto'; daysOfWeek = @('Mon', 'Tue', 'Wed', 'Thu', 'Fri') }) 'A' @{ A = '02:00' } | Should -Be 'weekdays auto (02:00)'
        ConvertTo-ScheduleHuman @([pscustomobject]@{ daily = 'auto'; daysOfWeek = @('Sat', 'Sun') }) 'A' @{ A = '02:00' } | Should -Be 'Sat/Sun auto (02:00)'
    }
    It 'leaves a pinned time as it was' {
        ConvertTo-ScheduleHuman @([pscustomobject]@{ daily = '23:30' }) 'A' @{ A = '02:00' } | Should -Be 'daily at 23:30'
    }
}

Describe 'ConvertFrom-ActionCommand' {
    It 'drops conhost''s --headless' {
        ConvertFrom-ActionCommand 'C:\Windows\System32\conhost.exe' '--headless cmd.exe /c exit 0' | Should -Be 'cmd.exe /c exit 0'
    }
    It 'drops the run wrapper, with or without --exclusive and a quoted name' {
        ConvertFrom-ActionCommand 'conhost.exe' '--headless "C:\Apps\MindAttic.Scheduler\mindattic-scheduler.exe" run Backup --exclusive -- "C:\x y\b.exe" --keep 3' | Should -Be '"C:\x y\b.exe" --keep 3'
        ConvertFrom-ActionCommand 'conhost.exe' '--headless "C:\Apps\MindAttic.Scheduler\mindattic-scheduler.exe" run "Two Words" -- cmd.exe /c exit 0' | Should -Be 'cmd.exe /c exit 0'
    }
    It 'quotes a directly-started GUI program with a space in its path' {
        ConvertFrom-ActionCommand 'C:\A B\x.exe' '--daemon' | Should -Be '"C:\A B\x.exe" --daemon'
    }
}

Describe 'scheduler job against the real Task Scheduler' {
    BeforeAll {
        $script:Root = '\MindAttic\Scheduler\Tests\'
        $script:Owner = 'JobApi'
        $script:Folder = "$($script:Root)$($script:Owner)\"
        $script:OldFolder = '\MindAttic\SchedulerJobApiTestsOld\'
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

    It 'adds one-off and repeating jobs headless under the owner''s folder, retiring what they replace' {
        Register-ScheduledTask -TaskPath $script:OldFolder -TaskName 'Legacy' `
            -Action (New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c exit 0') `
            -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddDays(1)) | Out-Null

        $at = (Get-Date).AddDays(1).Date.AddHours(9).AddMinutes(30)
        Add-SchedulerJob -Name 'Once' -Owner $script:Owner -At $at -Command 'cmd.exe /c "C:\A Dir\x.cmd"' `
            -Replaces "$($script:OldFolder)Legacy" -Root $script:Root | Out-Null
        Add-SchedulerJob -Name 'Often' -Owner $script:Owner -Every '00:10:00' -Command 'cmd.exe /c exit 0' -Root $script:Root | Out-Null
        Add-SchedulerJob -Name 'Often' -Owner $script:Owner -Every '00:15:00' -Command 'cmd.exe /c exit 0' -Root $script:Root | Out-Null   # overwrite

        ((Get-Tasks $script:Folder).TaskName | Sort-Object) -join ',' | Should -Be 'Often,Once'
        Get-Tasks $script:OldFolder | Should -BeNullOrEmpty

        $once = Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Once'
        $once.Actions[0].Execute | Should -Match 'conhost\.exe$'
        $once.Actions[0].Arguments | Should -Be '--headless cmd.exe /c "C:\A Dir\x.cmd"'
        $once.Principal.LogonType | Should -Be 'Interactive'
        ([datetime]$once.Triggers[0].StartBoundary) | Should -Be $at
        $once.Triggers[0].Repetition.Interval | Should -BeNullOrEmpty

        (Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Often').Triggers[0].Repetition.Interval | Should -Be 'PT15M'
    }

    It 'lists the owner''s jobs with their command and next run' {
        $jobs = @(Get-SchedulerJob -Owner $script:Owner -Root $script:Root)
        ($jobs.name | Sort-Object) -join ',' | Should -Be 'Often,Once'
        $once = $jobs | Where-Object name -eq 'Once'
        $once.command | Should -Be 'cmd.exe /c "C:\A Dir\x.cmd"'
        $once.task | Should -Be "$($script:Folder)Once"
        $once.nextRun | Should -Not -BeNullOrEmpty
        @(Get-SchedulerJob -Owner 'NobodyHere' -Root $script:Root).Count | Should -Be 0
    }

    It 'cancels a job, refuses an unknown one, and drops the owner''s folder once empty' {
        Remove-SchedulerJob -Name 'Once' -Owner $script:Owner -Root $script:Root | Out-Null
        { Remove-SchedulerJob -Name 'Once' -Owner $script:Owner -Root $script:Root } | Should -Throw '*No job*'
        Remove-SchedulerJob -Name 'Often' -Owner $script:Owner -Root $script:Root | Out-Null
        Get-Tasks $script:Folder | Should -BeNullOrEmpty
        { $svc = New-Object -ComObject Schedule.Service; $svc.Connect(); $svc.GetFolder($script:Folder.TrimEnd('\')) } | Should -Throw
    }

    It 'runs as a command line: add, list --json and cancel, exit 0; a bad call exits 1' {
        $exe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        # The real root, a test-only owner: the command line has no --root option.
        $ErrorActionPreference = 'Continue'   # the failing call's stderr is expected
        $owner = 'SchedulerTests'
        $at = (Get-Date).AddDays(1).ToString('yyyy-MM-ddTHH:mm')
        try {
            & $exe -NoProfile -ExecutionPolicy Bypass -File $script:Scheduler job add Cli --owner $owner --at $at --run 'cmd.exe /c exit 0' | Out-Null
            $LASTEXITCODE | Should -Be 0
            $json = & $exe -NoProfile -ExecutionPolicy Bypass -File $script:Scheduler job list --owner $owner --json
            $LASTEXITCODE | Should -Be 0
            $listed = @(($json -join '') | ConvertFrom-Json)
            $listed.Count | Should -Be 1
            $listed[0].name | Should -Be 'Cli'
            $listed[0].command | Should -Be 'cmd.exe /c exit 0'
            & $exe -NoProfile -ExecutionPolicy Bypass -File $script:Scheduler job cancel Cli --owner $owner | Out-Null
            $LASTEXITCODE | Should -Be 0
            & $exe -NoProfile -ExecutionPolicy Bypass -File $script:Scheduler job cancel Cli --owner $owner 2>$null | Out-Null
            $LASTEXITCODE | Should -Be 1
        }
        finally { Remove-Folder "\MindAttic\Scheduler\$owner\" }
    }
}

Describe 'scheduler job status/enable/disable against the real Task Scheduler' {
    BeforeAll {
        $script:StatusRoot = '\MindAttic\Scheduler\TestsStatus\'
        $script:StatusOwner = 'StatusOwner'
        $script:StatusOwnerFolder = "$($script:StatusRoot)$($script:StatusOwner)\"
        $script:StatusScratch = Join-Path ([IO.Path]::GetTempPath()) ('scheduler-status-tests-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:StatusScratch | Out-Null

        function Get-Tasks($path) { @(Get-ScheduledTask -TaskPath $path -ErrorAction SilentlyContinue | Where-Object TaskPath -eq $path) }
        function Remove-Folder($path) {
            Get-Tasks $path | ForEach-Object { Unregister-ScheduledTask -TaskPath $path -TaskName $_.TaskName -Confirm:$false }
            try {
                $svc = New-Object -ComObject Schedule.Service; $svc.Connect()
                $p = $path.TrimEnd('\'); $svc.GetFolder((Split-Path $p -Parent)).DeleteFolder((Split-Path $p -Leaf), 0)
            } catch { }
        }
        function Write-Manifest([object[]]$Jobs) {
            $path = Join-Path $script:StatusScratch ([guid]::NewGuid().ToString('N') + '.json')
            ConvertTo-Json -InputObject @{ jobs = @($Jobs) } -Depth 10 | Set-Content -LiteralPath $path -Encoding UTF8
            $path
        }
        Remove-Folder $script:StatusRoot
    }

    AfterAll {
        Remove-Folder $script:StatusRoot
        Remove-Item -LiteralPath $script:StatusScratch -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'reports a manifest job and a runtime job together, each enabled by default' {
        # A manifest job: registered the way install.ps1 would, directly at the root (install.ps1
        # itself is covered by install.Tests.ps1; this is only testing the status read path).
        Register-ScheduledTask -TaskPath $script:StatusRoot -TaskName 'Nightly' `
            -Action (New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c exit 0') `
            -Trigger (New-ScheduledTaskTrigger -Daily -At ([datetime]'03:15')) `
            -Description 'Nightly description' | Out-Null
        $manifest = Write-Manifest @(@{ name = 'Nightly'; command = 'cmd.exe /c exit 0'; description = 'Nightly description'; schedule = @{ daily = '03:15' } })

        Add-SchedulerJob -Name 'Ping' -Owner $script:StatusOwner -Every '00:10:00' -Command 'cmd.exe /c exit 0' -Root $script:StatusRoot | Out-Null

        # No @(): Get-AllSchedulerJobs returns its array whole, and @() would nest it (M.SCHD-T41).
        $jobs = Get-AllSchedulerJobs -Root $script:StatusRoot -Manifest $manifest
        $jobs.Count | Should -Be 2
        ($jobs.name | Sort-Object) -join ',' | Should -Be 'Nightly,Ping'
        # --json shape: a flat list, and a one-entry schedule still an array (the dashboard
        # deserializes scheduleRaw as a list; an object there broke every row).
        (ConvertTo-Json -InputObject $jobs -Depth 6 -Compress) | Should -Match '^\[\{'
        (ConvertTo-Json -InputObject $jobs -Depth 6 -Compress) | Should -Not -Match '"scheduleRaw":\{'

        $nightly = $jobs | Where-Object name -eq 'Nightly'
        $nightly.source | Should -Be 'manifest'
        $nightly.owner | Should -BeNullOrEmpty
        $nightly.description | Should -Be 'Nightly description'
        $nightly.scheduleHuman | Should -Be 'daily at 03:15'
        $nightly.enabled | Should -BeTrue

        $ping = $jobs | Where-Object name -eq 'Ping'
        $ping.source | Should -Be 'runtime'
        $ping.owner | Should -Be $script:StatusOwner
        $ping.scheduleHuman | Should -Be 'every 10 minutes'
        $ping.enabled | Should -BeTrue
    }

    It 'reports an auto job with "daily": "auto" in scheduleRaw and its arranged slot in scheduleHuman' {
        Register-ScheduledTask -TaskPath $script:StatusRoot -TaskName 'Arranged' `
            -Action (New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c exit 0') `
            -Trigger (New-ScheduledTaskTrigger -Daily -At ([datetime]'01:15')) | Out-Null
        $manifest = Write-Manifest @(@{ name = 'Arranged'; command = 'cmd.exe /c exit 0'; schedule = @{ daily = 'auto' } })
        $arrangement = Join-Path $script:StatusScratch 'arrangement.json'
        '{"arrangedAt":"2026-11-19T23:30:00-06:00","gapMinutes":5,"fits":true,"slots":{"Arranged":{"start":"01:15","durationMinutes":20,"source":"measured"}}}' |
            Set-Content -LiteralPath $arrangement -Encoding UTF8
        try {
            $jobs = Get-AllSchedulerJobs -Root $script:StatusRoot -Manifest $manifest -ArrangementPath $arrangement
            $arranged = $jobs | Where-Object name -eq 'Arranged'
            $arranged.scheduleHuman | Should -Be 'auto (01:15)'
            @($arranged.scheduleRaw)[0].daily | Should -Be 'auto'
            (ConvertTo-Json -InputObject $jobs -Depth 6 -Compress) | Should -Match '"scheduleRaw":\[\{"daily":"auto"\}\]'

            $none = Get-AllSchedulerJobs -Root $script:StatusRoot -Manifest $manifest -ArrangementPath (Join-Path $script:StatusScratch 'none.json')
            ($none | Where-Object name -eq 'Arranged').scheduleHuman | Should -Be 'auto (not arranged yet)'
        }
        finally { Unregister-ScheduledTask -TaskPath $script:StatusRoot -TaskName 'Arranged' -Confirm:$false -ErrorAction SilentlyContinue }
    }

    It 'disables and re-enables a runtime job, and refuses an unknown one' {
        Disable-SchedulerJob -Name 'Ping' -Owner $script:StatusOwner -Root $script:StatusRoot | Out-Null
        (Get-ScheduledTask -TaskPath $script:StatusOwnerFolder -TaskName 'Ping').Settings.Enabled | Should -BeFalse
        $all = Get-AllSchedulerJobs -Root $script:StatusRoot
        ($all | Where-Object name -eq 'Ping').enabled | Should -BeFalse

        Enable-SchedulerJob -Name 'Ping' -Owner $script:StatusOwner -Root $script:StatusRoot | Out-Null
        (Get-ScheduledTask -TaskPath $script:StatusOwnerFolder -TaskName 'Ping').Settings.Enabled | Should -BeTrue

        { Disable-SchedulerJob -Name 'Nope' -Owner $script:StatusOwner -Root $script:StatusRoot } | Should -Throw '*No job*'
        { Enable-SchedulerJob -Name 'Nope' -Owner $script:StatusOwner -Root $script:StatusRoot } | Should -Throw '*No job*'
    }
}
