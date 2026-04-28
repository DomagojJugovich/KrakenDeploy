#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the KrakenDeploy server in development mode.

.DESCRIPTION
    Starts the Blazor Server host using the specified launch profile.
    The server listens on:
        https://localhost:5443  (default, "https" profile)
        http://localhost:5080

    Secrets (connection string, JWT signing key) are loaded from user-secrets:
        dotnet user-secrets set "ConnectionStrings:KrakenDb" "..."  --project src/KrakenDeploy.Server
        dotnet user-secrets set "Agent:JwtSigningKey"       "..."  --project src/KrakenDeploy.Server

    If secrets are not configured the development appsettings fallbacks are used
    (localhost Postgres / hardcoded dev JWT key — fine for local dev, never for prod).

.PARAMETER Profile
    Launch profile from launchSettings.json. Defaults to "https".

.EXAMPLE
    .\scripts\run-server.ps1
    .\scripts\run-server.ps1 -Profile http
#>
[CmdletBinding()]
param(
    [ValidateSet('http', 'https')]
    [string] $Profile = 'https'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent

dotnet run --project "$RepoRoot/src/KrakenDeploy.Server" --launch-profile $Profile
exit $LASTEXITCODE
