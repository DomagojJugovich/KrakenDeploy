#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Applies pending EF Core migrations to the development database.

.DESCRIPTION
    Reads the connection string from the DesignTimeDbContextFactory fallback
    (localhost:5432, db krakendeploy_dev, user postgres / password postgres),
    or from the KRAKEN_DESIGN_TIME_CONNECTION_STRING environment variable for
    a different target.

.EXAMPLE
    .\scripts\migrate.ps1

    # Against a custom database:
    $env:KRAKEN_DESIGN_TIME_CONNECTION_STRING = 'Host=...;...'
    .\scripts\migrate.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent

$DataProject   = "$RepoRoot/src/KrakenDeploy.Server.Data"

Write-Host "Applying EF Core migrations..." -ForegroundColor Cyan
dotnet ef database update --project $DataProject --startup-project $DataProject
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Migrations applied." -ForegroundColor Green
