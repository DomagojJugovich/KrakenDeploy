#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Drops and recreates the development database, then re-applies all migrations.

.DESCRIPTION
    WARNING: All data in the target database is permanently destroyed.
    Uses the same DesignTimeDbContextFactory connection string as migrate.ps1.
    Supports -WhatIf to preview without executing.

.EXAMPLE
    .\scripts\reset-db.ps1 -WhatIf   # preview only
    .\scripts\reset-db.ps1            # actually drops + recreates
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent

$DataProject = "$RepoRoot/src/KrakenDeploy.Server.Data"

if (-not $PSCmdlet.ShouldProcess('krakendeploy_dev', 'Drop and recreate database')) {
    return
}

Write-Host "Dropping database..." -ForegroundColor Yellow
dotnet ef database drop --force --project $DataProject --startup-project $DataProject
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Applying migrations..." -ForegroundColor Cyan
dotnet ef database update --project $DataProject --startup-project $DataProject
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Database reset complete." -ForegroundColor Green
