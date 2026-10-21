<#
.SYNOPSIS
  Restarts Logitech G HUB's lghub_agent once it has leaked past a memory limit.

.DESCRIPTION
  lghub_agent leaks private memory until it starves the machine. Stopping it is safe:
  lghub_system_tray relaunches it within seconds. Each restart is logged with the size it
  reached. Run hourly by the Scheduler job 'Lghub.LeakGuard' (jobs.json).

.PARAMETER LimitGB
  Restart the agent once its private memory reaches this many GB. Default 2.

.PARAMETER LogPath
  Where restarts are logged. Default %LOCALAPPDATA%\MindAttic\WatchDog\lghub-agent-restarts.log.
#>
[CmdletBinding()]
param(
    [double]$LimitGB = 2,
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'MindAttic\WatchDog\lghub-agent-restarts.log'),
    [string]$ProcessName = 'lghub_agent'
)

function Select-LeakedProcess([object[]]$Process, [double]$LimitGB) {
    # The processes whose private memory has reached the limit.
    @($Process | Where-Object { $_ -and $_.PrivateMemorySize64 -ge [long]($LimitGB * 1GB) })
}

function Format-RestartLine($Process, [datetime]$When) {
    '{0:yyyy-MM-dd HH:mm:ss}  restarted {1} at {2} GB (pid {3})' -f $When, $Process.ProcessName,
        [math]::Round($Process.PrivateMemorySize64 / 1GB, 2), $Process.Id
}

# Dot-sourcing (the tests do) only loads the functions above.
if ($MyInvocation.InvocationName -eq '.') { return }

$leaked = Select-LeakedProcess @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue) $LimitGB
foreach ($p in $leaked) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    $dir = Split-Path $LogPath -Parent
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Add-Content -LiteralPath $LogPath -Value (Format-RestartLine $p (Get-Date))
}
