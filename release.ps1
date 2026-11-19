<#
.SYNOPSIS
  Publishes the Scheduler jobs dashboard (M.SCHD-T38) and the mindattic-scheduler CLI (M.SCHD-T42)
  to C:\Apps\MindAttic.Scheduler, self-contained single-file, the same way MindAttic.Orchestrator's
  own release.ps1 publishes its desktop app.

.DESCRIPTION
  This is a single-machine ops tool, not something distributed to other PCs (unlike Orchestrator's
  release.ps1, there's no zip, no GitHub release): just build the exes and drop them where
  C:\Apps\MindAttic.Scheduler\MindAttic.Scheduler.Desktop.exe can be launched from, and where
  install.ps1 finds C:\Apps\MindAttic.Scheduler\mindattic-scheduler.exe for the run wrapper.

  A job running through the wrapper at publish time holds mindattic-scheduler.exe open; the publish
  then fails on the locked file. Run it again once the job is done.

.EXAMPLE
  .\release.ps1
#>
$ErrorActionPreference = 'Stop'

$output = 'C:\Apps\MindAttic.Scheduler'
$projects = @(
    @{ Name = 'MindAttic.Scheduler.Wpf'; Path = 'src\MindAttic.Scheduler.Wpf\MindAttic.Scheduler.Wpf.csproj'; Exe = 'MindAttic.Scheduler.Desktop.exe' }
    @{ Name = 'MindAttic.Scheduler.Cli'; Path = 'src\MindAttic.Scheduler.Cli\MindAttic.Scheduler.Cli.csproj'; Exe = 'mindattic-scheduler.exe' }
)

foreach ($p in $projects) {
    $project = Join-Path $PSScriptRoot $p.Path
    Write-Host "Publishing $($p.Name) -> $output" -ForegroundColor Cyan
    dotnet publish $project --configuration Release --output $output `
        --runtime win-x64 --self-contained true `
        /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publishing $($p.Name) failed." }
    Write-Host "Done: $output\$($p.Exe)" -ForegroundColor Green
}
