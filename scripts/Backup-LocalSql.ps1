#Requires -Version 5.1
<#
.SYNOPSIS
    Nightly native backup of every local SQL Server database (M.SCHD-T73), the job Sql.BackupLocalNightly.

.DESCRIPTION
    Backup-LocalSql.ps1 [-Instances ...] [-Root R:\Backup\MindAttic\sql] [-KeepDays 7] [-WhatIf]

    For each instance (LocalDB and the full local SQL Server by default) every online user database is backed up
    with BACKUP DATABASE ... WITH CHECKSUM, INIT (plus COMPRESSION where the edition supports it; LocalDB/Express
    doesn't) to <Root>\<instance>\<db>\<db>_<yyyyMMdd_HHmmss>.bak, then checked with RESTORE VERIFYONLY.
    A file is written as .bak.part and renamed only once verified, so a .bak is always a good backup.

    Skipped: snapshots, read-only databases (their last backup stays), the frozen pre-copy databases renamed
    <name>_Local_<yyyyMMdd> by the 2026-11-25 Azure-to-local move (MAT-T255), and scratch test databases (names
    containing Tests_ followed by a date, or .Test-).

    Retention: .bak files older than -KeepDays are deleted, but never the newest one of a database, so a database
    that stops being backed up keeps its last copy. Leftover .bak.part files from a crash are removed.

    Exit code: 0 all good, 1 at least one database failed (the others still ran). Log: <Root>\logs\backup-<date>.log.
#>
param(
    [string[]]$Instances = @('(localdb)\MSSQLLocalDB', 'localhost'),
    [string]$Root = 'R:\Backup\MindAttic\sql',
    [int]$KeepDays = 7,
    [int]$CommandTimeoutSeconds = 0,
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'
$logDir = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ("backup-{0:yyyyMMdd}.log" -f (Get-Date))
function Say([string]$m) { $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m; Add-Content -LiteralPath $log -Value $line; Write-Host $line }

function Invoke-Sql([string]$instance, [string]$sql) {
    $c = New-Object System.Data.SqlClient.SqlConnection "Server=$instance;Database=master;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=120"
    $c.Open()
    try {
        $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = $CommandTimeoutSeconds
        $t = New-Object System.Data.DataTable; $t.Load($cmd.ExecuteReader()); return , $t
    } finally { $c.Close() }
}
function Folder([string]$instance) { ($instance -replace '[\\/:*?"<>|()]', '_').Trim('_') }

$failed = 0
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
Say "backup start: instances $($Instances -join ', '); root $Root; keep $KeepDays days"
foreach ($instance in $Instances) {
    try {
        $info = Invoke-Sql $instance "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int) AS ed, CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition"
        $edition = [string]$info.Rows[0].edition
        $compress = $edition -notmatch 'Express'
        $dbs = Invoke-Sql $instance @"
SELECT name FROM sys.databases
WHERE database_id > 4 AND state_desc = 'ONLINE' AND source_database_id IS NULL AND is_read_only = 0
  AND name NOT LIKE '%Tests[_]2[0-9][0-9][0-9][0-9][0-9][0-9][0-9][_]%'
  AND name NOT LIKE '%[_]Local[_]2[0-9][0-9][0-9][0-9][0-9][0-9][0-9]'
  AND name NOT LIKE '%.Test-%'
ORDER BY name
"@
    } catch { Say "FAILED instance ${instance}: $($_.Exception.Message)"; $failed++; continue }
    Say "$instance ($edition, compression $compress): $($dbs.Rows.Count) database(s)"
    $instDir = Join-Path $Root (Folder $instance)
    foreach ($row in $dbs.Rows) {
        $db = [string]$row.name
        $dir = Join-Path $instDir $db
        $final = Join-Path $dir "${db}_$stamp.bak"
        $part = "$final.part"
        if ($WhatIf) { Say "WHATIF $instance/$db -> $final"; continue }
        try {
            New-Item -ItemType Directory -Force $dir | Out-Null
            $q = $db.Replace(']', ']]'); $p = $part.Replace("'", "''")
            $opts = 'CHECKSUM, INIT, FORMAT' + $(if ($compress) { ', COMPRESSION' } else { '' })
            $sw = [Diagnostics.Stopwatch]::StartNew()
            [void](Invoke-Sql $instance "BACKUP DATABASE [$q] TO DISK = N'$p' WITH $opts")
            [void](Invoke-Sql $instance "RESTORE VERIFYONLY FROM DISK = N'$p' WITH CHECKSUM")
            Move-Item -LiteralPath $part -Destination $final
            Say ("ok     {0}/{1}: {2:N0} MB in {3:N0}s" -f $instance, $db, ((Get-Item $final).Length / 1MB), $sw.Elapsed.TotalSeconds)
        } catch {
            $failed++
            Say "FAILED $instance/${db}: $($_.Exception.Message)"
            if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
        }
    }
}

# Retention: per database folder, drop .bak older than KeepDays but always keep the newest; clear crash leftovers.
if (-not $WhatIf -and (Test-Path $Root)) {
    $cutoff = (Get-Date).AddDays(-$KeepDays)
    Get-ChildItem $Root -Recurse -Filter '*.bak.part' -File | Where-Object { $_.LastWriteTime -lt (Get-Date).AddHours(-12) } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; Say "removed leftover $($_.FullName)" }
    Get-ChildItem $Root -Recurse -Filter '*.bak' -File | Group-Object DirectoryName | ForEach-Object {
        $files = $_.Group | Sort-Object LastWriteTime -Descending
        $files | Select-Object -Skip 1 | Where-Object { $_.LastWriteTime -lt $cutoff } | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force; Say "pruned $($_.FullName)"
        }
    }
}
Say "backup done: $failed failure(s)"
exit ([int]($failed -gt 0))
