<#
.SYNOPSIS
  Publishes the Scheduler jobs dashboard (M.SCHD-T38) to C:\Apps\MindAttic.Scheduler, self-contained
  single-file, the same way MindAttic.Orchestrator's own release.ps1 publishes its desktop app.

.DESCRIPTION
  This is a single-machine ops tool, not something distributed to other PCs (unlike Orchestrator's
  release.ps1, there's no zip, no GitHub release): just build the exe and drop it where
  C:\Apps\MindAttic.Scheduler\MindAttic.Scheduler.Desktop.exe can be launched from.

.EXAMPLE
  .\release.ps1
#>
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\MindAttic.Scheduler.Wpf\MindAttic.Scheduler.Wpf.csproj'
$output = 'C:\Apps\MindAttic.Scheduler'

Write-Host "Publishing MindAttic.Scheduler.Wpf -> $output" -ForegroundColor Cyan
dotnet publish $project --configuration Release --output $output `
    --runtime win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Publishing MindAttic.Scheduler.Wpf failed.' }

Write-Host "Done: $output\MindAttic.Scheduler.Desktop.exe" -ForegroundColor Green
