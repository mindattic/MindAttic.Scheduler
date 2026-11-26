# MindAttic.Scheduler

One home for every MindAttic scheduled job. Instead of each app registering its own Windows
Scheduled Task its own way, each job is a line in [`jobs.json`](jobs.json) and
[`install.ps1`](install.ps1) makes the Task Scheduler match it:

- every job is registered at `\MindAttic\Scheduler\<name>`
  ([HOUSE-LAW-11](../MindAttic.HouseRules.md#HOUSE-LAW-11));
- every job runs through `conhost.exe --headless`, so a console program never opens a window or
  steals focus, and the task keeps the user's ordinary Interactive token (no S4U, no stored
  password, no elevation). The one exception is a GUI-subsystem program (a windowless WinExe
  daemon such as the Orchestrator's): it never gets a console, and conhost would hand it off and
  exit at once, so it is started directly and the task tracks the real process. That is read from
  the exe's header at install time (a bare name such as `pythonw` is first resolved through PATH
  and then run by its full path); if the exe isn't there yet, rerun the install once it is;
- every other job except a `keepRunning` daemon also runs through the [run wrapper](#the-run-wrapper-and-the-run-log),
  which records each run and its exit code;
- a `"daily": "auto"` job starts at the slot the [arranger](#overnight-jobs-daily-auto) gave it;
- the older copies a job lists under `replaces` are stopped and unregistered, and their folder is
  removed once it is empty;
- any task directly in `\MindAttic\Scheduler\` that the manifest no longer lists is unregistered.
  Subfolders (`\MindAttic\Scheduler\<owner>\`, for jobs apps create at runtime) are left alone.

It is idempotent: rerun it after every edit to `jobs.json`.

```powershell
.\install.ps1 -WhatIf   # show what would change
.\install.ps1           # apply
```

## Run and deploy

`run.bat` (or `run.ps1`) opens the MindAttic run menu: `1` Redeploy builds the jobs dashboard
(`MindAttic.Scheduler.Desktop.exe`) and the `mindattic-scheduler` CLI into
`R:\MindAttic\Apps\MindAttic.Scheduler\Debug` and `\Release`, `2` Run Debug and `3` Run Release start the dashboard.
The Release folder is the one the jobs use: install.ps1 and jobs.json run its `mindattic-scheduler.exe`. A redeploy
refuses while a job is running through that CLI, rather than stopping it mid-run; run it again once the job is done.

## jobs.json

The `jobs` list is required: a manifest without it is refused rather than read as "no jobs" (which
would unregister every Scheduler job). Write `"jobs": []` to really have none.

```json
{
  "jobs": [
    {
      "name": "Automata.Tick",
      "command": "\"D:\\Projects\\MindAttic\\Automata\\Automata.Runner\\bin\\Debug\\net10.0-windows\\automata-runner.exe\" tick",
      "schedule": [{ "every": "00:05:00" }, { "atLogon": true }],
      "replaces": ["\\MindAttic\\Automata\\MindAttic.Automata.Tick", "MindAttic.Automata.Logon"]
    }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `name` | yes | Task name under `\MindAttic\Scheduler\`. Letters, digits, `.`, `_`, `-`, space; unique. |
| `command` | yes | The full command line (program, quoted if it has spaces, then its arguments). `%VAR%` environment variables are expanded at install time. |
| `schedule` | yes | One entry or a list of them. Each entry has exactly one of: `{"every": "hh:mm:ss"}` (repeat forever, at least a minute apart), `{"daily": "HH:mm"}` (pinned at that time), `{"daily": "auto"}` (an overnight slot the arranger picks; see [below](#overnight-jobs-daily-auto); one per job, not on a `keepRunning` job), `{"atLogon": true}`. A `daily` entry may add `"daysOfWeek": ["Mon", ...]` (`Mon`/`Tue`/`Wed`/`Thu`/`Fri`/`Sat`/`Sun`) to run on only those days instead of every day. |
| `estimate` | no | How long an `auto` job usually takes (`hh:mm:ss`, more than zero), used by the arranger until the run log has measured runs of it. Default 15 minutes. |
| `replaces` | no | Tasks this job takes over from, as full paths (`\MindAttic\Launcher\MindAtticLauncherNightlyBackup`) or bare names for root-level tasks. They are removed on install. A task anywhere under `\MindAttic\Scheduler\` (runtime owner subfolders included) is refused. |
| `workingDirectory` | no | Start-in folder; `%VAR%` expanded. |
| `description` | no | Shown in Task Scheduler above the "managed by Scheduler" note. |
| `timeLimit` | no | Kill the run after this long (`hh:mm:ss`, `00:00:00` = never). Default 72 hours, the Windows default. |
| `keepRunning` | no | `true` for a daemon: no time limit (a `timeLimit` is refused), restarted up to 3 times a minute apart if it fails, and started by the install if it isn't running, so retiring its old task doesn't leave it down until the next logon. |
| `enabled` | no | `false` to register the task disabled. Default `true`. Applied explicitly on every install, so a disabled job stays disabled across reinstalls and a re-enabled one comes back; the Scheduler jobs dashboard (M.SCHD-T38) toggles this field rather than calling `Disable-ScheduledTask` by hand. |

Every task gets: start when available (catch up a missed run), run on battery, and
`MultipleInstances = IgnoreNew` (a run still going blocks the next one rather than piling up).

A job with no app of its own to run keeps its script in [`scripts/`](scripts) and runs it from
this repo's main checkout, e.g. `Lghub.LeakGuard` runs
[`scripts/restart-lghub-agent.ps1`](scripts/restart-lghub-agent.ps1) hourly to restart Logitech
G HUB's `lghub_agent` once it leaks past 2 GB (logged to
`%LOCALAPPDATA%\MindAttic\Scheduler\lghub-agent-restarts.log`).

## Overnight jobs: `"daily": "auto"`

An overnight job with no time of its own says `"schedule": { "daily": "auto" }` (optionally with
`daysOfWeek`) and the arranger picks its start, so no two overnight jobs start at once and all of
them are done by 08:00. Any explicit `HH:mm` pins a job: the arranger never moves it, and plans the
auto jobs around the pinned ones (and around every-N jobs' fire minutes).

- **arrangement.json.** The arranged starts live in
  `%LOCALAPPDATA%\MindAttic\Scheduler\arrangement.json`, not in jobs.json, so a nightly re-arrange
  never leaves a diff in the repo:

  ```json
  {
    "arrangedAt": "2026-11-19T23:30:04-06:00",
    "gapMinutes": 5,
    "fits": true,
    "slots": {
      "Azure.BackupNightly": { "start": "00:00", "durationMinutes": 14.2, "source": "measured" }
    }
  }
  ```

  `source` says where the duration came from: `measured` (the run log), `estimate` (the job's
  `estimate`) or `default` (15 minutes).
- **install.ps1** registers each auto job's daily trigger at its `slots.<job>.start`. A job with
  no slot (a new job, or no arrangement.json yet) has the arranger run once
  (`mindattic-scheduler arrange --no-install`); if it still has none, or the CLI isn't installed,
  it gets a placeholder (02:00, then 7 minutes apart in manifest order) and a warning. The install
  never fails over a missing slot. `-ArrangementPath` and `-CliPath` point it elsewhere (the tests
  do); the arranger is only run when `-ArrangementPath` is the default file, the one the CLI writes.
- **Scheduler.Arrange**, pinned at 23:30, runs `mindattic-scheduler arrange` nightly: it keeps
  tonight's slots unless something is wrong (a new or removed auto job, two starts colliding, a
  predicted finish after 08:00), re-placing them from measured durations, then reruns install.ps1.
  `arrange --optimize` re-plans from scratch, heaviest first from midnight.
- **The overnight lock.** Every auto job runs with `--exclusive`: the run wrapper takes the named
  mutex `Local\MindAttic.Scheduler.Overnight` first, so a job that overruns makes the next one wait
  (the wait is recorded) instead of piling on top of it. Pinned and every-N jobs don't take it.

## The run wrapper and the run log

Every manifest job except a GUI-subsystem program and a `keepRunning` daemon (Task Scheduler must
own those processes directly) is registered as

```text
conhost.exe --headless "<cli>" run <job> [--exclusive] -- <command>
```

where `<cli>` is `mindattic-scheduler.exe`: `%MINDATTIC_SCHEDULER_CLI%` if set, else
`R:\MindAttic\Apps\MindAttic.Scheduler\Release\mindattic-scheduler.exe` (deployed by `run.ps1 -Redeploy`, see
[Run and deploy](#run-and-deploy)). It records a
`running` row, takes the overnight lock if `--exclusive`, runs the command on the same hidden
console, records the end time and exit code, and exits with the command's own exit code, so Task
Scheduler's Last Run Result stays accurate. If the CLI isn't installed, install.ps1 registers the
plain conhost action instead, with a warning; rerun it once the CLI is published.

Logging never breaks a job: if the database is unreachable, rows go to
`%LOCALAPPDATA%\MindAttic\Scheduler\spool.jsonl` and the next run that can connect flushes them. A
row left `running` whose process is gone is marked `abandoned` by the next run or arrange.

**The database** is `MindAttic.Scheduler` (table `JobRun`). The connection is, in order:

1. the `MINDATTIC_SCHEDULER_SQL` environment variable;
2. `%APPDATA%\MindAttic\MindAttic.Scheduler\database.json`: `{"SqlServer": "<connection string>"}`;
3. local `(localdb)\MSSQLLocalDB`, `Database=MindAttic.Scheduler`, Windows auth (Windows only;
   elsewhere a missing connection string is an error).

Any SQL Server works, so the run log can live on the Hetzner SQL Server instead of LocalDB, e.g.
`{"SqlServer": "Server=<host>,1433;Database=MindAttic.Scheduler;User ID=<login>;Password=<password>;Encrypt=true;TrustServerCertificate=true"}`.
`db migrate` creates the database there when it is missing (the login must be able to connect to
`master`); only Azure SQL needs it made beforehand.

The CLI and Core target `net10.0` (M.SCHD-T58), so they build and run on Linux too (Task Scheduler
work there goes through `pwsh`); only the WPF dashboard is `net10.0-windows`.

### CLI verbs

```text
mindattic-scheduler run <job> [--exclusive] -- <command line>
mindattic-scheduler arrange [--optimize] [--dry-run] [--json] [--no-install]
mindattic-scheduler db migrate
mindattic-scheduler runs [--job <name>] [--since <date>] [--summary] [--night yyyy-MM-dd] [--json]
mindattic-scheduler dbsync [--db <name>]... [--full] [--dry-run] [--json]
```

- `run`: the run wrapper above.
- `arrange`: plans the auto jobs and writes arrangement.json, then runs install.ps1.
  `--optimize` re-plans from scratch; `--dry-run` writes nothing and `--json` prints the plan (the
  dashboard's Optimize preview uses both); `--no-install` writes arrangement.json but doesn't run
  install.ps1 (install.ps1 itself uses it, to fill a missing slot without recursing).
- `db migrate`: applies the run-log schema (idempotent). The only verb that runs DDL.
- `runs`: reads the run log. `--job` and `--since` filter it, `--summary` gives each job's usual
  duration and last result, `--night` shows one night's planned slots against what actually ran,
  `--json` for the dashboard.
- `dbsync`: syncs the local SQL Server databases to their remote copies; see [Database sync](#database-sync).

## Database sync

`mindattic-scheduler dbsync` (M.SCHD-T60) keeps a remote SQL Server (the Hetzner host, once it exists;
any SQL Server works) in step with the databases on this PC. The job **Database.SyncToRemote** runs it
nightly at the slot the arranger gives it, and is registered **disabled** until a target is configured.

How a run works, per database (each independent; one failing doesn't stop the others):

1. **Change Tracking** is turned on for the source database (retention `changeRetentionDays`) and for
   every table with a primary key. Turning it on for a table makes that table's next sync a full copy.
2. The source and target schemas are compared. **The schema is never altered**: a difference fails
   that database with nothing written (exit code 3). A target that doesn't exist, or has no tables, is
   **bootstrapped** first: the schema alone is copied with sqlpackage (no data, nothing dropped, no
   users or permissions), then every table is copied in full.
3. The watermark is captured **at the start of the run**: the source's change-tracking version and the
   UTC time. Rows written while the run is going are after that version, so the next run picks them up.
4. The database is **skipped** when nothing changed since the last watermark.
5. Otherwise each table syncs by the cheapest exact route:
   - tracked tables: only the rows inserted, updated or deleted since the watermark;
   - the first run, a newly tracked table, or a watermark older than the retention: a full copy;
   - tables without a primary key, and temporal history tables: a hash comparison (append-only tails
     copied alone, otherwise per-key-range row hashes);
   - system-versioned tables: versioning is suspended on the target, history and current tables are
     synced with their period columns exactly, then versioning is restored (repaired on the next run
     if a run died half way). VECTOR values are copied exactly.

   Foreign keys and triggers on the target are suspended during the writes and re-checked after.
6. Only when the whole database succeeded is the start watermark saved (in the Scheduler's own
   database, tables `DbSyncWatermark` and `DbSyncTableState`). A failure keeps the old one.

Settings: `%APPDATA%\MindAttic\MindAttic.Scheduler\dbsync.json`, every field optional; see
[docs/dbsync.example.json](docs/dbsync.example.json) for the fields and their defaults
(`changeRetentionDays`, `commandTimeoutSeconds`, `connectTimeoutSeconds`, `bulkBatchSize`,
`maxParallelDatabases`, `hashDiffRangeRows`, `strongHashMaxMB`, `sqlPackagePath`,
`schemaBootstrapTimeoutSeconds`, `schemaExtractProperties`, `schemaPublishProperties`). The database list
is built in (LocalDB for most; `localhost` for MindAttic.Tickets, MindAttic.Orchestrator and
IdiotProof_History); a `databases` entry only names what it changes. `targetConnection` is a full
connection string, or `{ProviderName}` to sign in as that service principal from
`%APPDATA%\MindAttic\Database\providers.json`. A database with no target is reported "not configured".

Flags: `--db` limits the run to the named databases; `--full` ignores the watermark; `--dry-run` writes
nothing anywhere; `--json` prints the results. Exit codes: 0 fine, 1 a database failed, 2 usage,
3 a schema mismatch. Log: `%LOCALAPPDATA%\MindAttic\Scheduler\dbsync.log`.

To turn it on once the remote host is up:

1. Put each database's `targetConnection` in dbsync.json.
2. Run `mindattic-scheduler dbsync --dry-run` and check every line, then `mindattic-scheduler dbsync`
   once by hand (the first run copies everything, so it takes a while).
3. Set `"enabled": true` on Database.SyncToRemote in jobs.json and run `install.ps1`.

## Jobs made at runtime

Some apps create and cancel tasks while they run (Psst's reminders). They don't register tasks
themselves; they call [`scheduler.ps1`](scheduler.ps1) (or the `scheduler.cmd` shim next to it):

```powershell
scheduler job add <name> --owner <app> (--at <time> | --every <hh:mm:ss>) --run <command>
                  [--workdir <dir>] [--description <text>] [--replaces <task>]...
scheduler job cancel <name> --owner <app>
scheduler job list --owner <app> [--json]
scheduler job status [--json]
scheduler job enable <name> --owner <app>
scheduler job disable <name> --owner <app>
```

- Each job lands at `\MindAttic\Scheduler\<owner>\<name>` and runs exactly like a manifest job:
  `conhost.exe --headless`, the user's Interactive token, start when available, `IgnoreNew`
  (but not through the run wrapper: runtime jobs aren't in the run log).
  `install.ps1` never touches these owner folders.
- `--at` is one run at a local time (`yyyy-MM-ddTHH:mm` is safest) and must be in the future;
  the task stays registered after it runs until the owner cancels it. `--every` repeats forever,
  at least a minute apart, from the next minute. Adding a name that exists overwrites it.
- `--replaces` takes over an older task the app registered itself, as in `jobs.json`.
- `cancel` unregisters the task without stopping a run under way, so a job can cancel itself
  as its last step. The owner's folder goes once its last job does.
- `list --json` prints `[{name, owner, task, state, nextRun, lastRun, command}]`, times in local
  ISO 8601.
- `status [--json]` lists *every* job under `\MindAttic\Scheduler\`, manifest jobs and every
  owner's runtime jobs together: `[{name, source, owner, task, description, scheduleRaw,
  scheduleHuman, enabled, state, nextRun, lastRun, command}]` (`source` is `"manifest"` or
  `"runtime"`; `owner` is `null` for a manifest job). This is the one read path the Scheduler
  jobs dashboard (M.SCHD-T38, see [docs/USER_STORIES.md](docs/USER_STORIES.md)) uses.
- `enable`/`disable` flip a runtime job's live Enabled bit directly. A manifest job's `enabled`
  state is set in `jobs.json` instead (see the field table above) and applied by `install.ps1`,
  so the manifest is always what Task Scheduler actually has.
- Exit code 0 on success, 1 on any error, with the reason on stderr.

## Tests

```powershell
Invoke-Pester .\tests
```

The integration tests register real tasks under a scratch folder, `\MindAttic\Scheduler\Tests\`
(plus `\MindAttic\Scheduler\SchedulerTests\` for the command-line test), and delete them when done.

## Project docs

- [AGENTS.md](AGENTS.md) - agent entrypoint.
- [docs/USER_STORIES.md](docs/USER_STORIES.md) - stories and the ticket log (generated by the
  tickets CLI; don't edit it by hand).
