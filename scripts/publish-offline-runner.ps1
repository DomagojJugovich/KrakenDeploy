#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes the self-contained KrakenDeploy offline-drop runner, one folder per
    target RID, ready to ship with the server.

.DESCRIPTION
    The offline runner IS the agent (KrakenDeploy.Agent) invoked with
    `--run-offline-drop`. Published self-contained so an offline target needs no
    .NET runtime installed. The server's DropBundleService embeds the matching
    RID folder into each offline drop bundle (best-effort: if absent, the bundle
    bootstrap falls back to a KrakenDeploy.Agent on PATH).

    Folder publish only — NOT single-file, NOT trimmed, NOT NativeAOT: the runner
    loads step-package handler assemblies at runtime via a collectible
    AssemblyLoadContext, which trimming strips and AOT cannot JIT.

    Output: <OutputRoot>/<rid>/  (KrakenDeploy.Agent[.exe] + deps + runtime).
    Ship each folder to the server's <DataPath>/offline-runner/<rid>/ — that is
    where DropBundleService looks for it (RID derived from the target's OS).

.PARAMETER Rids
    Target runtime identifiers. Default: win-x64, linux-x64.

.PARAMETER Configuration
    MSBuild configuration. Default: Release.

.PARAMETER OutputRoot
    Output root directory. Default: <RepoRoot>/artifacts/offline-runner.

.EXAMPLE
    ./scripts/publish-offline-runner.ps1
    ./scripts/publish-offline-runner.ps1 -Rids win-x64 -Configuration Release
#>
[CmdletBinding()]
param(
    [string[]] $Rids = @('win-x64', 'linux-x64'),
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot 'artifacts/offline-runner'
}
$Project = Join-Path $RepoRoot 'src/KrakenDeploy.Agent/KrakenDeploy.Agent.csproj'

foreach ($rid in $Rids) {
    $outDir = Join-Path $OutputRoot $rid
    Write-Host "Publishing offline runner: $rid -> $outDir" -ForegroundColor Cyan

    dotnet publish $Project `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        --output $outDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Ship each <rid> folder to the server's <DataPath>/offline-runner/<rid>/;" -ForegroundColor Green
Write-Host "DropBundleService embeds it into offline drop bundles automatically." -ForegroundColor Green
