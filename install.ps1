<#
.SYNOPSIS
  Register every job in jobs.json as a Windows Scheduled Task under \MindAttic\Scheduler\.

.DESCRIPTION
  Idempotent: run it as often as you like and the scheduler ends up matching the manifest.

    1. For each job, every task named in its 'replaces' list (an older copy registered by the
       app that used to own the job) is stopped and unregistered, and the folder it lived in
       is removed if that leaves it empty.
    2. Each job is registered (or overwritten) at \MindAttic\Scheduler\<name> (HOUSE-LAW-11).
       Its action is 'conhost.exe --headless <command>', so no console window ever opens and
       nothing steals focus, while the task keeps the user's ordinary Interactive token. A
       GUI-subsystem program (a windowless WinExe daemon) never gets a console, and conhost would
       hand it off and exit at once, so it is started directly and the task tracks the real process.
       A 'keepRunning' job (a daemon) has no time limit, is restarted if it fails, and is started
       on install if it isn't already running.
       Every other job (a 'tracked' job) runs through the run wrapper instead:
       'conhost.exe --headless "<cli>" run <job> [--exclusive] -- <command>', which records the
       run in the run log and returns the command's own exit code (M.SCHD-T42). '--exclusive'
       (the one-at-a-time overnight lock) is added for "daily": "auto" jobs only. If the CLI isn't
       installed, the job keeps the plain conhost action and a warning says so.
       A "daily": "auto" entry starts at the job's arranged slot in arrangement.json. A job with
       no slot there has the arranger run once ('<cli> arrange --no-install'); if it still has
       none (or there's no CLI), it gets a placeholder slot (02:00, then 7 minutes apart in
       manifest order) and a warning, never a failed install.
    3. Any task left directly in \MindAttic\Scheduler\ that the manifest no longer lists is
       unregistered. Subfolders (\MindAttic\Scheduler\<owner>\, used by runtime jobs) are left
       alone.

  No elevation is needed: every task runs as the current user, Interactive, limited rights.

.PARAMETER Manifest
  The jobs manifest. Defaults to jobs.json next to this script.

.PARAMETER TaskPath
  The Task Scheduler folder the jobs live in. Leave it at the default; the tests point it at a
  scratch folder.

.PARAMETER ArrangementPath
  The arranged overnight slots. Defaults to %LOCALAPPDATA%\MindAttic\Scheduler\arrangement.json;
  the tests point it at a scratch file. The arranger is only run to fill a missing slot when this
  is the default path (the CLI always writes the default one).

.PARAMETER CliPath
  mindattic-scheduler.exe, the run wrapper and arranger. Defaults to %MINDATTIC_SCHEDULER_CLI%,
  else R:\MindAttic\Apps\MindAttic.Scheduler\Release\mindattic-scheduler.exe.

.EXAMPLE
  .\install.ps1 -WhatIf     # show what would change, touch nothing
  .\install.ps1             # apply
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Manifest,
    [string]$TaskPath = '\MindAttic\Scheduler\',
    [string]$ArrangementPath,
    [string]$CliPath
)

$ErrorActionPreference = 'Stop'

$script:ManagedMarker = 'Managed by MindAttic.Scheduler (jobs.json). Edit the manifest and rerun install.ps1; do not edit this task by hand.'

function Get-DefaultArrangementPath {
    Join-Path $env:LOCALAPPDATA 'MindAttic\Scheduler\arrangement.json'
}

function Get-DefaultCliPath {
    if ($env:MINDATTIC_SCHEDULER_CLI) { $env:MINDATTIC_SCHEDULER_CLI }
    else { 'R:\MindAttic\Apps\MindAttic.Scheduler\Release\mindattic-scheduler.exe' }
}

function Test-AutoEntry($Entry) {
    # {"daily": "auto"}: an arranged overnight slot (M.SCHD-T42). Any HH:mm is pinned.
    $Entry.daily -is [string] -and $Entry.daily -ieq 'auto'
}

