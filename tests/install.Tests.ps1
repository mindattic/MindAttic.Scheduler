# Pester tests for install.ps1. Run: Invoke-Pester .\tests
# The integration block registers real tasks, but only under a scratch folder
# (\MindAttic\Scheduler\Tests\), and deletes them afterwards. No elevation needed.

BeforeAll {
    . (Join-Path $PSScriptRoot '..\install.ps1')
    $script:Scratch = Join-Path ([IO.Path]::GetTempPath()) ('scheduler-tests-' + [guid]::NewGuid().ToString('N'))
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

    It 'runs the Launcher nightly backup at an arranged overnight slot keeping 30, replacing the Launcher''s own task' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'Launcher.NightlyBackup'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Match 'MindAttic\.Launcher\.exe" backup --keep 30$'
        @($job.Schedule).Count | Should -Be 1
        @($job.Schedule)[0].daily | Should -Be 'auto'
        $job.IsAuto | Should -BeTrue
        $replaced = $job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }
        $replaced | Should -Contain '\MindAttic\Launcher\MindAtticLauncherNightlyBackup'
        $replaced | Should -Contain '\MindAtticLauncherNightlyBackup'
    }

    It 'runs IdiotProof''s research scan hourly, capped at 30 minutes, replacing IdiotProof''s own task and its stale root copy' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'IdiotProof.ResearchScan'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Be 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\Projects\MindAttic\IdiotProof\tools\scheduled-job.ps1" -Job IdiotProof.ResearchScan -Run "research scan"'
        $job.WorkingDirectory | Should -Be 'C:\Apps\IdiotProof\Cli'
        @($job.Schedule).Count | Should -Be 1
        @($job.Schedule)[0].every | Should -Be '01:00:00'
        $job.TimeLimit | Should -Be ([TimeSpan]::FromMinutes(30))
        $replaced = $job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }
        $replaced | Should -Contain '\MindAttic\IdiotProof\IdiotProof Research Scan'
        $replaced | Should -Contain '\IdiotProof Research Scan'
    }

    It 'refreshes Jellyfin posters nightly at an arranged slot from the repo folder, replacing JellyFinPoster''s own task' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        $job = $jobs | Where-Object Name -eq 'JellyFinPoster.Refresh'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Match '^pythonw "[^"]+\\jellyfin_poster\.py"$'
        $job.WorkingDirectory | Should -Be 'D:\Projects\MindAttic\JellyFinPoster'
        @($job.Schedule | Where-Object { $_.daily -eq 'auto' }).Count | Should -Be 1
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
        $job.Command | Should -Be 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\Projects\MindAttic\MindAttic.Scheduler\scripts\restart-lghub-agent.ps1" -LimitGB 2'
        $job.Command | Should -Not -Match 'LOCALAPPDATA|AppData'
        Test-Path (Join-Path $PSScriptRoot '..\scripts\restart-lghub-agent.ps1') | Should -BeTrue
        @($job.Schedule).Count | Should -Be 1
        @($job.Schedule)[0].every | Should -Be '01:00:00'
        $job.TimeLimit | Should -Be ([TimeSpan]::FromMinutes(2))
        ($job.Replaces | ForEach-Object { "$($_.Path)$($_.Name)" }) | Should -Contain '\MindAttic LghubWatchdog'
    }

    It 'arranges the overnight jobs (M.SCHD-T42), each with an estimate' {
        $jobs = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json')
        foreach ($name in 'Launcher.NightlyBackup', 'JellyFinPoster.Refresh', 'Azure.BackupNightly',
                          'GridGame.Nightly', 'IdiotProof.DataMirror', 'Azure.BackupWeekly') {
            $job = $jobs | Where-Object Name -eq $name
            $job | Should -Not -BeNullOrEmpty -Because $name
            $job.IsAuto | Should -BeTrue -Because $name
            $job.Estimate | Should -BeGreaterThan ([TimeSpan]::Zero) -Because $name
        }
    }

    It 'leaves the GLMZ wiki sync a resident watcher, not an arranged job' {
        $job = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json') | Where-Object Name -eq 'Prose.GLMZ.Wiki.Sync'
        $job.IsAuto | Should -BeFalse
        $job.KeepRunning | Should -BeTrue
        @($job.Schedule)[0].atLogon | Should -BeTrue
    }

    It 're-arranges the night at a pinned 23:30, capped at 30 minutes' {
        $job = Read-JobManifest (Join-Path $PSScriptRoot '..\jobs.json') | Where-Object Name -eq 'Scheduler.Arrange'
        $job | Should -Not -BeNullOrEmpty
        $job.Command | Should -Be '"C:\Apps\MindAttic.Scheduler\mindattic-scheduler.exe" arrange'
        $job.IsAuto | Should -BeFalse
        @($job.Schedule)[0].daily | Should -Be '23:30'
        $job.TimeLimit | Should -Be ([TimeSpan]::FromMinutes(30))
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

    It 'defaults enabled to true and accepts an explicit false' {
        $m = Write-Manifest @(
            @{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true } },
            @{ name = 'B'; command = 'b'; schedule = @{ atLogon = $true }; enabled = $false }
        )
        $jobs = Read-JobManifest $m
        ($jobs | Where-Object Name -eq 'A').Enabled | Should -BeTrue
        ($jobs | Where-Object Name -eq 'B').Enabled | Should -BeFalse
    }

    It 'accepts daysOfWeek on a daily entry' {
        $m = Write-Manifest @(@{ name = 'A'; command = 'a'; schedule = @{ daily = '09:00'; daysOfWeek = @('Mon', 'Tue', 'Wed', 'Thu', 'Fri') } })
        (Read-JobManifest $m)[0].Schedule[0].daysOfWeek | Should -Be @('Mon', 'Tue', 'Wed', 'Thu', 'Fri')
    }

    It 'accepts "daily": "auto" in any case, with or without daysOfWeek, and flags the job IsAuto' {
        $m = Write-Manifest @(
            @{ name = 'A'; command = 'a'; schedule = @{ daily = 'auto' } },
            @{ name = 'B'; command = 'b'; schedule = @{ daily = 'AUTO'; daysOfWeek = @('Sat', 'Sun') } },
            @{ name = 'C'; command = 'c'; schedule = @{ daily = '01:00' } }
        )
        $jobs = Read-JobManifest $m
        ($jobs | Where-Object Name -eq 'A').IsAuto | Should -BeTrue
        ($jobs | Where-Object Name -eq 'B').IsAuto | Should -BeTrue
        ($jobs | Where-Object Name -eq 'C').IsAuto | Should -BeFalse
    }

    It 'reads an optional estimate as a time span' {
        $m = Write-Manifest @(
            @{ name = 'A'; command = 'a'; schedule = @{ daily = 'auto' }; estimate = '00:20:00' },
            @{ name = 'B'; command = 'b'; schedule = @{ daily = 'auto' } }
        )
        $jobs = Read-JobManifest $m
        ($jobs | Where-Object Name -eq 'A').Estimate | Should -Be ([TimeSpan]::FromMinutes(20))
        ($jobs | Where-Object Name -eq 'B').Estimate | Should -BeNullOrEmpty
    }

    It 'rejects <case>' -ForEach @(
        @{ case = 'a duplicate name';      jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true } }, @{ name = 'a'; command = 'b'; schedule = @{ atLogon = $true } }) }
        @{ case = 'a missing command';     jobs = @(@{ name = 'A'; schedule = @{ atLogon = $true } }) }
        @{ case = 'a missing schedule';    jobs = @(@{ name = 'A'; command = 'a' }) }
        @{ case = 'two kinds in one entry'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true; daily = '01:00' } }) }
        @{ case = 'a bad daily time';      jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ daily = '25:00' } }) }
        @{ case = 'a sub-minute interval'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ every = '00:00:30' } }) }
        @{ case = 'a backslash in a name'; jobs = @(@{ name = 'A\B'; command = 'a'; schedule = @{ atLogon = $true } }) }
        @{ case = 'replacing a Scheduler task'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true }; replaces = @('\MindAttic\Scheduler\Other') }) }
        @{ case = 'keepRunning with a timeLimit'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true }; keepRunning = $true; timeLimit = '01:00:00' }) }
        @{ case = 'a non-boolean keepRunning'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true }; keepRunning = 'yes' }) }
        @{ case = 'a non-boolean enabled'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true }; enabled = 'yes' }) }
        @{ case = 'daysOfWeek on an every entry'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ every = '00:05:00'; daysOfWeek = @('Mon') } }) }
        @{ case = 'daysOfWeek on an atLogon entry'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ atLogon = $true; daysOfWeek = @('Mon') } }) }
        @{ case = 'an empty daysOfWeek list'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ daily = '09:00'; daysOfWeek = @() } }) }
        @{ case = 'an unrecognised day name'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ daily = '09:00'; daysOfWeek = @('Someday') } }) }
        @{ case = 'a daily word other than auto'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ daily = 'automatic' } }) }
        @{ case = 'auto on an every entry';  jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ every = 'auto' } }) }
        @{ case = 'two auto entries in one job'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @(@{ daily = 'auto' }, @{ daily = 'auto'; daysOfWeek = @('Sun') }) }) }
        @{ case = 'a keepRunning auto job';  jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ daily = 'auto' }; keepRunning = $true }) }
        @{ case = 'an estimate that isn''t a time span'; jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ daily = 'auto' }; estimate = 'soon' }) }
        @{ case = 'a zero estimate';         jobs = @(@{ name = 'A'; command = 'a'; schedule = @{ daily = 'auto' }; estimate = '00:00:00' }) }
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

