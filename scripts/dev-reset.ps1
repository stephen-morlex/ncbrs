<#
.SYNOPSIS
    Resets the local NCBRS development environment to a clean seed.

.DESCRIPTION
    Development only. Drops the local registry, read model and district queue,
    and clears Kafka, so the next API start in Development migrates and seeds a
    clean South Sudan dataset from scratch.

    Why a reset is ever needed: the development seeders are idempotent and keyed
    by id and by registrar subject, so they **never rebind** an existing row. A
    database seeded before a seed change keeps the old bindings -- this is how a
    dev registrar stayed bound to a facility from the retired scaffold long after
    the seed moved on -- and load runs leave thousands of synthetic registrations
    and LOADTEST- devices behind. A restart cannot correct either; only an empty
    database can.

    Kafka is cleared too, deliberately. The reporting read model is rebuilt from
    the topics, so resetting the database alone would replay every old event --
    synthetic load included -- straight back into the dashboard.

    Backs up first unless -NoBackup is given: a pg_dump of the compose database
    and copies of the SQLite files, under backup/dev-reset/<timestamp>/ (which
    .gitignore already excludes). A reset is then always undoable.

    What it never touches: Keycloak (its realm is re-imported on start), and the
    Postgres WAL-archive volume (the A6 recovery setup).

.PARAMETER Yes
    Required to actually do anything. Without it the script prints what it
    would destroy and exits.

.PARAMETER NoBackup
    Skip the backup.

.EXAMPLE
    ./scripts/dev-reset.ps1            # shows the plan, changes nothing
    ./scripts/dev-reset.ps1 -Yes       # backs up, then resets
#>
[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$NoBackup
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$sqliteFiles = @('ncbrs.db', 'ncbrs-readmodel.db', 'ncbrs-district.db') |
    ForEach-Object { $_, "$_-wal", "$_-shm" }

function Test-ComposeService([string]$service) {
    try {
        $id = docker compose ps -q $service 2>$null
        return -not [string]::IsNullOrWhiteSpace($id)
    } catch {
        return $false
    }
}

$dockerUp = $true
try { docker info *> $null; if ($LASTEXITCODE -ne 0) { $dockerUp = $false } } catch { $dockerUp = $false }

$postgresUp = $dockerUp -and (Test-ComposeService 'postgres')
$kafkaUp = $dockerUp -and (Test-ComposeService 'kafka')

Write-Host ''
Write-Host 'NCBRS development reset' -ForegroundColor Cyan
Write-Host 'This will destroy LOCAL DEVELOPMENT data only:'
Write-Host '  - local NCBRS services (Api, Consumer, Relay, District) are stopped'
Write-Host "  - SQLite files at the repo root: $((@('ncbrs.db', 'ncbrs-readmodel.db', 'ncbrs-district.db')) -join ', ')"
if ($postgresUp) {
    Write-Host '  - compose Postgres: the registry database is dropped and recreated empty (WAL-archive volume kept)'
} else {
    Write-Host '  - compose Postgres: not running, skipped' -ForegroundColor DarkGray
}
if ($kafkaUp) {
    Write-Host '  - compose Kafka: recreated, which clears every topic and consumer offset'
} else {
    Write-Host '  - compose Kafka: not running, skipped' -ForegroundColor DarkGray
}
Write-Host '  - Keycloak: untouched'
if (-not $NoBackup) {
    Write-Host '  A backup is taken first (backup/dev-reset/<timestamp>/).'
}
Write-Host ''

if (-not $Yes) {
    Write-Host 'Nothing changed. Re-run with -Yes to reset.' -ForegroundColor Yellow
    exit 1
}

# 1. Stop local services. They hold the SQLite files open, and a running Relay
#    would republish the old outbox into the freshly cleared Kafka.
$services = Get-Process -Name 'NCBRS.Api', 'NCBRS.Consumer', 'NCBRS.Relay', 'NCBRS.District' -ErrorAction SilentlyContinue
if ($services) {
    $services | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Write-Host "Stopped $($services.Count) local service process(es)."
}

# 2. Back up.
if (-not $NoBackup) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupDir = Join-Path $repo "backup/dev-reset/$stamp"
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

    foreach ($file in $sqliteFiles) {
        if (Test-Path $file) { Copy-Item $file $backupDir }
    }

    if ($postgresUp) {
        # Custom format, so pg_restore can put it back. Written inside the
        # container and copied out as a file: piping pg_dump through PowerShell
        # would not do, because Windows PowerShell turns native output into
        # text lines and would silently corrupt a binary dump -- the one safety
        # net this script relies on.
        docker compose exec -T postgres sh -c 'pg_dump -U $POSTGRES_USER -d $POSTGRES_DB --format=custom --file=/tmp/registry.dump'
        if ($LASTEXITCODE -ne 0) { throw "pg_dump failed (exit $LASTEXITCODE); nothing has been reset." }

        docker compose cp postgres:/tmp/registry.dump (Join-Path $backupDir 'registry.dump') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Copying the dump out failed (exit $LASTEXITCODE); nothing has been reset." }

        docker compose exec -T postgres rm -f /tmp/registry.dump | Out-Null
    }

    Write-Host "Backed up to $backupDir"
}

# 3. SQLite.
foreach ($file in $sqliteFiles) {
    if (Test-Path $file) { Remove-Item $file -Force }
}
Write-Host 'Removed local SQLite files.'

# 4. Postgres: drop and recreate the database rather than the volume, so the
#    WAL archive (on its own volume) and the cluster configuration survive.
if ($postgresUp) {
    $db = (docker compose exec -T postgres printenv POSTGRES_DB).Trim()
    if ([string]::IsNullOrWhiteSpace($db)) { throw 'Could not read POSTGRES_DB from the container.' }

    # dropdb/createdb inside the container: no SQL quoting to get wrong across
    # PowerShell's native-argument handling. --force ends open connections.
    docker compose exec -T postgres sh -c 'dropdb -U $POSTGRES_USER --if-exists --force $POSTGRES_DB && createdb -U $POSTGRES_USER $POSTGRES_DB'
    if ($LASTEXITCODE -ne 0) { throw "Resetting the Postgres database failed (exit $LASTEXITCODE)." }
    Write-Host "Recreated Postgres database '$db' (empty)."
}

# 5. Kafka: the compose service has no volume, so recreating the container
#    (renewing any anonymous volume too) clears every topic and offset.
if ($kafkaUp) {
    docker compose up -d --force-recreate --renew-anon-volumes --no-deps kafka | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Recreating Kafka failed (exit $LASTEXITCODE)." }
    Write-Host 'Recreated Kafka (topics and consumer offsets cleared).'
}

Write-Host ''
Write-Host 'Reset complete. Start the API in Development to migrate and seed:' -ForegroundColor Green
Write-Host '  SQLite:   dotnet run --project src/NCBRS.Api'
Write-Host '  Postgres: set Database__Provider=Postgres and ConnectionStrings__Default, then the same'
Write-Host 'then the Relay and Consumer. The read model rebuilds from the (now empty) topics.'

Write-Host 'To undo: restore the SQLite files from the backup folder, and for Postgres'
Write-Host '  docker compose cp <backup>/registry.dump postgres:/tmp/r.dump'
Write-Host '  docker compose exec -T postgres sh -c ''pg_restore -U $POSTGRES_USER -d $POSTGRES_DB --clean --if-exists /tmp/r.dump'''
