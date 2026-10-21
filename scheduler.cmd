@echo off
rem Runs scheduler.ps1 (scheduler job add / cancel / list). See scheduler.ps1 for usage.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scheduler.ps1" %*
