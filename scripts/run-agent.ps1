#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the KrakenDeploy agent in development mode.

.DESCRIPTION
    Starts the agent worker service and connects it to a KrakenDeploy server.

    First run (no identity file on disk):
        Pass -RegistrationToken with the one-time token generated in the server UI
        (Infrastructure → Targets → Add Target → copy the token from Step 3).
        After successful registration the token is consumed and an identity file
        is written to the agent data directory. Subsequent runs need no token.

    The agent data directory defaults to:
        Windows:  %ProgramData%\KrakenDeploy\Agent
        Linux/macOS: /var/lib/krakendeploy-agent
    Override with -DataPath or the Agent:DataPath config key.

.PARAMETER ServerUrl
    URL of the KrakenDeploy server.
    Default: https://localhost:5443 (set via launchSettings.json env var).

.PARAMETER RegistrationToken
    One-time registration token. Required only on the first run on a machine.

.PARAMETER DataPath
    Override the agent data directory (stores identity file, work temp dirs).

.EXAMPLE
    # First run — provide the registration token
    .\scripts\run-agent.ps1 -RegistrationToken eyJhb...

    # Subsequent runs — identity already on disk
    .\scripts\run-agent.ps1

    # Against a remote server
    .\scripts\run-agent.ps1 -ServerUrl https://deploy.example.com -RegistrationToken eyJhb...
#>
[CmdletBinding()]
param(
    [string] $ServerUrl = '',

    [string] $RegistrationToken = '',

    [string] $DataPath = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent

$extraArgs = [System.Collections.Generic.List[string]]::new()

if ($ServerUrl) {
    $extraArgs.Add("--Server:Url=$ServerUrl")
}
if ($RegistrationToken) {
    $extraArgs.Add("--Server:RegistrationToken=$RegistrationToken")
}
if ($DataPath) {
    $extraArgs.Add("--Agent:DataPath=$DataPath")
}

if ($extraArgs.Count -gt 0) {
    dotnet run --project "$RepoRoot/src/KrakenDeploy.Agent" -- @extraArgs
}
else {
    dotnet run --project "$RepoRoot/src/KrakenDeploy.Agent"
}
exit $LASTEXITCODE
