# Runs a full KrakenDeploy backup (pg_dump + data directory)
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
$backupDir = Join-Path $OutputDirectory "kraken-backup-$timestamp"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

Write-Host "Running backup to $backupDir..." -ForegroundColor Yellow

dotnet run --project src/KrakenDeploy.Server -- backup --to "$OutputDirectory"

if ($LASTEXITCODE -eq 0) {
    Write-Host "Backup complete: $backupDir" -ForegroundColor Green
}
else {
    Write-Host "Backup failed." -ForegroundColor Red
    exit $LASTEXITCODE
}
