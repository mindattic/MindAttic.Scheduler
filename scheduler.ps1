<#
.SYNOPSIS
  Scheduler's runtime job commands, for apps that create and cancel scheduled tasks while they run.

.DESCRIPTION
  jobs.json covers jobs known ahead of time. An app that makes tasks on the fly (Psst's
  reminders) calls these commands instead of registering tasks itself (HOUSE-LAW-11):

    scheduler job add <name> --owner <app> (--at <time> | --every <hh:mm:ss>) --run <command>
                      [--workdir <dir>] [--description <text>] [--replaces <task>]...
    scheduler job cancel <name> --owner <app>
    scheduler job list --owner <app> [--json]

  Each job is registered at \MindAttic\WatchDog\<owner>\<name>, the same way install.ps1
  registers manifest jobs: run through 'conhost.exe --headless' (no window, no focus stolen),
  as the current user, Interactive, limited rights. 'add' overwrites a job of the same name.

    --at       One run at a local time (ISO 'yyyy-MM-ddTHH:mm' is safest; anything
               [datetime] parses is accepted). Must be in the future. The task stays
               registered after it runs until its owner cancels it.
    --every    Repeat forever, at least a minute apart, starting at the next minute.
    --replaces A task this job takes over (full path, or a bare root-level name); it is
               unregistered and its folder removed once empty. Repeatable.

  'cancel' unregisters the task without stopping a run already under way, so a job may
  cancel itself as its last step. Exit code 0 on success, 1 on any error (message on stderr).

.EXAMPLE
  scheduler job add 1a2b3c --owner Psst --at 2026-10-19T09:30 --run "cmd.exe /c ""C:\x\1a2b3c.cmd"""
  scheduler job list --owner Psst --json
  scheduler job cancel 1a2b3c --owner Psst
#>

# No param block: PowerShell would try to bind '--owner' and friends as its own parameters.
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'install.ps1')

$script:JobRoot = '\MindAttic\WatchDog\'
$script:RuntimeMarker = 'Created at runtime through MindAttic.Scheduler (scheduler job add). Cancel it with scheduler job cancel; do not edit this task by hand.'

function Test-JobName([string]$Value, [string]$What) {
    if ($Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._ -]*$') {
        throw "$What '$Value' is missing or has characters a task name can't hold."
    }
}

function Get-OwnerFolder([string]$Owner, [string]$Root = $script:JobRoot) {
    Test-JobName $Owner 'Owner'
    (ConvertTo-TaskFolder $Root) + $Owner + '\'
}

function ConvertTo-FireTime([string]$Text, [datetime]$Now = (Get-Date)) {
    $formats = [string[]]@('yyyy-MM-ddTHH:mm', 'yyyy-MM-ddTHH:mm:ss', 'yyyy-MM-dd HH:mm', 'yyyy-MM-dd HH:mm:ss')
    $at = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($Text, $formats, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeLocal, [ref]$at) -and
        -not [datetime]::TryParse($Text, [ref]$at)) {
        throw "--at '$Text' is not a date and time (use yyyy-MM-ddTHH:mm)."
    }
    if ($at -le $Now) { throw "--at $($at.ToString('yyyy-MM-dd HH:mm:ss')) is not in the future." }
    $at
}

