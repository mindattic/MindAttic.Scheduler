<#
.SYNOPSIS
  Temporary compat shim: MindAttic.WatchDog was renamed to MindAttic.Scheduler (M.WD-T17).
  Forwards to scheduler.ps1; drop this once every caller has moved to `scheduler`.
#>

& (Join-Path $PSScriptRoot 'scheduler.ps1') @args
exit $LASTEXITCODE
