#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates the first (or an additional) admin user for the KrakenDeploy server.

.DESCRIPTION
    Delegates to the built-in CLI sub-command:
        KrakenDeploy.Server users create-admin --email <e> --password <p>

    The command is idempotent: if the user already exists it prints a notice and
    exits 0 without making any changes.

    The server does NOT need to be running. The command builds a minimal host,
    connects to the database, performs the operation, and exits.

.PARAMETER Email
    Admin account email address.

.PARAMETER Password
    Admin password. Must satisfy the password policy:
    minimum 8 characters, upper + lower case, digit, and one special character.

.EXAMPLE
    .\scripts\create-admin.ps1 -Email admin@example.com -Password 'ChangeMe123!'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Email,

    [Parameter(Mandatory)]
    [string] $Password
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent

dotnet run --project "$RepoRoot/src/KrakenDeploy.Server" -- `
    users create-admin --email $Email --password $Password
exit $LASTEXITCODE
