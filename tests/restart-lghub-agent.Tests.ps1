# Pester tests for scripts\restart-lghub-agent.ps1 (the Lghub.LeakGuard job). Run: Invoke-Pester .\tests

BeforeAll {
    $script:Guard = Join-Path $PSScriptRoot '..\scripts\restart-lghub-agent.ps1'
    . $script:Guard
    function New-FakeProcess([double]$GB, [int]$Id = 42) {
        [pscustomobject]@{ ProcessName = 'lghub_agent'; Id = $Id; PrivateMemorySize64 = [long]($GB * 1GB) }
    }
}

Describe 'Select-LeakedProcess' {
    It 'picks only the agents at or past the limit' {
        $picked = Select-LeakedProcess @((New-FakeProcess 1.99 1), (New-FakeProcess 2 2), (New-FakeProcess 3.5 3)) 2
        @($picked).Id | Should -Be @(2, 3)
    }

    It 'picks nothing when no agent is running' {
        @(Select-LeakedProcess @() 2).Count | Should -Be 0
    }
}

Describe 'Format-RestartLine' {
    It 'logs the time, size and pid' {
        Format-RestartLine (New-FakeProcess 2.346 1234) ([datetime]'2026-10-19 13:05:09') |
            Should -Be '2026-10-19 13:05:09  restarted lghub_agent at 2.35 GB (pid 1234)'
    }
}

Describe 'restart-lghub-agent.ps1' {
    It 'leaves agents under the limit running and logs nothing' {
        $log = Join-Path ([IO.Path]::GetTempPath()) ('lghub-guard-' + [guid]::NewGuid().ToString('N') + '\restarts.log')
        # A process name that can't exist, so nothing real is ever stopped.
        & $script:Guard -ProcessName ('no-such-process-' + [guid]::NewGuid().ToString('N')) -LogPath $log
        Test-Path -LiteralPath $log | Should -BeFalse
    }
}
