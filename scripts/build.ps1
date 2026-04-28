#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the entire KrakenDeploy solution.

.PARAMETER Configuration
    MSBuild configuration. Default: Debug.

.EXAMPLE
    .\scripts\build.ps1
    .\scripts\build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent

Write-Host "Building KrakenDeploy ($Configuration)..." -ForegroundColor Cyan
dotnet build "$RepoRoot/KrakenDeploy.sln" --configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Build succeeded." -ForegroundColor Green
