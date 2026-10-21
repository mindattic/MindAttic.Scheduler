@echo off
rem Temporary compat shim: MindAttic.WatchDog was renamed to MindAttic.Scheduler (M.WD-T17).
rem Forwards to scheduler.cmd; drop this once every caller has moved to `scheduler`.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scheduler.ps1" %*
