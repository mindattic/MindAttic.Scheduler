---
codex: 1
project: MindAttic.Scheduler
code: M.SCHD
layer: stories
status: living
updated: 2026-10-18
---
<!-- GENERATED from MindAttic.Tickets/data/Tickets.db by `tickets db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the tickets CLI. -->

# MindAttic.Scheduler — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test.

## Priority backlog
_Nothing recorded yet._

## Tickets
> Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
> delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
> a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

| ID | Date | Type | Status | Summary | Ref |
|---|---|---|---|---|---|
| M.SCHD-T1 | 2026-10-18 | docs | closed | Start the permanent ticket log in USER_STORIES.md | — |
| M.SCHD-T2 | 2026-10-18 | feature | closed | Scaffold the scheduled-jobs host: jobs.json manifest (name, command, schedule, replaces) and an idempotent install.ps1 that registers every job under \MindAttic\WatchDog\ (HOUSE-LAW-11) through conhost --headless so none opens a window or steals focus, removes the older copies a job lists under 'replaces', and unregisters WatchDog tasks no longer in the manifest; README; first commit of AGENTS.md and docs/USER_STORIES.md | tests/install.Tests.ps1 (ae85b6d) |
| M.SCHD-T3 | 2026-10-18 | chore | closed | Audit every repo for scheduled tasks and record each as a job (owner repo, registrar file, task path, schedule, command): found so far Automata (SchTasksRegistrar, Tick), MindAttic.Orchestrator (Setup/Installer.cs, daemon), MindAttic.Launcher (ScheduledTaskService + tools/register-nightly-backup-task.ps1), JellyFinPoster (scripts/register_scheduled_task.ps1), IdiotProof (tools/register-research-scan-task.ps1), MindAttic.Psst (Cli/Scheduling per-reminder tasks), Unstuck (UnstuckTaskbar logon task), Prose (scripts/fix-task-focus-stealing.ps1 patches others' tasks); also any task under \MindAttic\ on this machine with no repo | 72f26f7 |
| M.SCHD-T4 | 2026-10-18 | feature | closed | Run Automata's tick as a WatchDog job (replaces \MindAttic\Automata\MindAttic.Automata.Tick: automata-runner.exe tick, same schedule) | 61500f0 |
| M.SCHD-T5 | 2026-10-18 | feature | closed | Run the Orchestrator daemon as a WatchDog job (replaces \MindAttic\Orchestrator\Orchestrator daemon: MindAttic.Orchestrator.exe --daemon at logon, kept running) | ec47857 |
| M.SCHD-T6 | 2026-10-18 | feature | closed | Run the Launcher nightly backup as a WatchDog job (replaces \MindAttic\Launcher\MindAtticLauncherNightlyBackup: MindAttic.Launcher.exe backup --keep 30) | install.Tests.ps1: runs the Launcher nightly backup at midnight keeping 30, replacing the Launcher's own task (eed5c9e) |
| M.SCHD-T7 | 2026-10-18 | feature | closed | Run the Jellyfin poster refresh as a WatchDog job (replaces \MindAttic\JellyFinPoster\JellyfinPosterRefresh: pythonw jellyfin_poster.py) | 59131d6 |
| M.SCHD-T8 | 2026-10-18 | feature | closed | Run IdiotProof's research scan as a WatchDog job (the task tools/register-research-scan-task.ps1 registers by hand, elevated, S4U logon) | 1b1ba49 (tests/install.Tests.ps1: runs IdiotProof's research scan hourly...) |
| M.SCHD-T9 | 2026-10-18 | feature | closed | Run UnstuckTaskbar at logon as a WatchDog job (replaces \MindAttic\UnstuckTaskbar\Logon) | install.Tests.ps1: runs UnstuckTaskbar at logon and keeps it running (73d374e) |
| M.SCHD-T10 | 2026-10-18 | feature | wont-do | Decide how Psst's per-reminder tasks fit WatchDog: they're created and cancelled at runtime per reminder, so either WatchDog exposes a register/cancel API Psst calls, or Psst stays exempt and the exemption is recorded in HOUSE-LAW-11 | replaced by M.WD-T12 |
| M.SCHD-T11 | 2026-10-18 | feature | closed | Move the G HUB leak guard into WatchDog: restart lghub_agent past 2 GB, hourly (replaces the root-level '\MindAttic LghubWatchdog' task, which breaks HOUSE-LAW-11, and its script in %LOCALAPPDATA%\MindAttic\LghubWatchdog) | 14203b6 |
| M.SCHD-T12 | 2026-10-18 | feature | closed | Runtime job API for apps that schedule on the fly: a WatchDog command (watchdog job add <name> --at <time>\|--every <span> --run <command> --owner <app>, watchdog job cancel <name>, watchdog job list --owner <app>) that registers one-off and recurring tasks under \MindAttic\WatchDog\<owner>\ headless, the same way install.ps1 does; first caller is Psst's reminders. Supersedes M.WD-T10's open question | tests/watchdog.Tests.ps1 (212554d) |
| M.SCHD-T13 | 2026-10-19 | chore | wont-do | Convert tracked source files to LF line endings via .gitattributes, per HOUSE-LAW-13 (low priority) | — |
| M.SCHD-T14 | 2026-10-19 | chore | wont-do | Rename WatchDog to Coordinator everywhere: project/repo name MindAttic.WatchDog -> MindAttic.Coordinator, ticket code, README/docs/AGENTS.md, jobs.json comments, install.ps1 and tests, the watchdog CLI command (watchdog job ... -> coordinator job ...), the task folder \MindAttic\WatchDog\ -> \MindAttic\Coordinator\ (install.ps1 migrates/removes the old WatchDog-folder tasks), the %LOCALAPPDATA%\MindAttic\WatchDog log path, and every reference in other repos (Psst's job API calls and README, HOUSE-LAW-11 in MindAttic.HouseRules.md, Orchestrator/workflow registries). The user renames the folder and GitHub repo themselves. | superseded by M.SCHD-T17 (user confirmed Scheduler over Coordinator on 2026-10-20) |
| M.SCHD-T15 | 2026-10-19 | fix | open | The Orchestrator.Daemon job (M.WD-T5) runs the exe from a fixed C:\Apps\MindAttic.Orchestrator path, but the Orchestrator installs to %LOCALAPPDATA%\Programs\MindAttic.Orchestrator when C:\Apps isn't writable; there the daemon never starts at logon (only when the dashboard opens), now that the Orchestrator no longer installs its own scheduled task (M.ORCH-T475). Resolve the installed exe's path instead | — |
| M.SCHD-T16 | 2026-10-20 | docs | open | Publish MindAttic.Scheduler docs to GitHub Pages via the shared Agency DocFX pipeline: run adopt-docs-site.ps1 (README.md as home page, docs/*.md as articles, API reference only where the code builds in CI), enable Pages with build_type=workflow, set the repo homepage URL, retire tools/build-readme.ps1 and README.htm, confirm the docs Actions run is green and https://mindattic.github.io/MindAttic.Scheduler/ returns 200. Depends on MAT-T8. | — |
| M.SCHD-T17 | 2026-10-20 | chore | closed | Rename MindAttic.WatchDog to MindAttic.Scheduler (repo, code, namespaces, docs, callers), the same way MindAttic.Workflow became MindAttic.Tickets (M.TIX-T120) | folder/GitHub repo renamed to MindAttic.Scheduler; install.ps1/scheduler.ps1/README/jobs.json/tests retargeted to \MindAttic\Scheduler\ (3f988ee); live scheduled tasks migrated (Automata.Tick + full jobs.json rollout); Psst's WatchDogClient -> SchedulerClient (9e8035c); HOUSE-LAW-11 updated (00a26b3) |
| M.SCHD-T18 | 2026-10-20 | chore | open | Bug hunt: run an exhaustive /bughunt over the whole repo and fix every real bug found (each fix under its own fix ticket), then hunt again; close this only when a full pass finds nothing | user request 2026-10-20: approved repo sweep gap-fill |
| M.SCHD-T19 | 2026-10-21 | docs | closed | Update USER_STORIES.md | — |
| M.SCHD-T20 | 2026-10-22 | docs | closed | Update USER_STORIES.md | — |
| M.SCHD-T21 | 2026-10-24 | docs | closed | Update USER_STORIES.md | — |
| M.SCHD-T22 | 2026-10-25 | chore | open | Point the Orchestrator.Daemon job (jobs.json, install.Tests.ps1, SCHEDULED_TASK_AUDIT.md) at C:\Apps\MindAttic.Orchestrator\MindAttic.Orchestrator.Daemon.exe once the daemon has its own exe (M.ORCH-T1216); the old path keeps working through the forwarder | — |
| M.SCHD-T23 | 2026-10-25 | fix | open | Start the Orchestrator dashboard by its new name MindAttic.Orchestrator.Desktop.exe (M.ORCH-T844 renamed it; the old MindAttic.Orchestrator.exe is only kept as a legacy fallback) | — |
| M.SCHD-T24 | 2026-10-27 | docs | closed | Update USER_STORIES.md | — |
| M.SCHD-T25 | 2026-11-02 | docs | open | Adopt MindAttic.Agency docs-site pipeline, publish README to GitHub Pages (HOUSE-LAW-14) | — |
| M.SCHD-T26 | 2026-11-02 | fix | closed | Bughunt sweep, two passes: find and fix real bugs across tracked source (user request 2026-11-02) | — |
| M.SCHD-T27 | 2026-11-02 | fix | closed | Fix the open items the 2026-11-02 bughunt left for decision (missing jobs key, replace guard subtree, bare-name GUI exes via PATH) | — |
| M.SCHD-T28 | 2026-11-05 | feature | open | Job: when free space on C: drops below 20 GB, clean out old temp data (user/Windows temp and other safe caches, oldest first, only items older than a cutoff and not in use; never live agent scratchpads; log what was freed) | user request 2026-11-05 |
| M.SCHD-T29 | 2026-11-10 | chore | closed | Crash recovery: commit the command-catalog sync (.agent/commands/*.md) and regenerated docs/USER_STORIES.md left uncommitted by the session crash | — |
