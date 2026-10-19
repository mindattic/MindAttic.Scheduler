---
codex: 1
project: MindAttic.Watchdog
code: M.WD
layer: stories
status: living
updated: 2026-10-18
---
<!-- GENERATED from MindAttic.Workflow/data/workflow.db by `workflow db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the workflow CLI. -->

# MindAttic.Watchdog — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test.

## Priority backlog
_Nothing recorded yet._

## Tickets
> Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
> delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
> a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

| ID | Date | Type | Status | Summary | Ref |
|---|---|---|---|---|---|
| M.WD-T1 | 2026-10-18 | docs | closed | Start the permanent ticket log in USER_STORIES.md | — |
| M.WD-T2 | 2026-10-18 | feature | open | Scaffold the scheduled-jobs host: jobs.json manifest (name, command, schedule, replaces) and an idempotent install.ps1 that registers every job under \MindAttic\WatchDog\ (HOUSE-LAW-11) through conhost --headless so none opens a window or steals focus, removes the older copies a job lists under 'replaces', and unregisters WatchDog tasks no longer in the manifest; README; first commit of AGENTS.md and docs/USER_STORIES.md | scheduled-task consolidation 2026-10-18 |
| M.WD-T3 | 2026-10-18 | chore | open | Audit every repo for scheduled tasks and record each as a job (owner repo, registrar file, task path, schedule, command): found so far Automata (SchTasksRegistrar, Tick), MindAttic.Orchestrator (Setup/Installer.cs, daemon), MindAttic.Launcher (ScheduledTaskService + tools/register-nightly-backup-task.ps1), JellyFinPoster (scripts/register_scheduled_task.ps1), IdiotProof (tools/register-research-scan-task.ps1), MindAttic.Psst (Cli/Scheduling per-reminder tasks), Unstuck (UnstuckTaskbar logon task), Prose (scripts/fix-task-focus-stealing.ps1 patches others' tasks); also any task under \MindAttic\ on this machine with no repo | scheduled-task consolidation 2026-10-18 |
| M.WD-T4 | 2026-10-18 | feature | open | Run Automata's tick as a WatchDog job (replaces \MindAttic\Automata\MindAttic.Automata.Tick: automata-runner.exe tick, same schedule) | scheduled-task consolidation 2026-10-18 |
| M.WD-T5 | 2026-10-18 | feature | open | Run the Orchestrator daemon as a WatchDog job (replaces \MindAttic\Orchestrator\Orchestrator daemon: MindAttic.Orchestrator.exe --daemon at logon, kept running) | scheduled-task consolidation 2026-10-18 |
| M.WD-T6 | 2026-10-18 | feature | open | Run the Launcher nightly backup as a WatchDog job (replaces \MindAttic\Launcher\MindAtticLauncherNightlyBackup: MindAttic.Launcher.exe backup --keep 30) | scheduled-task consolidation 2026-10-18 |
| M.WD-T7 | 2026-10-18 | feature | open | Run the Jellyfin poster refresh as a WatchDog job (replaces \MindAttic\JellyFinPoster\JellyfinPosterRefresh: pythonw jellyfin_poster.py) | scheduled-task consolidation 2026-10-18 |
| M.WD-T8 | 2026-10-18 | feature | open | Run IdiotProof's research scan as a WatchDog job (the task tools/register-research-scan-task.ps1 registers by hand, elevated, S4U logon) | scheduled-task consolidation 2026-10-18 |
| M.WD-T9 | 2026-10-18 | feature | open | Run UnstuckTaskbar at logon as a WatchDog job (replaces \MindAttic\UnstuckTaskbar\Logon) | scheduled-task consolidation 2026-10-18 |
| M.WD-T10 | 2026-10-18 | feature | open | Decide how Psst's per-reminder tasks fit WatchDog: they're created and cancelled at runtime per reminder, so either WatchDog exposes a register/cancel API Psst calls, or Psst stays exempt and the exemption is recorded in HOUSE-LAW-11 | scheduled-task consolidation 2026-10-18 |
| M.WD-T11 | 2026-10-18 | feature | open | Move the G HUB leak guard into WatchDog: restart lghub_agent past 2 GB, hourly (replaces the root-level '\MindAttic LghubWatchdog' task, which breaks HOUSE-LAW-11, and its script in %LOCALAPPDATA%\MindAttic\LghubWatchdog) | scheduled-task consolidation 2026-10-18 |
| M.WD-T12 | 2026-10-18 | feature | open | Runtime job API for apps that schedule on the fly: a WatchDog command (watchdog job add <name> --at <time>\|--every <span> --run <command> --owner <app>, watchdog job cancel <name>, watchdog job list --owner <app>) that registers one-off and recurring tasks under \MindAttic\WatchDog\<owner>\ headless, the same way install.ps1 does; first caller is Psst's reminders. Supersedes M.WD-T10's open question | user decision 2026-10-18: Psst uses WatchDog to create and cancel its tasks |
