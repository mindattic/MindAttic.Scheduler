---
layer: bible
status: living
---
# MindAttic.Scheduler — Project Bible

> Single source of truth for what MindAttic.Scheduler IS, is NOT, and the rules that keep it coherent.
> README says how to build and run it; this says how to think about it. The finish line lives in
> `docs/RELEASE.md`.

## 1. The one sentence {#M.SCHD-§1}
MindAttic.Scheduler is the one home for every MindAttic scheduled job on this PC: each job is a line in
`jobs.json`, `install.ps1` makes Windows Task Scheduler match it, and a run wrapper records every run in a
run log that a WPF dashboard shows.

## 2. The product promise {#M.SCHD-§2}
- **One manifest.** A job is added, changed, disabled or retired by editing `jobs.json` and rerunning
  `install.ps1` (idempotent). No app registers its own scheduled task its own way.
- **Quiet jobs.** Every job runs under `\MindAttic\Scheduler\<name>` through `conhost.exe --headless`, on the
  user's ordinary Interactive token: no window, no focus steal, no stored password, no elevation.
- **Every run recorded.** Non-daemon jobs run through `mindattic-scheduler run`, which records start, end and
  exit code (spooling to `spool.jsonl` when the database is unreachable) and keeps Task Scheduler's Last Run
  Result accurate.
- **Overnight jobs arrange themselves.** `"daily": "auto"` jobs get staggered slots, heaviest first from
  midnight, all done by 08:00, under one overnight lock.
- **Runtime jobs for apps.** Apps such as Psst add and cancel their own jobs through `scheduler job ...`
  under `\MindAttic\Scheduler\<owner>\`, which `install.ps1` never touches.
- **A dashboard.** `MindAttic.Scheduler.Desktop.exe` lists every job (manifest and runtime) with its
  schedule, enabled state and last run, edits schedules, toggles jobs, previews the overnight plan and
  shows last night's runs.

## 3. What it is NOT {#M.SCHD-§3}
- NOT a cron daemon of its own: Windows Task Scheduler does the firing; the Scheduler only registers and
  records.
- NOT a cloud scheduler. Azure is decommissioned (MAT-T297); there is no Azure job provider in use.
- NOT the owner of the jobs' own logic: a job's script or exe lives in its own repo (only jobs with no app
  of their own keep a script in `scripts/`).
- NOT a test or soak runner: jobs that run soaks (e.g. GridGame.Nightly) are disabled during the
  portfolio-reset freeze unless their repo's charter says otherwise.

## 4. Architecture canon {#M.SCHD-§4}
```
 jobs.json ──► install.ps1 ──► Task Scheduler \MindAttic\Scheduler\<job>
                   │                  │ fires
                   │                  ▼
                   │     conhost --headless mindattic-scheduler run <job> [--exclusive] -- <command>
                   │                  │ records
 arrangement.json ◄┴── arrange        ▼
                              SQL run log (MindAttic.Scheduler: JobRun, DbSync*)
                                      ▲
 scheduler.ps1 job add|cancel|list|status|enable|disable (runtime jobs, per owner)
 MindAttic.Scheduler.Desktop (WPF) ── IJobProvider ── LocalTaskSchedulerProvider
```

### 4.1 Projects
- `src/MindAttic.Scheduler.Core` (net10.0): manifest model and editor (`ManifestJob`, `JobsManifestEditor`),
  `IJobProvider` + `LocalTaskSchedulerProvider`, `CronTranslator`, `Arranger`/`OvernightOptimizer`,
  `RunLog`/`SchedulerDb`, `NightReport`, and `DbSync/*` (local-to-remote SQL sync).
- `src/MindAttic.Scheduler.Cli` (net10.0, `mindattic-scheduler`): `run`, `arrange`, `db migrate`, `runs`,
  `dbsync`.
- `src/MindAttic.Scheduler.Wpf` (net10.0-windows, `MindAttic.Scheduler.Desktop.exe`): the jobs dashboard.
- `install.ps1`, `scheduler.ps1`/`scheduler.cmd`, `watchdog.ps1`: the PowerShell front doors.
- `tests/`: NUnit `MindAttic.Scheduler.Core.Tests` plus Pester `*.Tests.ps1`.
- `run.ps1`/`run.bat`: the shared run menu; deploys Debug and Release to `R:\MindAttic\Apps\MindAttic.Scheduler`.

### 4.2 Domain model (nouns)
Job (manifest or runtime), ScheduleEntry (`every`, `daily HH:mm`, `daily auto`, `atLogon`), Arrangement
(slots), JobRun (a run-log row), Owner (an app's runtime-job folder), DbSync watermark.

### 4.3 Key services (verbs)
Install (manifest to tasks), Run (wrap and record), Arrange (plan overnight slots), Job add/cancel (runtime),
Status (every job, one read path), DbSync (local SQL to a remote copy, disabled until a target exists).

## 5. The Laws {#M.SCHD-§5}
Inherits `MindAttic.HouseRules.md` by reference.
1. {#M.SCHD-LAW-1} Every manifest job lives under `\MindAttic\Scheduler\` (HOUSE-LAW-11); `install.ps1`
   unregisters any task directly there that the manifest no longer lists.
2. {#M.SCHD-LAW-2} A manifest without a `jobs` list is refused, never read as "no jobs".
3. {#M.SCHD-LAW-3} Logging never breaks a job: an unreachable run log spools, it does not fail the run.
4. {#M.SCHD-LAW-4} A manifest job's enabled state lives in `jobs.json`; the dashboard edits the manifest,
   it does not flip live tasks behind the manifest's back.
5. {#M.SCHD-LAW-5} Jobs run the R: Release deploy (`R:\MindAttic\Apps\<repo>\Release`), not a `C:\Apps`
   copy, once that app has moved to R:.

## 6. Verified state {#M.SCHD-§6}
- Run menu over `deploy-lib.ps1`; Debug and Release land in `R:\MindAttic\Apps\MindAttic.Scheduler`
  (dc84b95, MAT-T305; both `mindattic-scheduler.exe` present 2026-11-27).
- Dashboard with job list, schedule editor, enable/disable, Optimize preview and Last-night view
  (8806562, M.SCHD-T42; 1194c6b).
- Overnight arranger and run wrapper with overnight lock (8806562).
- Core and CLI on net10.0, run log on any SQL Server (519732d, M.SCHD-T58).
- DbSync engine complete, job registered disabled until a remote target exists (M.SCHD-T60..T72).
- GridGame.Nightly disabled for the portfolio-reset freeze (74558aa, M.SCHD-T78).

## 7. Active frontier {#M.SCHD-§7}
See `docs/RELEASE.md` for the milestone and its epics.

## 8. Quality bar {#M.SCHD-§8}
A change is done when the solution builds, `install.ps1 -WhatIf` shows only the intended diff, the job runs
from its R: path, and README's field table and verbs still match the code.

## 9. Glossary {#M.SCHD-§9}
- **Manifest job**: a `jobs.json` entry. **Runtime job**: one an app registered via `scheduler job add`.
- **Auto job**: `"daily": "auto"`, slotted by the arranger. **keepRunning**: a daemon job Task Scheduler
  restarts; it skips the run wrapper.
- **Run log**: the `MindAttic.Scheduler` SQL database.
