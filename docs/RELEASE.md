---
layer: release
status: living
---
# MindAttic.Scheduler — Release charter

> The one finish line this repo is working toward. Every open ticket in this repo sits under one of the epics
> below, and every epic is part of the milestone. Work that does not fit is out of scope until this milestone is
> reached (owner, 2026-11-27, MAT-T319).

## 1. What this is
The one home for every MindAttic scheduled job on this PC: `jobs.json` + `install.ps1` + a recording run wrapper
and a WPF jobs dashboard, for the owner's own machine.

## 2. The milestone: MindAttic.Scheduler 1.0 on R: {#milestone}
- **Milestone:** MS-61, "Every local job runs from R:, nothing points at retired infrastructure"
- **Published means:** internal tool, installed at `R:\MindAttic\Apps\MindAttic.Scheduler\{Debug,Release}` by
  `run.ps1`; the jobs run its Release `mindattic-scheduler.exe`.
- **The natural stopping point:** the manifest, arranger, run log, runtime-job API and dashboard already work.
  Done means every job points at a current exe (R: deploys, the Orchestrator daemon's own exe), the old
  `C:\Apps\MindAttic.Scheduler` copy is gone, the Azure leftovers are retired, and the manifest holds the jobs
  the owner asked for. That is a complete local scheduler; nothing else is needed for one PC.

## 3. Done when (acceptance checks)
1. build — `MindAttic.Scheduler.slnx` builds clean on main.
2. exists — `R:\MindAttic\Apps\MindAttic.Scheduler\Release\mindattic-scheduler.exe`.
3. harness — `install.ps1 -WhatIf` reports no change and no warning against the live Task Scheduler.
4. signoff — the owner opens the dashboard and sees every job with a sensible schedule and last run.

## 4. Epics, in priority order
| Priority | Epic (ticket) | What it delivers | Features |
|---|---|---|---|
| 900 | M.SCHD-T79 Jobs on current paths | Every job runs a current exe; C: copy gone | M.SCHD-T80 Orchestrator daemon exe (990), M.SCHD-T81 Retire the C: deploy (980) |
| 800 | M.SCHD-T82 Manifest is the truth | The manifest holds the wanted jobs and no dead ones | M.SCHD-T83 Requested jobs registered (890), M.SCHD-T84 Azure leftovers retired (880) |

## 5. Out of scope (not before this milestone)
- Running jobs on a Linux/Hetzner server (systemd timer provider, M.SCHD-T57/T59) — no such host exists yet.
- Turning on Database.SyncToRemote — waits for a remote SQL target; the engine is done and the job stays disabled.
- A C:-drive temp-cleanup job (M.SCHD-T28) — the Orchestrator's disk-cleanup floor (M.ORCH-T1449) owns this.
- Re-enabling GridGame.Nightly — GridGame's own charter decides (M.SCHD-T78).
- Generic bughunt sweeps (M.SCHD-T56).

## 6. Current verified state
- Run menu over deploy-lib.ps1; both exes on R: (dc84b95, MAT-T305).
- Jobs dashboard: job list, schedule editor, enable/disable over `IJobProvider` + `LocalTaskSchedulerProvider`,
  Optimize preview, Last-night view (8806562, M.SCHD-T42; 1194c6b).
- Core/CLI on net10.0 and run log on any SQL Server (519732d, M.SCHD-T58); DbSync engine (M.SCHD-T60..T72).
- GridGame.Nightly registered (1af06f3) and disabled for the freeze (74558aa, M.SCHD-T78).

## 7. Decision record
- 2026-11-27 — charter drafted from README/jobs.json/code/tickets; `docs/BIBLE.md` written; Legion vote: Approve, 9/9 voters, plurality quorum reached, consensus 1.0; no dissent.
- Triage: 15 open → done 6 (T33, T38, T52, T53, T54, T76; GridGame job registered then frozen by M.SCHD-T78), duplicate 1 (T23 of T22), wont-do 5 (T15, T28, T56, T57, T59; closing T57 rolled up MAT-T247 "Scheduled jobs on Hetzner", whose other child was already wont-do), kept 3 (T22 reworded, T45, T77), new tasks 1 (M.SCHD-T85 remove Azure leftovers).