Describe 'New-JobAction run wrapper (M.SCHD-T42)' {
    BeforeAll {
        # Only its existence matters to install.ps1; it is never run here.
        $script:Cli = Join-Path $script:Scratch 'cli\mindattic-scheduler.exe'
        New-Item -ItemType Directory -Path (Split-Path $script:Cli) -Force | Out-Null
        Set-Content -LiteralPath $script:Cli -Value '' -Encoding ASCII
        $script:ConsoleCmd = "`"$env:SystemRoot\System32\cmd.exe`" /c exit 0"
        function New-TestJob([string]$Name, [string]$Command, [bool]$IsAuto = $false, [bool]$KeepRunning = $false) {
            [pscustomobject]@{ Name = $Name; Command = $Command; WorkingDirectory = $null; IsAuto = $IsAuto; KeepRunning = $KeepRunning }
        }
    }

    It 'wraps a console job in the run wrapper, without the overnight lock when it is pinned' {
        $a = New-JobAction (New-TestJob 'Nightly' $script:ConsoleCmd) -CliPath $script:Cli
        $a.Execute | Should -Match 'conhost\.exe$'
        $a.Arguments | Should -Be "--headless `"$($script:Cli)`" run Nightly -- $($script:ConsoleCmd)"
    }

    It 'adds --exclusive for an auto job' {
        $a = New-JobAction (New-TestJob 'Backup' $script:ConsoleCmd -IsAuto $true) -CliPath $script:Cli
        $a.Arguments | Should -Be "--headless `"$($script:Cli)`" run Backup --exclusive -- $($script:ConsoleCmd)"
    }

    It 'quotes a job name with a space in it' {
        $a = New-JobAction (New-TestJob 'Two Words' $script:ConsoleCmd) -CliPath $script:Cli
        $a.Arguments | Should -BeLike "*run `"Two Words`" -- *"
    }

    It 'starts a GUI program directly, unwrapped, even when it is auto' {
        $exe = "$env:SystemRoot\System32\charmap.exe"
        $a = New-JobAction (New-TestJob 'Gui' "`"$exe`" --x" -IsAuto $true) -CliPath $script:Cli
        $a.Execute | Should -Be $exe
        $a.Arguments | Should -Be '--x'
    }

    It 'leaves a keepRunning daemon on the plain conhost action' {
        $a = New-JobAction (New-TestJob 'Daemon' $script:ConsoleCmd -KeepRunning $true) -CliPath $script:Cli
        $a.Arguments | Should -Be "--headless $($script:ConsoleCmd)"
    }

    It 'leaves a runtime job (no -CliPath) unwrapped' {
        $a = New-JobAction ([pscustomobject]@{ Command = $script:ConsoleCmd; WorkingDirectory = $null })
        $a.Arguments | Should -Be "--headless $($script:ConsoleCmd)"
    }

    It 'falls back to the unwrapped action, warning once, when the CLI isn''t installed' {
        # A path no earlier test used: the warning is once per CLI path.
        $missing = Join-Path $script:Scratch ([guid]::NewGuid().ToString('N') + '\mindattic-scheduler.exe')
        $a = New-JobAction (New-TestJob 'One' $script:ConsoleCmd -IsAuto $true) -CliPath $missing -WarningVariable w1 -WarningAction SilentlyContinue
        $b = New-JobAction (New-TestJob 'Two' $script:ConsoleCmd) -CliPath $missing -WarningVariable w2 -WarningAction SilentlyContinue
        $a.Arguments | Should -Be "--headless $($script:ConsoleCmd)"
        $b.Arguments | Should -Be "--headless $($script:ConsoleCmd)"
        @($w1).Count + @($w2).Count | Should -Be 1
    }
}

Describe 'Arranged slots (M.SCHD-T42)' {
    BeforeAll {
        $script:NoCli = Join-Path $script:Scratch 'nowhere\mindattic-scheduler.exe'
        function Write-Arrangement([hashtable]$Starts) {
            $slots = [ordered]@{}
            foreach ($k in $Starts.Keys) { $slots[$k] = [ordered]@{ start = $Starts[$k]; durationMinutes = 10; source = 'estimate' } }
            $path = Join-Path $script:Scratch ([guid]::NewGuid().ToString('N') + '.arrangement.json')
            ConvertTo-Json -InputObject ([ordered]@{ arrangedAt = '2026-11-19T23:30:00-06:00'; gapMinutes = 5; fits = $true; slots = $slots }) -Depth 5 |
                Set-Content -LiteralPath $path -Encoding UTF8
            $path
        }
        function Read-TestJobs([object[]]$Jobs) { Read-JobManifest (Write-Manifest $Jobs) }
    }

    It 'reads each slot''s start from arrangement.json' {
        $path = Write-Arrangement @{ Backup = '01:15'; Mirror = '02:40' }
        $slots = Read-Arrangement $path
        $slots['Backup'] | Should -Be '01:15'
        $slots['Mirror'] | Should -Be '02:40'
    }

    It 'resolves an auto entry to its arranged slot' {
        $path = Write-Arrangement @{ Backup = '01:15' }
        $jobs = Read-TestJobs @(@{ name = 'Backup'; command = 'cmd.exe /c exit 0'; schedule = @{ daily = 'auto' } })
        $slots = Resolve-AutoSlots -Jobs $jobs -ArrangementPath $path -CliPath $script:NoCli -WarningVariable w -WarningAction SilentlyContinue
        $slots['Backup'] | Should -Be '01:15'
        @($w).Count | Should -Be 0

        $t = @(New-JobTriggers -Job $jobs[0] -Slots $slots)
        $t.Count | Should -Be 1
        ([datetime]$t[0].StartBoundary).ToString('HH:mm') | Should -Be '01:15'
    }

    It 'resolves the slot itself when New-JobTriggers is given only -ArrangementPath' {
        $path = Write-Arrangement @{ Backup = '03:05' }
        $jobs = Read-TestJobs @(@{ name = 'Backup'; command = 'cmd.exe /c exit 0'; schedule = @{ daily = 'auto' } })
        $t = @(New-JobTriggers -Job $jobs[0] -ArrangementPath $path -CliPath $script:NoCli)
        ([datetime]$t[0].StartBoundary).ToString('HH:mm') | Should -Be '03:05'
    }

    It 'keeps daysOfWeek on an auto entry: a weekly trigger at the arranged slot' {
        $path = Write-Arrangement @{ Weekend = '04:00' }
        $jobs = Read-TestJobs @(@{ name = 'Weekend'; command = 'cmd.exe /c exit 0'; schedule = @{ daily = 'auto'; daysOfWeek = @('Sat', 'Sun') } })
        $t = @(New-JobTriggers -Job $jobs[0] -Slots (Resolve-AutoSlots -Jobs $jobs -ArrangementPath $path -CliPath $script:NoCli))
        $t[0].CimClass.CimClassName | Should -Be 'MSFT_TaskWeeklyTrigger'
        ([datetime]$t[0].StartBoundary).ToString('HH:mm') | Should -Be '04:00'
    }

    It 'falls back to placeholders 7 minutes apart from 02:00, with a warning, when there is no slot and no CLI' {
        $missingFile = Join-Path $script:Scratch 'no-such-arrangement.json'
        $jobs = Read-TestJobs @(
            @{ name = 'First'; command = 'a'; schedule = @{ daily = 'auto' } },
            @{ name = 'Pinned'; command = 'b'; schedule = @{ daily = '05:00' } },
            @{ name = 'Second'; command = 'c'; schedule = @{ daily = 'auto' } }
        )
        $slots = Resolve-AutoSlots -Jobs $jobs -ArrangementPath $missingFile -CliPath $script:NoCli -WarningVariable w -WarningAction SilentlyContinue
        $slots['First'] | Should -Be '02:00'
        $slots['Second'] | Should -Be '02:07'
        $slots.ContainsKey('Pinned') | Should -BeFalse
        @($w).Count | Should -Be 2
    }

    It 'uses a placeholder only for the job the arrangement lacks, and for a slot that isn''t HH:mm' {
        $path = Write-Arrangement @{ Known = '00:30'; Broken = '25:99' }
        $jobs = Read-TestJobs @(
            @{ name = 'Known'; command = 'a'; schedule = @{ daily = 'auto' } },
            @{ name = 'Broken'; command = 'b'; schedule = @{ daily = 'auto' } },
            @{ name = 'New'; command = 'c'; schedule = @{ daily = 'auto' } }
        )
        $slots = Resolve-AutoSlots -Jobs $jobs -ArrangementPath $path -CliPath $script:NoCli -WarningAction SilentlyContinue
        $slots['Known'] | Should -Be '00:30'
        $slots['Broken'] | Should -Be '02:00'
        $slots['New'] | Should -Be '02:07'
    }

    It 'never throws on an unreadable arrangement file' {
        $path = Join-Path $script:Scratch 'garbage.arrangement.json'
        Set-Content -LiteralPath $path -Value '{ not json' -Encoding ASCII
        $jobs = Read-TestJobs @(@{ name = 'A'; command = 'a'; schedule = @{ daily = 'auto' } })
        { Resolve-AutoSlots -Jobs $jobs -ArrangementPath $path -CliPath $script:NoCli -WarningAction SilentlyContinue } | Should -Not -Throw
        (Resolve-AutoSlots -Jobs $jobs -ArrangementPath $path -CliPath $script:NoCli -WarningAction SilentlyContinue)['A'] | Should -Be '02:00'
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

Describe 'Install-SchedulerJobs against the real Task Scheduler' {
    BeforeAll {
        $script:Folder = '\MindAttic\Scheduler\Tests\'
        $script:OldFolder = '\MindAttic\SchedulerTestsOld\'
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

        # No CLI: this test is about the plain conhost action, whether or not the real run
        # wrapper is installed on this machine.
        $noCli = Join-Path $script:Scratch 'absent\mindattic-scheduler.exe'
        Install-SchedulerJobs -Manifest $m -TaskPath $script:Folder -CliPath $noCli -WarningAction SilentlyContinue | Out-Null
        Install-SchedulerJobs -Manifest $m -TaskPath $script:Folder -CliPath $noCli -WarningAction SilentlyContinue | Out-Null   # second run: no change, no error

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
            Install-SchedulerJobs -Manifest $m -TaskPath $script:Folder | Out-Null

            (Get-Tasks $script:Folder).TaskName | Should -Be 'Tick'
            (Get-Tasks $sub).TaskName | Should -Be 'Runtime'
        }
        finally { Remove-Folder $sub }
    }

    It 'keeps a keepRunning job running: no time limit, restarted on failure, started on install' {
        $m = Write-Manifest @(@{ name = 'Daemon'; command = 'cmd.exe /c ping -n 120 127.0.0.1'; schedule = @{ atLogon = $true }; keepRunning = $true })
        try {
            $out = Install-SchedulerJobs -Manifest $m -TaskPath $script:Folder
            $out | Should -Contain "  started $($script:Folder)Daemon"
            $d = Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Daemon'
            $d.State | Should -Be 'Running'
            $d.Settings.ExecutionTimeLimit | Should -Be 'PT0S'
            $d.Settings.RestartCount | Should -Be 999
            $d.Settings.RestartInterval | Should -Be 'PT1M'
            # Rerunning leaves the running instance alone.
            Install-SchedulerJobs -Manifest $m -TaskPath $script:Folder | Should -Not -Contain "  started $($script:Folder)Daemon"
        }
        finally {
            Stop-ScheduledTask -TaskPath $script:Folder -TaskName 'Daemon' -ErrorAction SilentlyContinue
            # Put the folder back the way the -WhatIf test expects it.
            Install-SchedulerJobs -Manifest (Write-Manifest @(@{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } })) -TaskPath $script:Folder | Out-Null
        }
    }

    It 'registers a disabled job disabled, and reinstalling preserves both states' {
        try {
            $disabled = Write-Manifest @(
                @{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } },
                @{ name = 'Nightly'; command = $script:Cmd; schedule = @{ daily = '03:15' }; enabled = $false }
            )
            Install-SchedulerJobs -Manifest $disabled -TaskPath $script:Folder | Out-Null
            (Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Tick').Settings.Enabled | Should -BeTrue
            (Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Nightly').Settings.Enabled | Should -BeFalse

            # Rerunning must not silently re-enable it.
            Install-SchedulerJobs -Manifest $disabled -TaskPath $script:Folder | Out-Null
            (Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Nightly').Settings.Enabled | Should -BeFalse

            # Flipping the manifest back to enabled must re-enable it.
            $enabled = Write-Manifest @(
                @{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } },
                @{ name = 'Nightly'; command = $script:Cmd; schedule = @{ daily = '03:15' } }
            )
            Install-SchedulerJobs -Manifest $enabled -TaskPath $script:Folder | Out-Null
            (Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Nightly').Settings.Enabled | Should -BeTrue
        }
        finally {
            Install-SchedulerJobs -Manifest (Write-Manifest @(@{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } })) -TaskPath $script:Folder | Out-Null
        }
    }

    It 'registers a daily schedule with daysOfWeek as a weekly trigger' {
        try {
            $m = Write-Manifest @(
                @{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } },
                @{ name = 'Weekdays'; command = $script:Cmd; schedule = @{ daily = '09:00'; daysOfWeek = @('Mon', 'Tue', 'Wed', 'Thu', 'Fri') } }
            )
            Install-SchedulerJobs -Manifest $m -TaskPath $script:Folder | Out-Null
            $t = Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Weekdays'
            $t.Triggers[0].CimClass.CimClassName | Should -Be 'MSFT_TaskWeeklyTrigger'
            $t.Triggers[0].DaysOfWeek | Should -Be 62   # Mon(2)+Tue(4)+Wed(8)+Thu(16)+Fri(32)
            # Stored as local time, no 'Z'/offset, so the job doesn't drift an hour at DST
            # (registration converts New-ScheduledTaskTrigger's UTC start to local; M.SCHD-T41).
            $t.Triggers[0].StartBoundary | Should -Match '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$'
        }
        finally {
            Install-SchedulerJobs -Manifest (Write-Manifest @(@{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } })) -TaskPath $script:Folder | Out-Null
        }
    }

    It 'registers an auto job at its arranged slot through the run wrapper, with the overnight lock' {
        $cli = Join-Path $script:Scratch 'installcli\mindattic-scheduler.exe'
        New-Item -ItemType Directory -Path (Split-Path $cli) -Force | Out-Null
        Set-Content -LiteralPath $cli -Value '' -Encoding ASCII
        $arrangement = Join-Path $script:Scratch 'install.arrangement.json'
        '{"arrangedAt":"2026-11-19T23:30:00-06:00","gapMinutes":5,"fits":true,"slots":{"Overnight":{"start":"01:45","durationMinutes":12.5,"source":"estimate"}}}' |
            Set-Content -LiteralPath $arrangement -Encoding UTF8
        try {
            $m = Write-Manifest @(
                @{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } },
                @{ name = 'Overnight'; command = $script:Cmd; schedule = @{ daily = 'auto' }; estimate = '00:10:00' }
            )
            $out = Install-SchedulerJobs -Manifest $m -TaskPath $script:Folder -ArrangementPath $arrangement -CliPath $cli
            $out | Should -Contain "  registered $($script:Folder)Overnight (auto, 01:45)"

            $o = Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Overnight'
            ([datetime]$o.Triggers[0].StartBoundary).ToString('HH:mm') | Should -Be '01:45'
            $o.Actions[0].Execute | Should -Match 'conhost\.exe$'
            $o.Actions[0].Arguments | Should -Be "--headless `"$cli`" run Overnight --exclusive -- $($script:Cmd)"

            $tick = Get-ScheduledTask -TaskPath $script:Folder -TaskName 'Tick'
            $tick.Actions[0].Arguments | Should -Be "--headless `"$cli`" run Tick -- $($script:Cmd)"
        }
        finally {
            Install-SchedulerJobs -Manifest (Write-Manifest @(@{ name = 'Tick'; command = $script:Cmd; schedule = @{ every = '00:05:00' } })) -TaskPath $script:Folder | Out-Null
        }
    }

    It 'changes nothing under -WhatIf' {
        $m = Write-Manifest @()
        Install-SchedulerJobs -Manifest $m -TaskPath $script:Folder -WhatIf | Out-Null
        (Get-Tasks $script:Folder).TaskName | Should -Be 'Tick'
    }
}
