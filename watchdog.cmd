@echo off
rem Runs watchdog.ps1 (watchdog job add / cancel / list). See watchdog.ps1 for usage.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0watchdog.ps1" %*
