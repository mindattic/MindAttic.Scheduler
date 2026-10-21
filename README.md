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
  the exe's header at install time; if the exe isn't there yet, rerun the install once it is;
- the older copies a job lists under `replaces` are stopped and unregistered, and their folder is
  removed once it is empty;
- any task directly in `\MindAttic\Scheduler\` that the manifest no longer lists is unregistered.
  Subfolders (`\MindAttic\Scheduler\<owner>\`, for jobs apps create at runtime) are left alone.

It is idempotent: rerun it after every edit to `jobs.json`.

```powershell
.\install.ps1 -WhatIf   # show what would change
.\install.ps1           # apply
```

## jobs.json

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
| `schedule` | yes | One entry or a list of them. Each entry has exactly one of: `{"every": "hh:mm:ss"}` (repeat forever, at least a minute apart), `{"daily": "HH:mm"}`, `{"atLogon": true}`. |
| `replaces` | no | Tasks this job takes over from, as full paths (`\MindAttic\Launcher\MindAtticLauncherNightlyBackup`) or bare names for root-level tasks. They are removed on install. |
| `workingDirectory` | no | Start-in folder; `%VAR%` expanded. |
| `description` | no | Shown in Task Scheduler above the "managed by Scheduler" note. |
| `timeLimit` | no | Kill the run after this long (`hh:mm:ss`, `00:00:00` = never). Default 72 hours, the Windows default. |
| `keepRunning` | no | `true` for a daemon: no time limit (a `timeLimit` is refused), restarted up to 3 times a minute apart if it fails, and started by the install if it isn't running, so retiring its old task doesn't leave it down until the next logon. |

Every task gets: start when available (catch up a missed run), run on battery, and
`MultipleInstances = IgnoreNew` (a run still going blocks the next one rather than piling up).

A job with no app of its own to run keeps its script in [`scripts/`](scripts) and runs it from
this repo's main checkout, e.g. `Lghub.LeakGuard` runs
[`scripts/restart-lghub-agent.ps1`](scripts/restart-lghub-agent.ps1) hourly to restart Logitech
G HUB's `lghub_agent` once it leaks past 2 GB (logged to
`%LOCALAPPDATA%\MindAttic\Scheduler\lghub-agent-restarts.log`).

## Jobs made at runtime

Some apps create and cancel tasks while they run (Psst's reminders). They don't register tasks
themselves; they call [`scheduler.ps1`](scheduler.ps1) (or the `scheduler.cmd` shim next to it):

```powershell
scheduler job add <name> --owner <app> (--at <time> | --every <hh:mm:ss>) --run <command>
                  [--workdir <dir>] [--description <text>] [--replaces <task>]...
scheduler job cancel <name> --owner <app>
scheduler job list --owner <app> [--json]
```

- Each job lands at `\MindAttic\Scheduler\<owner>\<name>` and runs exactly like a manifest job:
  `conhost.exe --headless`, the user's Interactive token, start when available, `IgnoreNew`.
  `install.ps1` never touches these owner folders.
- `--at` is one run at a local time (`yyyy-MM-ddTHH:mm` is safest) and must be in the future;
  the task stays registered after it runs until the owner cancels it. `--every` repeats forever,
  at least a minute apart, from the next minute. Adding a name that exists overwrites it.
- `--replaces` takes over an older task the app registered itself, as in `jobs.json`.
- `cancel` unregisters the task without stopping a run under way, so a job can cancel itself
  as its last step. The owner's folder goes once its last job does.
- `list --json` prints `[{name, owner, task, state, nextRun, lastRun, command}]`, times in local
  ISO 8601.
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
