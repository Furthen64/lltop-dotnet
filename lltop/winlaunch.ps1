[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $ApplicationArguments
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $scriptDir 'lltop.csproj'

dotnet run --project $project --configuration $Configuration --no-build -- @ApplicationArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
