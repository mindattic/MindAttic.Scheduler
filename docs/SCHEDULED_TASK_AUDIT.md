# Scheduled-task audit (M.WD-T3)

Every Windows Scheduled Task a MindAttic repo registers, and every MindAttic task found on this
machine (SNOWCRASH) with no repo behind it. Audited 2026-10-18 by grepping every repo under
`D:\Projects\MindAttic` for `schtasks`, `Register-ScheduledTask`, `New-ScheduledTask*`,
`Schedule.Service` and `ITaskService` (hits on .NET's `System.Threading.Tasks.TaskScheduler` were
discarded), then cross-checking `Get-ScheduledTask` on the machine. Each row is a candidate
WatchDog job; the "WatchDog ticket" column names the ticket that moves it.

## Jobs registered by a repo

| Owner repo | Registrar file | Task path | Schedule | Command | Live on machine | WatchDog ticket |
|---|---|---|---|---|---|---|
| Automata | `Automata.Runner/SchTasksRegistrar.cs` (XML from `Automata.Core/Automation/Scheduling/ScheduledTaskXmlBuilder.cs`; run by `automata-runner install [--interval-minutes N]`) | `\MindAttic\Automata\MindAttic.Automata.Tick` (also deletes legacy root `MindAttic.Automata.Logon`) | every 5 min (default `--interval-minutes 5`) + at logon; StartWhenAvailable, IgnoreNew, runs on battery; InteractiveToken | `automata-runner.exe tick` (live: `D:\Projects\MindAttic\Automata\Automata.Runner\bin\Debug\net10.0-windows\automata-runner.exe tick`) | yes, Ready | M.WD-T4 |
| MindAttic.Orchestrator | `src/MindAttic.Orchestrator.Core/Setup/Installer.cs` (`TaskXml`, re-checked and re-registered on every install/launch) | `\MindAttic\Orchestrator\Orchestrator daemon` | at logon, no time limit; InteractiveToken | `C:\Apps\MindAttic.Orchestrator\MindAttic.Orchestrator.exe --daemon` | yes, Running | M.WD-T5 |
| MindAttic.Launcher | `tools/register-nightly-backup-task.ps1` (registers; elevated, S4U); `MindAttic.Launcher/Services/ScheduledTaskService.cs` + `Menus/ScheduledBackupMenu.cs` (query/enable/disable/run/delete via schtasks, register by calling the script) | `\MindAttic\Launcher\MindAtticLauncherNightlyBackup` (removes stale root copy) | daily 00:00 (`-Time`), StartWhenAvailable, hidden; S4U | `C:\Apps\MindAttic.Launcher\MindAttic.Launcher.exe backup --keep 30` | yes, Ready | M.WD-T6 |
| JellyFinPoster | `scripts/register_scheduled_task.ps1` | `\MindAttic\JellyFinPoster\JellyfinPosterRefresh` (removes stale root copy) | daily 00:00 (`-Time`), StartWhenAvailable | `pythonw "D:\Projects\MindAttic\JellyFinPoster\jellyfin_poster.py"` (working dir = repo) | yes, Ready | M.WD-T7 |
| IdiotProof | `tools/register-research-scan-task.ps1` (elevated; publishes `IdiotProof.ResearchScanner` Release first) | `\MindAttic\IdiotProof\IdiotProof Research Scan` (removes stale root copy) | every 60 min (`-IntervalMinutes`), repeating indefinitely from registration; S4U, Limited | `C:\Apps\IdiotProof\ResearchScanner\idiotproof-research-scanner.exe` (working dir same folder) | **no** (never registered here) | M.WD-T8 |
| Unstuck | `UnstuckTaskbar/Startup/StartupRegistrar.cs` (the app's "Start with Windows" toggle) | `\MindAttic\UnstuckTaskbar\Logon` | at logon; InteractiveToken | `C:\Apps\Unstuck\UnstuckTaskbar.exe` | yes, Running | M.WD-T9 |
| MindAttic.Psst | `MindAttic.Psst.Cli/Scheduling/ScheduledTaskRegistrar.cs` (create) + `ScheduledTaskLister.cs` (list/cancel) | `\MindAttic\Psst\MindAttic.Psst.<id>`, one per reminder, created at runtime | once, at the reminder's time (`/SC ONCE /F`, no `/Z`: the launcher `.cmd` deletes its own task and JSON sidecar after firing) | `%LOCALAPPDATA%\MindAttic\Psst\scheduled\<id>.cmd` (generated launcher holding the message) | none pending now; old `<id>.cmd` launchers stay on disk by design | M.WD-T12 (supersedes M.WD-T10) |
| Prose | `scripts/register-autocorrect-task.ps1` | `\MindAttic\Prose\ProseAutoCorrectNightly` | daily 03:00, StartWhenAvailable, 4 h limit, 1 restart | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\run-autocorrect-nightly.ps1` (`dotnet run --project src\Prose.Cli -- --auto-correct-nightly --dry-run`) | **no**: unregistered 2026-09-20 (RFC 0009 forbids unattended prose writes), but the script is still in the repo | none: retired, not migrated; the script should be deleted from Prose |
| MindAttic.Bob (archived, read-only) | `tools/register-nightly-backup-task.ps1` | `\MindAttic\Bob\MindAtticBobNightlyBackup` | daily 00:00 | `powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File tools\nightly-backup.ps1` | **no** | none: superseded by the Launcher nightly backup (M.WD-T6) |

## Scripts that modify other repos' tasks

| Owner repo | File | What it does | Note |
|---|---|---|---|
| Prose | `scripts/fix-task-focus-stealing.ps1` | Elevated; switches `MindAttic.Automata.Tick`'s principal to S4U (or, with `-WrapInConhost`, keeps Interactive and wraps the action in `conhost.exe --headless`) so its console stops stealing focus every 5 min | Becomes moot once WatchDog registers every job headless (M.WD-T2); Prose should drop it after M.WD-T4 lands. Not a job. |

## MindAttic tasks on this machine with no repo

| Task path | Schedule | Command | Origin | WatchDog ticket |
|---|---|---|---|---|
| `\MindAttic LghubWatchdog` (root, breaks HOUSE-LAW-11) | hourly from 2026-10-18 21:00 | `conhost.exe --headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\MindAttic\LghubWatchdog\restart-lghub-agent.ps1"` | Registered by hand from an agent session; the script lives only in `%LOCALAPPDATA%`, in no repo | M.WD-T11 |

Out of scope: the root-level `\Sleep` and `\Wake` tasks run the user's own batch files
(`OneDrive\Documents\Batch Files\sleep.bat`/`wake.bat`), not MindAttic code; everything else
outside `\Microsoft\` belongs to third-party vendors.
