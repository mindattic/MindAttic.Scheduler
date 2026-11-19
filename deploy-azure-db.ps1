<#
.SYNOPSIS
  Creates the MindAttic.Scheduler run-log database on Azure SQL and points this machine at it
  (M.SCHD-T42).

.DESCRIPTION
  Modelled on IdeaCms/infra/provision.ps1 and migrate.ps1. Safe to re-run: every step checks first.

    1. az sql db create MindAttic.Scheduler on sql-mindattic-f65744 (rg-mindattic-data), Basic,
       2 GB, locally-redundant backups. The server's ten free databases are all in use, so this one
       is BILLED (about $0.16/day, ~$5/month); the script refuses to create it without -ConfirmCost.
    2. Opens a temporary firewall rule for this machine's IP (always removed afterwards).
    3. Creates the service-principal user (CREATE USER ... FROM EXTERNAL PROVIDER) with
       db_datareader, db_datawriter and db_ddladmin. The principal is the one MindAttic.Tickets
       already signs in with (read from its database.json), unless -AppId/-ClientSecret are given.
    4. Runs `mindattic-scheduler db migrate` against it.
    5. Writes the service-principal connection string to
       %APPDATA%\MindAttic\MindAttic.Scheduler\database.json (the old one, if any, kept as .bak).
    6. Flushes any local spool, then checks a test row round-trips (and deletes it).

  Afterwards, add MindAttic.Scheduler to AlwaysOnDatabases in
  docs/ops/azure-migration/Backup-Scheduled.ps1 (the workspace root repo) if it isn't there yet.

.PARAMETER ConfirmCost
  Required to create the database: it is billed (Basic tier, about $0.16/day).

.PARAMETER Cli
  mindattic-scheduler.exe to migrate with. Defaults to the published one, else the Debug build.

.EXAMPLE
  .\deploy-azure-db.ps1 -ConfirmCost
#>
[CmdletBinding()]
param(
    [switch]$ConfirmCost,
    [string]$ResourceGroup = 'rg-mindattic-data',
    [string]$Server = 'sql-mindattic-f65744',
    [string]$Database = 'MindAttic.Scheduler',
    [string]$AppId,
    [string]$ClientSecret,
    [string]$Cli
)

$ErrorActionPreference = 'Stop'

function Write-Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

# See IdeaCms/infra/provision.ps1: judge az by its exit code, never by stderr.
function Invoke-Az {
    $arguments = $args
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & az @arguments 2>&1; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) {
        $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "az $($arguments -join ' ') failed (exit $code):`n$text"
    }
    $output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
}

function Test-Az {
    $arguments = $args
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & az @arguments 2>&1; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { return $null }
    ($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join ''
}

# --- Preflight -----------------------------------------------------------------------------------

Write-Step 'Preflight'
if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'Azure CLI (az) is not on PATH.' }
$account = Test-Az account show
if (-not $account) { throw 'Not logged in. Run: az login' }
Write-Host "Subscription : $(($account | ConvertFrom-Json).name)"

if (-not $Cli) {
    $Cli = @('C:\Apps\MindAttic.Scheduler\mindattic-scheduler.exe',
             (Join-Path $PSScriptRoot 'src\MindAttic.Scheduler.Cli\bin\Debug\net10.0-windows\mindattic-scheduler.exe')) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $Cli -or -not (Test-Path -LiteralPath $Cli)) { throw 'mindattic-scheduler.exe not found: run release.ps1 (or build) first, or pass -Cli.' }
Write-Host "CLI          : $Cli"

if (-not $AppId -or -not $ClientSecret) {
    $ticketsConfig = Join-Path $env:APPDATA 'MindAttic\MindAttic.Tickets\database.json'
    if (-not (Test-Path -LiteralPath $ticketsConfig)) { throw "Pass -AppId and -ClientSecret (no $ticketsConfig to borrow the service principal from)." }
    $cs = (Get-Content -LiteralPath $ticketsConfig -Raw | ConvertFrom-Json).SqlServer
    $parts = @{}
    foreach ($kv in ($cs -split ';')) { $i = $kv.IndexOf('='); if ($i -gt 0) { $parts[$kv.Substring(0, $i).Trim().ToLowerInvariant()] = $kv.Substring($i + 1).Trim() } }
    if (-not $AppId) { $AppId = $parts['user id'] }
    if (-not $ClientSecret) { $ClientSecret = $parts['password'] }
    if (-not $AppId -or -not $ClientSecret) { throw "$ticketsConfig is not a service-principal connection; pass -AppId and -ClientSecret." }
}
$spName = Invoke-Az ad sp show --id $AppId --query displayName -o tsv
$spObjectId = Invoke-Az ad sp show --id $AppId --query id -o tsv
Write-Host "Principal    : $spName ($AppId)"
$fqdn = "$Server.database.windows.net"

