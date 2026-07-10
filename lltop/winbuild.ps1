[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet build (Join-Path $scriptDir 'lltop.csproj') --configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