function ConvertTo-TaskFolder([string]$Path) {
    # Task Scheduler folders are written '\A\B\': leading and trailing backslash.
    $p = '\' + $Path.Trim().Trim('\') + '\'
    if ($p -eq '\\') { '\' } else { $p }
}

function Split-TaskFullName([string]$FullName) {
    # '\MindAttic\Automata\Tick' -> Path '\MindAttic\Automata\', Name 'Tick'. A bare name is a
    # root-level task (the pre-HOUSE-LAW-11 location).
    $s = $FullName.Trim()
    $i = $s.LastIndexOf('\')
    if ($i -lt 0) { return [pscustomobject]@{ Path = '\'; Name = $s } }
    [pscustomobject]@{
        Path = ConvertTo-TaskFolder $s.Substring(0, $i)
        Name = $s.Substring($i + 1)
    }
}

function ConvertTo-Span([string]$Text, [string]$What) {
    $span = [TimeSpan]::Zero
    if (-not [TimeSpan]::TryParse($Text, [ref]$span)) {
        throw "$What '$Text' is not a time span (expected hh:mm:ss, e.g. 00:05:00)."
    }
    $span
}

$script:DayOfWeekNames = @{
    Mon = 'Monday'; Tue = 'Tuesday'; Wed = 'Wednesday'; Thu = 'Thursday'
    Fri = 'Friday'; Sat = 'Saturday'; Sun = 'Sunday'
}

function ConvertTo-DaysOfWeek([string[]]$Names, [string]$Job) {
    # Returned as the full day names (e.g. 'Monday'): New-ScheduledTaskTrigger's -DaysOfWeek
    # parameter is its own ScheduledTasks module enum, not [DayOfWeek], but binds a string by
    # name just the same.
    $Names | ForEach-Object {
        $full = $script:DayOfWeekNames[$_]
        if (-not $full) { throw "Job '$Job': daysOfWeek '$_' must be one of Mon, Tue, Wed, Thu, Fri, Sat, Sun." }
        $full
    }
}

function Test-ScheduleEntry($Entry, [string]$Job) {
    $kinds = @('every', 'daily', 'atLogon') | Where-Object { $null -ne $Entry.$_ }
    if (@($kinds).Count -ne 1) {
        throw "Job '$Job': each schedule entry needs exactly one of every, daily, atLogon."
    }
    switch ($kinds) {
        'every' {
            $span = ConvertTo-Span $Entry.every "Job '$Job': every"
            if ($span -lt [TimeSpan]::FromMinutes(1)) { throw "Job '$Job': every must be at least 00:01:00." }
            if ($null -ne $Entry.daysOfWeek) { throw "Job '$Job': daysOfWeek only applies to a daily schedule entry." }
        }
        'daily' {
            if (-not (Test-AutoEntry $Entry) -and $Entry.daily -notmatch '^([01]\d|2[0-3]):[0-5]\d$') {
                throw "Job '$Job': daily '$($Entry.daily)' must be a 24-hour HH:mm time or ""auto""."
            }
            if ($null -ne $Entry.daysOfWeek) {
                $days = @($Entry.daysOfWeek | Where-Object { $_ })
                if ($days.Count -eq 0) { throw "Job '$Job': daysOfWeek, if given, must list at least one day." }
                ConvertTo-DaysOfWeek $days $Job | Out-Null
            }
        }
        'atLogon' {
            if ($Entry.atLogon -ne $true) { throw "Job '$Job': atLogon must be true." }
            if ($null -ne $Entry.daysOfWeek) { throw "Job '$Job': daysOfWeek only applies to a daily schedule entry." }
        }
    }
}

function Read-JobManifest([string]$Path, [string]$TaskPath = '\MindAttic\Scheduler\') {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Manifest not found: $Path" }
    $doc = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    # A manifest with no 'jobs' key (or "jobs": null) is a broken manifest, not an empty one:
    # treating it as empty would unregister every Scheduler job. An explicit "jobs": [] still
    # means "no jobs".
    $jobsProp = if ($null -ne $doc) { $doc.PSObject.Properties['jobs'] } else { $null }
    if ($null -eq $jobsProp -or $null -eq $jobsProp.Value) {
        throw "Manifest $Path has no 'jobs' list. Use `"jobs`": [] to really have no jobs."
    }
    $folder = ConvertTo-TaskFolder $TaskPath
    $seen = @{}
    $jobs = @()
    foreach ($j in @($doc.jobs | Where-Object { $null -ne $_ })) {
        $name = [string]$j.name
        if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9._ -]*$') {
            throw "Job name '$name' is missing or has characters a task name can't hold."
        }
        if ($seen.ContainsKey($name)) { throw "Job '$name' is listed twice." }
        $seen[$name] = $true

        if ([string]::IsNullOrWhiteSpace([string]$j.command)) { throw "Job '$name' has no command." }

        $schedule = @($j.schedule | Where-Object { $null -ne $_ })
        if ($schedule.Count -eq 0) { throw "Job '$name' has no schedule." }
        foreach ($entry in $schedule) { Test-ScheduleEntry $entry $name }
        # One arranged slot per job: arrangement.json holds a single start for it.
        $autoCount = @($schedule | Where-Object { Test-AutoEntry $_ }).Count
        if ($autoCount -gt 1) { throw "Job '$name': only one schedule entry can be ""daily"": ""auto""." }
        $isAuto = $autoCount -eq 1

        $estimate = $null
        if ($null -ne $j.estimate) {
            $estimate = ConvertTo-Span ([string]$j.estimate) "Job '$name': estimate"
            if ($estimate -le [TimeSpan]::Zero) { throw "Job '$name': estimate must be longer than 00:00:00." }
        }

        $replaces = @()
        foreach ($r in @($j.replaces | Where-Object { $_ })) {
            $old = Split-TaskFullName ([string]$r)
            # The whole subtree is Scheduler's: runtime jobs live in $folder<owner>\ (scheduler.ps1
            # blocks the same subtree for --replaces).
            if ($old.Path.StartsWith($folder, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Job '$name' replaces '$r', which is inside $folder; that would delete a Scheduler job."
            }
            $replaces += $old
        }

        if ($null -ne $j.keepRunning -and $j.keepRunning -isnot [bool]) { throw "Job '$name': keepRunning must be true or false." }
        $keepRunning = $j.keepRunning -eq $true
        $timeLimit = if ($j.timeLimit) { ConvertTo-Span $j.timeLimit "Job '$name': timeLimit" }
                     elseif ($keepRunning) { [TimeSpan]::Zero }
                     else { [TimeSpan]::FromHours(72) }
        if ($keepRunning -and $timeLimit -ne [TimeSpan]::Zero) {
            throw "Job '$name': a keepRunning job can't have a timeLimit; it runs until it exits."
        }
        if ($keepRunning -and $isAuto) {
            throw "Job '$name': a keepRunning job can't be ""daily"": ""auto""; the arranger only places jobs that finish."
        }

        if ($null -ne $j.enabled -and $j.enabled -isnot [bool]) { throw "Job '$name': enabled must be true or false." }
        $enabled = $j.enabled -ne $false

        $jobs += [pscustomobject]@{
            Name             = $name
            Command          = [Environment]::ExpandEnvironmentVariables([string]$j.command)
            WorkingDirectory = if ($j.workingDirectory) { [Environment]::ExpandEnvironmentVariables([string]$j.workingDirectory) } else { $null }
            Description      = [string]$j.description
            Schedule         = $schedule
            Replaces         = $replaces
            TimeLimit        = $timeLimit
            KeepRunning      = $keepRunning
            Enabled          = $enabled
            IsAuto           = $isAuto
            Estimate         = $estimate
        }
    }
    , $jobs
}

function Get-CurrentUserId { "$env:USERDOMAIN\$env:USERNAME" }

function Read-Arrangement([string]$Path) {
    # arrangement.json's slots as job name -> 'HH:mm'. A missing or unreadable file, or a slot
    # whose start isn't HH:mm, is simply absent (the caller falls back); this never throws.
    $slots = @{}
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $slots }
    try {
        $doc = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $doc -or $null -eq $doc.slots) { return $slots }
        foreach ($p in $doc.slots.PSObject.Properties) {
            $start = [string]$p.Value.start
            if ($start -match '^([01]\d|2[0-3]):[0-5]\d$') { $slots[$p.Name] = $start }
        }
    }
    catch { Write-Warning "Could not read ${Path}: $($_.Exception.Message)" }
    $slots
}

function Resolve-AutoSlots {
    # The start ('HH:mm') of every "daily": "auto" job in $Jobs, as a hashtable. A job with no
    # slot in arrangement.json has the arranger run once to place it ('<cli> arrange
    # --no-install', which writes arrangement.json without calling install.ps1 back); one still
    # without a slot gets a placeholder (02:00, then 7 minutes apart in manifest order) and a
    # warning. Never throws: a missing slot must not fail the install.
    [CmdletBinding()]
    param($Jobs, [string]$ArrangementPath, [string]$CliPath, [switch]$NoArrange)

    if (-not $ArrangementPath) { $ArrangementPath = Get-DefaultArrangementPath }
    if (-not $CliPath) { $CliPath = Get-DefaultCliPath }
    $result = @{}
    $auto = @($Jobs | Where-Object { $null -ne $_ -and $_.IsAuto -eq $true })
    if ($auto.Count -eq 0) { return $result }

    $slots = Read-Arrangement $ArrangementPath
    $missing = @($auto | Where-Object { -not $slots.ContainsKey($_.Name) })

    # The CLI always writes the default arrangement.json (from the repo's jobs.json), so it is
    # only run when that is the file being read here; a test's scratch file is never touched.
    $isDefault = $false
    try {
        $isDefault = [string]::Equals([IO.Path]::GetFullPath($ArrangementPath),
            [IO.Path]::GetFullPath((Get-DefaultArrangementPath)), [StringComparison]::OrdinalIgnoreCase)
    }
    catch { }
    if ($missing.Count -gt 0 -and -not $NoArrange -and $isDefault -and (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
        Write-Verbose "No arranged slot yet for $(($missing | ForEach-Object Name) -join ', '); running $CliPath arrange --no-install."
        try {
            # Continue: under Stop, Windows PowerShell turns the CLI's first stderr line into a
            # terminating error and cuts the arrange short.
            $ErrorActionPreference = 'Continue'
            & $CliPath arrange --no-install 2>&1 | ForEach-Object { Write-Verbose "arrange: $_" }
            if ($LASTEXITCODE -ne 0) { Write-Warning "$CliPath arrange --no-install exited $LASTEXITCODE." }
        }
        catch { Write-Warning "$CliPath arrange --no-install failed: $($_.Exception.Message)" }
        $slots = Read-Arrangement $ArrangementPath
    }

    $i = 0
    foreach ($job in $auto) {
        if ($slots.ContainsKey($job.Name)) { $result[$job.Name] = $slots[$job.Name]; continue }
        $placeholder = [datetime]::Today.AddHours(2).AddMinutes(7 * $i).ToString('HH:mm')
        $i++
        Write-Warning "Job '$($job.Name)' is ""daily"": ""auto"" but has no arranged slot in $ArrangementPath; using a placeholder $placeholder until 'mindattic-scheduler arrange' places it."
        $result[$job.Name] = $placeholder
    }
    $result
}

function New-JobTriggers {
    # $Slots (job name -> 'HH:mm', from Resolve-AutoSlots) gives a "daily": "auto" entry its
    # start. Without one for this job, its slot is resolved here from $ArrangementPath.
    param($Job, [hashtable]$Slots, [string]$ArrangementPath, [string]$CliPath)

    if ($Job.IsAuto -eq $true -and ($null -eq $Slots -or -not $Slots.ContainsKey($Job.Name))) {
        $Slots = Resolve-AutoSlots -Jobs @($Job) -ArrangementPath $ArrangementPath -CliPath $CliPath
    }
    foreach ($entry in $Job.Schedule) {
        if ($null -ne $entry.every) {
            # Anchored at today's midnight so a rerun produces the same definition. No
            # -RepetitionDuration: on Windows 10+ that means 'repeat indefinitely'.
            New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
                -RepetitionInterval (ConvertTo-Span $entry.every 'every')
        }
        elseif ($null -ne $entry.daily) {
            $time = if (Test-AutoEntry $entry) { $Slots[$Job.Name] } else { $entry.daily }
            $at = [datetime]::ParseExact($time, 'HH:mm', $null)
            $days = @($entry.daysOfWeek | Where-Object { $_ })
            if ($days.Count -gt 0) {
                New-ScheduledTaskTrigger -Weekly -At $at -DaysOfWeek (ConvertTo-DaysOfWeek $days $Job.Name)
            }
            else {
                New-ScheduledTaskTrigger -Daily -At $at
            }
        }
        else {
            New-ScheduledTaskTrigger -AtLogOn -User (Get-CurrentUserId)
        }
    }
}

function Split-CommandLine([string]$Command) {
    # '"C:\A B\x.exe" --daemon' -> Program 'C:\A B\x.exe', Arguments '--daemon'.
    $c = $Command.Trim()
    if ($c -match '^"([^"]*)"\s*(.*)$' -or $c -match '^(\S+)\s*(.*)$') {
        return [pscustomobject]@{ Program = $Matches[1]; Arguments = $Matches[2] }
    }
    [pscustomobject]@{ Program = $c; Arguments = '' }
}

function Resolve-JobProgram([string]$Program) {
    # A bare program name (pythonw) is resolved through PATH, the way the task itself would find
    # it, so Test-GuiProgram can see what it really is. A path, or a name PATH can't resolve, is
    # returned unchanged.
    if ([string]::IsNullOrWhiteSpace($Program) -or [IO.Path]::IsPathRooted($Program) -or
        $Program.IndexOfAny([char[]]'\/') -ge 0) { return $Program }
    $cmd = Get-Command -Name $Program -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($cmd -and $cmd.Source -and [IO.Path]::IsPathRooted($cmd.Source)) { return $cmd.Source }
    $Program
}

function Test-GuiProgram([string]$Path) {
    # True when $Path is a PE image of the Windows GUI subsystem (2). Anything else, including a
    # program that isn't there yet, is treated as a console program.
    try {
        if (-not [IO.Path]::IsPathRooted($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
        $fs = [IO.File]::OpenRead($Path)
        try {
            $r = New-Object IO.BinaryReader $fs
            if ($r.ReadUInt16() -ne 0x5A4D) { return $false }          # MZ
            $fs.Position = 0x3C; $pe = $r.ReadInt32()
            $fs.Position = $pe
            if ($r.ReadUInt32() -ne 0x4550) { return $false }          # PE\0\0
            $fs.Position = $pe + 24 + 68                               # optional header .Subsystem
            return $r.ReadUInt16() -eq 2
        }
        finally { $fs.Dispose() }
    }
    catch { return $false }
}

$script:CliMissingWarned = @{}

function New-JobAction {
    [CmdletBinding()]
    param($Job, [string]$CliPath)

    # conhost --headless gives the command a console that is never shown, so a console exe run
    # by an Interactive task neither flashes a window nor takes focus. A GUI-subsystem program
    # gets no console anyway, and conhost would start it and exit at once (leaving the task
    # 'finished' while the program runs untracked), so it is started directly.
    # A bare-name GUI program (pythonw) is resolved through PATH and run directly by full path.
    #
    # With -CliPath (install.ps1 passes it for manifest jobs; scheduler.ps1's runtime jobs don't),
    # a tracked job - a named console job that isn't a keepRunning daemon - runs through the run
    # wrapper, which records the run and exits with the command's own exit code:
    #   conhost --headless "<cli>" run <job> [--exclusive] -- <command>
    # --exclusive (the one-at-a-time overnight lock) is for "daily": "auto" jobs only. If the CLI
    # isn't installed the job keeps the plain conhost action, with one warning per install.
    $parts = Split-CommandLine $Job.Command
    $program = Resolve-JobProgram $parts.Program
    if (Test-GuiProgram $program) {
        $execute = $program
        $argument = $parts.Arguments
    }
    else {
        $execute = Join-Path $env:SystemRoot 'System32\conhost.exe'
        $argument = '--headless ' + $Job.Command
        $tracked = $CliPath -and $Job.Name -and $Job.KeepRunning -ne $true
        if ($tracked -and (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
            $name = if ($Job.Name -match '\s') { "`"$($Job.Name)`"" } else { $Job.Name }
            $exclusive = if ($Job.IsAuto -eq $true) { ' --exclusive' } else { '' }
            $argument = "--headless `"$CliPath`" run $name$exclusive -- $($Job.Command)"
        }
        elseif ($tracked -and -not $script:CliMissingWarned.ContainsKey($CliPath)) {
            $script:CliMissingWarned[$CliPath] = $true
            Write-Warning "The run wrapper $CliPath isn't installed, so jobs run unwrapped (no run log, no overnight lock). Deploy it (run.ps1 -Redeploy) and rerun install.ps1."
        }
    }
    $a = @{ Execute = $execute }
    if ($argument) { $a.Argument = $argument }
    if ($Job.WorkingDirectory) { $a.WorkingDirectory = $Job.WorkingDirectory }
    New-ScheduledTaskAction @a
}

function Remove-EmptyTaskFolder([string]$Folder) {
    # Best effort: drop the old owner's folder once its last task has moved to Scheduler.
    $Folder = ConvertTo-TaskFolder $Folder
    if ($Folder -eq '\' -or $Folder -ieq '\MindAttic\') { return }
    try {
        $svc = New-Object -ComObject 'Schedule.Service'
        $svc.Connect()
        $f = $svc.GetFolder($Folder.TrimEnd('\'))
        if ($f.GetTasks(1).Count -eq 0 -and $f.GetFolders(0).Count -eq 0) {
            $parent = $svc.GetFolder((Split-Path $Folder.TrimEnd('\') -Parent))
            $parent.DeleteFolder($f.Name, 0)
            Write-Output "  removed empty folder $Folder"
        }
    }
    catch { Write-Verbose "Could not tidy folder ${Folder}: $($_.Exception.Message)" }
}

function Install-SchedulerJobs {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$Manifest, [string]$TaskPath, [string]$ArrangementPath, [string]$CliPath)

    if (-not $ArrangementPath) { $ArrangementPath = Get-DefaultArrangementPath }
    if (-not $CliPath) { $CliPath = Get-DefaultCliPath }
    $folder = ConvertTo-TaskFolder $TaskPath
    $jobs = Read-JobManifest $Manifest $folder
    Write-Output "Scheduler: $($jobs.Count) job(s) from $Manifest -> $folder"
    $script:CliMissingWarned = @{}

    # The arranged starts of the "daily": "auto" jobs. Under -WhatIf the arranger isn't run (it
    # writes arrangement.json); a job with no slot shows its placeholder instead.
    $slots = Resolve-AutoSlots -Jobs $jobs -ArrangementPath $ArrangementPath -CliPath $CliPath -NoArrange:$WhatIfPreference

    # 1. Retire the old copies each job replaces.
    foreach ($job in $jobs) {
        foreach ($old in $job.Replaces) {
            $existing = Get-ScheduledTask -TaskPath $old.Path -TaskName $old.Name -ErrorAction SilentlyContinue
            if (-not $existing) { continue }
            if ($PSCmdlet.ShouldProcess("$($old.Path)$($old.Name)", "unregister (replaced by $folder$($job.Name))")) {
                Stop-ScheduledTask -TaskPath $old.Path -TaskName $old.Name -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskPath $old.Path -TaskName $old.Name -Confirm:$false
                Write-Output "  replaced $($old.Path)$($old.Name)"
                Remove-EmptyTaskFolder $old.Path
            }
        }
    }

    # 2. Register every job (overwriting its previous definition).
    $principal = New-ScheduledTaskPrincipal -UserId (Get-CurrentUserId) -LogonType Interactive -RunLevel Limited
    foreach ($job in $jobs) {
        if (-not $PSCmdlet.ShouldProcess("$folder$($job.Name)", 'register')) { continue }
        $settingsArgs = @{}
        if ($job.KeepRunning) {
            # A daemon: restarted if it fails, like the app-registered tasks it replaces. 999 (not 3, M.SCHD-T51:
            # a daemon that crashed more than 3 times - e.g. Orchestrator.Daemon repeatedly OOM-killed or
            # taskkilled - stayed down with no pacing until the next logon) is as high as Task Scheduler lets a
            # keepRunning job go; at the 1-minute interval below that's about 16.5 hours of retries before giving up.
            $settingsArgs.RestartCount = 999
            $settingsArgs.RestartInterval = [TimeSpan]::FromMinutes(1)
        }
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit $job.TimeLimit @settingsArgs
        $description = if ($job.Description) { "$($job.Description)`n`n$script:ManagedMarker" } else { $script:ManagedMarker }
        Register-ScheduledTask -TaskPath $folder -TaskName $job.Name `
            -Action (New-JobAction $job -CliPath $CliPath) -Trigger @(New-JobTriggers -Job $job -Slots $slots) `
            -Principal $principal -Settings $settings -Description $description -Force | Out-Null
        $note = if ($job.IsAuto) { " (auto, $($slots[$job.Name]))" } else { '' }
        Write-Output "  registered $folder$($job.Name)$note"

        # Enabled is part of the manifest, not left at the registration default: a job the
        # manifest (or the dashboard, via the manifest) says is disabled must stay disabled
        # across reinstalls, and one re-enabled in the manifest must come back.
        if ($job.Enabled) { Enable-ScheduledTask -TaskPath $folder -TaskName $job.Name | Out-Null }
        else {
            Disable-ScheduledTask -TaskPath $folder -TaskName $job.Name | Out-Null
            Write-Output "  disabled $folder$($job.Name)"
        }

        # A daemon shouldn't wait for the next logon, least of all right after its old task was
        # stopped above. Not a disabled one, though: starting it would run a daemon the manifest
        # switched off (or fail on the disabled task and abort the rest of the install).
        if ($job.KeepRunning -and $job.Enabled) {
            $state = (Get-ScheduledTask -TaskPath $folder -TaskName $job.Name).State
            if ($state -ne 'Running' -and $PSCmdlet.ShouldProcess("$folder$($job.Name)", 'start')) {
                Start-ScheduledTask -TaskPath $folder -TaskName $job.Name
                Write-Output "  started $folder$($job.Name)"
            }
        }
    }

    # 3. Unregister Scheduler tasks the manifest no longer lists (this folder only, not subfolders).
    $wanted = @{}
    foreach ($job in $jobs) { $wanted[$job.Name.ToLowerInvariant()] = $true }
    $present = @(Get-ScheduledTask -TaskPath $folder -ErrorAction SilentlyContinue |
        Where-Object { $_.TaskPath -ieq $folder })
    foreach ($t in $present) {
        if ($wanted.ContainsKey($t.TaskName.ToLowerInvariant())) { continue }
        if ($PSCmdlet.ShouldProcess("$folder$($t.TaskName)", 'unregister (no longer in jobs.json)')) {
            Stop-ScheduledTask -TaskPath $folder -TaskName $t.TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskPath $folder -TaskName $t.TaskName -Confirm:$false
            Write-Output "  removed $folder$($t.TaskName) (not in manifest)"
        }
    }
}

# Dot-sourcing (the tests do) only loads the functions above.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $Manifest) { $Manifest = Join-Path $PSScriptRoot 'jobs.json' }

Install-SchedulerJobs -Manifest $Manifest -TaskPath $TaskPath -ArrangementPath $ArrangementPath -CliPath $CliPath