# --- 1. The database ----------------------------------------------------------------------------

Write-Step "Database $Database on $Server"
$existing = Test-Az sql db show -g $ResourceGroup -s $Server -n $Database --query name -o tsv
if ($existing) { Write-Host '  already exists, left as it is' }
else {
    if (-not $ConfirmCost) {
        throw "Creating $Database is billed (Basic tier, about `$0.16/day, ~`$5/month: the server's free databases are all in use). Re-run with -ConfirmCost once that's agreed."
    }
    Invoke-Az sql db create -g $ResourceGroup -s $Server -n $Database --service-objective Basic `
        --max-size 2GB --backup-storage-redundancy Local --output none | Out-Null
    Write-Host '  created (Basic, 2 GB, local backup redundancy)'
}

# --- 2-6. Firewall, user, schema, config, round trip ---------------------------------------------

$myIp = (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json').ip
$rule = 'scheduler-deploy-script'
Write-Step "Temporary firewall rule for $myIp"
Invoke-Az sql server firewall-rule create -g $ResourceGroup -s $Server -n $rule `
    --start-ip-address $myIp --end-ip-address $myIp --output none | Out-Null
try {
    Write-Step "Service-principal user [$spName]"
    $token = Invoke-Az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
    $conn = New-Object System.Data.SqlClient.SqlConnection "Server=tcp:$fqdn,1433;Database=$Database;Encrypt=True;Connect Timeout=60"
    $conn.AccessToken = $token
    $conn.Open()
    try {
        $sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$spName')
    CREATE USER [$spName] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '$spObjectId';
ALTER ROLE db_datareader ADD MEMBER [$spName];
ALTER ROLE db_datawriter ADD MEMBER [$spName];
ALTER ROLE db_ddladmin ADD MEMBER [$spName];
"@
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120
        [void]$cmd.ExecuteNonQuery()
        Write-Host '  datareader, datawriter, ddladmin'
    }
    finally { $conn.Dispose() }

    $connectionString = "Server=tcp:$fqdn,1433;Database=$Database;Authentication=Active Directory Service Principal;User Id=$AppId;Password=$ClientSecret;Encrypt=True;Connect Timeout=60;Application Name=MindAttic.Scheduler"

    Write-Step 'Schema (mindattic-scheduler db migrate)'
    $env:MINDATTIC_SCHEDULER_SQL = $connectionString
    try {
        & $Cli db migrate
        if ($LASTEXITCODE -ne 0) { throw "db migrate failed (exit $LASTEXITCODE)." }
    }
    finally { Remove-Item Env:MINDATTIC_SCHEDULER_SQL -ErrorAction SilentlyContinue }

    Write-Step 'database.json'
    $configPath = Join-Path $env:APPDATA 'MindAttic\MindAttic.Scheduler\database.json'
    New-Item -ItemType Directory -Force -Path (Split-Path $configPath) | Out-Null
    if (Test-Path -LiteralPath $configPath) { Copy-Item -LiteralPath $configPath "$configPath.bak" -Force }
    [IO.File]::WriteAllText($configPath, (@{ SqlServer = $connectionString } | ConvertTo-Json))
    Write-Host "  $configPath"

    Write-Step 'Spool flush and round trip'
    & $Cli db flush
    $probe = "Scheduler.DeployCheck.$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    & $Cli run $probe -- cmd.exe /c exit 0
    $rows = @(& $Cli runs --job $probe --json | ConvertFrom-Json)
    if ($rows.Count -ne 1 -or $rows[0].outcome -ne 'succeeded') { throw "Round trip failed: expected one succeeded $probe row, got $($rows.Count)." }
    Write-Host "  $probe recorded as succeeded"
    $env:MINDATTIC_SCHEDULER_SQL = $connectionString
    try {
        $conn = New-Object System.Data.SqlClient.SqlConnection "Server=tcp:$fqdn,1433;Database=$Database;Encrypt=True;Connect Timeout=60"
        $conn.AccessToken = $token
        $conn.Open()
        try {
            $cmd = $conn.CreateCommand(); $cmd.CommandText = "DELETE dbo.JobRun WHERE JobName = @n"
            [void]$cmd.Parameters.AddWithValue('@n', $probe); [void]$cmd.ExecuteNonQuery()
        }
        finally { $conn.Dispose() }
    }
    finally { Remove-Item Env:MINDATTIC_SCHEDULER_SQL -ErrorAction SilentlyContinue }
}
finally {
    Test-Az sql server firewall-rule delete -g $ResourceGroup -s $Server -n $rule --output none | Out-Null
    Write-Host "`nFirewall rule $rule removed."
}

Write-Step 'Done'
Write-Host "Runs are now recorded in $Database on $fqdn." -ForegroundColor Green
Write-Host 'Remember: add it to AlwaysOnDatabases in docs/ops/azure-migration/Backup-Scheduled.ps1.'
