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
    3. Any task left directly in \MindAttic\Scheduler\ that the manifest no longer lists is
       unregistered. Subfolders (\MindAttic\Scheduler\<owner>\, used by runtime jobs) are left
       alone.

  No elevation is needed: every task runs as the current user, Interactive, limited rights.

.PARAMETER Manifest
  The jobs manifest. Defaults to jobs.json next to this script.

.PARAMETER TaskPath
  The Task Scheduler folder the jobs live in. Leave it at the default; the tests point it at a
  scratch folder.

.EXAMPLE
  .\install.ps1 -WhatIf     # show what would change, touch nothing
  .\install.ps1             # apply
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Manifest,
    [string]$TaskPath = '\MindAttic\Scheduler\'
)

$ErrorActionPreference = 'Stop'

$script:ManagedMarker = 'Managed by MindAttic.Scheduler (jobs.json). Edit the manifest and rerun install.ps1; do not edit this task by hand.'

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

function Test-ScheduleEntry($Entry, [string]$Job) {
    $kinds = @('every', 'daily', 'atLogon') | Where-Object { $null -ne $Entry.$_ }
    if (@($kinds).Count -ne 1) {
        throw "Job '$Job': each schedule entry needs exactly one of every, daily, atLogon."
    }
    switch ($kinds) {
        'every' {
            $span = ConvertTo-Span $Entry.every "Job '$Job': every"
            if ($span -lt [TimeSpan]::FromMinutes(1)) { throw "Job '$Job': every must be at least 00:01:00." }
        }
        'daily' {
            if ($Entry.daily -notmatch '^([01]\d|2[0-3]):[0-5]\d$') {
                throw "Job '$Job': daily '$($Entry.daily)' must be a 24-hour HH:mm time."
            }
        }
        'atLogon' {
            if ($Entry.atLogon -ne $true) { throw "Job '$Job': atLogon must be true." }
        }
    }
}

function Read-JobManifest([string]$Path, [string]$TaskPath = '\MindAttic\Scheduler\') {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Manifest not found: $Path" }
    $doc = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
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

        $replaces = @()
        foreach ($r in @($j.replaces | Where-Object { $_ })) {
            $old = Split-TaskFullName ([string]$r)
            if ($old.Path -ieq $folder) {
                throw "Job '$name' replaces '$r', which is inside $folder itself; that would delete a Scheduler job."
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

        $jobs += [pscustomobject]@{
            Name             = $name
            Command          = [Environment]::ExpandEnvironmentVariables([string]$j.command)
            WorkingDirectory = if ($j.workingDirectory) { [Environment]::ExpandEnvironmentVariables([string]$j.workingDirectory) } else { $null }
            Description      = [string]$j.description
            Schedule         = $schedule
            Replaces         = $replaces
            TimeLimit        = $timeLimit
            KeepRunning      = $keepRunning
        }
    }
    , $jobs
}

function Get-CurrentUserId { "$env:USERDOMAIN\$env:USERNAME" }

function New-JobTriggers($Job) {
    foreach ($entry in $Job.Schedule) {
        if ($null -ne $entry.every) {
            # Anchored at today's midnight so a rerun produces the same definition. No
            # -RepetitionDuration: on Windows 10+ that means 'repeat indefinitely'.
            New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
                -RepetitionInterval (ConvertTo-Span $entry.every 'every')
        }
        elseif ($null -ne $entry.daily) {
            New-ScheduledTaskTrigger -Daily -At ([datetime]::ParseExact($entry.daily, 'HH:mm', $null))
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

function New-JobAction($Job) {
    # conhost --headless gives the command a console that is never shown, so a console exe run
    # by an Interactive task neither flashes a window nor takes focus. A GUI-subsystem program
    # gets no console anyway, and conhost would start it and exit at once (leaving the task
    # 'finished' while the program runs untracked), so it is started directly.
    $parts = Split-CommandLine $Job.Command
    if (Test-GuiProgram $parts.Program) {
        $execute = $parts.Program
        $argument = $parts.Arguments
    }
    else {
        $execute = Join-Path $env:SystemRoot 'System32\conhost.exe'
        $argument = '--headless ' + $Job.Command
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
    param([string]$Manifest, [string]$TaskPath)

    $folder = ConvertTo-TaskFolder $TaskPath
    $jobs = Read-JobManifest $Manifest $folder
    Write-Output "Scheduler: $($jobs.Count) job(s) from $Manifest -> $folder"

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
            # A daemon: restarted if it fails, like the app-registered tasks it replaces.
            $settingsArgs.RestartCount = 3
            $settingsArgs.RestartInterval = [TimeSpan]::FromMinutes(1)
        }
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit $job.TimeLimit @settingsArgs
        $description = if ($job.Description) { "$($job.Description)`n`n$script:ManagedMarker" } else { $script:ManagedMarker }
        Register-ScheduledTask -TaskPath $folder -TaskName $job.Name `
            -Action (New-JobAction $job) -Trigger @(New-JobTriggers $job) `
            -Principal $principal -Settings $settings -Description $description -Force | Out-Null
        Write-Output "  registered $folder$($job.Name)"

        # A daemon shouldn't wait for the next logon, least of all right after its old task was
        # stopped above.
        if ($job.KeepRunning) {
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

Install-SchedulerJobs -Manifest $Manifest -TaskPath $TaskPath