function Add-SchedulerJob {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Owner,
        [Nullable[datetime]]$At,
        [string]$Every,
        [Parameter(Mandatory)][string]$Command,
        [string]$WorkingDirectory,
        [string]$Description,
        [string[]]$Replaces = @(),
        [string]$Root = $script:JobRoot
    )

    Test-JobName $Name 'Job name'
    $folder = Get-OwnerFolder $Owner $Root
    if ([string]::IsNullOrWhiteSpace($Command)) { throw "Job '$Name' has no --run command." }
    if (($null -ne $At) -eq [bool]$Every) { throw "Job '$Name' needs exactly one of --at and --every." }

    if ($null -ne $At) {
        if ($At -le (Get-Date)) { throw "--at $($At.ToString('yyyy-MM-dd HH:mm:ss')) is not in the future." }
        $trigger = New-ScheduledTaskTrigger -Once -At $At
    }
    else {
        $span = ConvertTo-Span $Every '--every'
        if ($span -lt [TimeSpan]::FromMinutes(1)) { throw "--every must be at least 00:01:00." }
        $now = Get-Date
        $start = $now.Date.AddHours($now.Hour).AddMinutes($now.Minute + 1)
        $trigger = New-ScheduledTaskTrigger -Once -At $start -RepetitionInterval $span
    }

    $root = ConvertTo-TaskFolder $Root
    foreach ($r in $Replaces | Where-Object { $_ }) {
        $old = Split-TaskFullName $r
        if ($old.Path -like "$root*") { throw "--replaces '$r' is a Scheduler task; cancel it instead." }
        $existing = Get-ScheduledTask -TaskPath $old.Path -TaskName $old.Name -ErrorAction SilentlyContinue
        if (-not $existing) { continue }
        Stop-ScheduledTask -TaskPath $old.Path -TaskName $old.Name -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskPath $old.Path -TaskName $old.Name -Confirm:$false
        Write-Output "replaced $($old.Path)$($old.Name)"
        Remove-EmptyTaskFolder $old.Path
    }

    $job = [pscustomobject]@{ Command = $Command; WorkingDirectory = $WorkingDirectory }
    $principal = New-ScheduledTaskPrincipal -UserId (Get-CurrentUserId) -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -MultipleInstances IgnoreNew
    $text = if ($Description) { "$Description`n`n$script:RuntimeMarker" } else { $script:RuntimeMarker }
    Register-ScheduledTask -TaskPath $folder -TaskName $Name -Action (New-JobAction $job) -Trigger $trigger `
        -Principal $principal -Settings $settings -Description $text -Force | Out-Null
    Write-Output "registered $folder$Name"
}

function Remove-SchedulerJob {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Owner,
        [string]$Root = $script:JobRoot
    )

    Test-JobName $Name 'Job name'
    $folder = Get-OwnerFolder $Owner $Root
    if (-not (Get-ScheduledTask -TaskPath $folder -TaskName $Name -ErrorAction SilentlyContinue)) {
        throw "No job '$Name' for owner '$Owner' ($folder$Name)."
    }
    # No Stop-ScheduledTask: a run already under way finishes, and a job can cancel itself.
    Unregister-ScheduledTask -TaskPath $folder -TaskName $Name -Confirm:$false
    Write-Output "cancelled $folder$Name"
    Remove-EmptyTaskFolder $folder
}

function Get-SchedulerJob {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Owner,
        [string]$Root = $script:JobRoot
    )

    $folder = Get-OwnerFolder $Owner $Root
    $tasks = @(Get-ScheduledTask -TaskPath $folder -ErrorAction SilentlyContinue | Where-Object { $_.TaskPath -ieq $folder })
    foreach ($t in $tasks) {
        $info = Get-ScheduledTaskInfo -TaskPath $folder -TaskName $t.TaskName -ErrorAction SilentlyContinue
        $arguments = [string]$t.Actions[0].Arguments
        $nextRun = if ($info -and $info.NextRunTime) { $info.NextRunTime.ToString('s') } else { $null }
        $lastRun = if ($info -and $info.LastRunTime -and $info.LastRunTime.Year -gt 2000) { $info.LastRunTime.ToString('s') } else { $null }
        [pscustomobject]@{
            name    = $t.TaskName
            owner   = $Owner
            task    = "$folder$($t.TaskName)"
            state   = [string]$t.State
            nextRun = $nextRun
            lastRun = $lastRun
            command = if ($arguments.StartsWith('--headless ')) { $arguments.Substring(11) } else { "$($t.Actions[0].Execute) $arguments".Trim() }
        }
    }
}

function Read-JobArguments([string[]]$Arguments) {
    # '<positional> --flag value --switch ...' -> @{ Positional = @(...); Flags = @{ flag = @(values) } }
    $switches = @('json')
    $flags = @{}
    $positional = @()
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $a = $Arguments[$i]
        if ($a -notlike '--?*') { $positional += $a; continue }
        $key = $a.Substring(2).ToLowerInvariant()
        if ($switches -contains $key) { $flags[$key] = @($true); continue }
        if ($i + 1 -ge $Arguments.Count) { throw "$a needs a value." }
        $i++
        $flags[$key] = @($flags[$key] | Where-Object { $null -ne $_ }) + $Arguments[$i]
    }
    [pscustomobject]@{ Positional = $positional; Flags = $flags }
}

function Get-Flag($Parsed, [string]$Key, [switch]$Required) {
    $v = $Parsed.Flags[$Key]
    if ($null -eq $v) {
        if ($Required) { throw "--$Key is required." }
        return $null
    }
    if (@($v).Count -gt 1) { throw "--$Key is given more than once." }
    @($v)[0]
}

function Assert-KnownFlags($Parsed, [string[]]$Known) {
    foreach ($k in $Parsed.Flags.Keys) {
        if ($Known -notcontains $k) { throw "Unknown option --$k." }
    }
}

function Invoke-SchedulerCommand([string[]]$Arguments) {
    $usage = @'
usage: scheduler job add <name> --owner <app> (--at <time> | --every <hh:mm:ss>) --run <command>
                         [--workdir <dir>] [--description <text>] [--replaces <task>]...
       scheduler job cancel <name> --owner <app>
       scheduler job list --owner <app> [--json]
'@
    if ($Arguments.Count -lt 2 -or $Arguments[0] -ne 'job') { throw $usage }
    $verb = $Arguments[1].ToLowerInvariant()
    $parsed = Read-JobArguments @($Arguments | Select-Object -Skip 2)

    switch ($verb) {
        'add' {
            Assert-KnownFlags $parsed @('owner', 'at', 'every', 'run', 'workdir', 'description', 'replaces')
            if ($parsed.Positional.Count -ne 1) { throw "job add takes exactly one job name.`n$usage" }
            $at = Get-Flag $parsed 'at'
            $splat = @{
                Name             = $parsed.Positional[0]
                Owner            = Get-Flag $parsed 'owner' -Required
                Command          = Get-Flag $parsed 'run' -Required
                Every            = Get-Flag $parsed 'every'
                WorkingDirectory = Get-Flag $parsed 'workdir'
                Description      = Get-Flag $parsed 'description'
                Replaces         = @($parsed.Flags['replaces'] | Where-Object { $_ })
            }
            if ($null -ne $at) { $splat.At = ConvertTo-FireTime $at }
            Add-SchedulerJob @splat
        }
        'cancel' {
            Assert-KnownFlags $parsed @('owner')
            if ($parsed.Positional.Count -ne 1) { throw "job cancel takes exactly one job name.`n$usage" }
            Remove-SchedulerJob -Name $parsed.Positional[0] -Owner (Get-Flag $parsed 'owner' -Required)
        }
        'list' {
            Assert-KnownFlags $parsed @('owner', 'json')
            if ($parsed.Positional.Count -ne 0) { throw "job list takes no job name.`n$usage" }
            $jobs = @(Get-SchedulerJob -Owner (Get-Flag $parsed 'owner' -Required))
            if ($parsed.Flags['json']) { ConvertTo-Json -InputObject $jobs -Depth 3 -Compress }
            elseif ($jobs.Count -eq 0) { 'No jobs.' }
            else { $jobs | Format-Table name, nextRun, state, command -AutoSize | Out-String -Width 4096 | ForEach-Object TrimEnd }
        }
        default { throw $usage }
    }
}

# Dot-sourcing (the tests do) only loads the functions above.
if ($MyInvocation.InvocationName -eq '.') { return }

try {
    Invoke-SchedulerCommand @($args)
    exit 0
}
catch {
    [Console]::Error.WriteLine("scheduler: $($_.Exception.Message)")
    exit 1
}
