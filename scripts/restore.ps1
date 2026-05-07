# Restores a KrakenDeploy backup bundle
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupDirectory
)

if (-not (Test-Path $BackupDirectory)) {
    Write-Host "Backup directory not found: $BackupDirectory" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path (Join-Path $BackupDirectory "manifest.json"))) {
    Write-Host "manifest.json not found — not a valid KrakenDeploy backup." -ForegroundColor Red
    exit 1
}

Write-Host "Restoring from $BackupDirectory..." -ForegroundColor Yellow

dotnet run --project src/KrakenDeploy.Server -- restore --from "$BackupDirectory"

if ($LASTEXITCODE -eq 0) {
    Write-Host "Restore complete." -ForegroundColor Green
}
else {
    Write-Host "Restore failed." -ForegroundColor Red
    exit $LASTEXITCODE
}
