param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$solution = Join-Path $projectRoot 'ProjectFileHub.slnx'
$nugetConfig = Join-Path $projectRoot 'NuGet.Config'

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget-packages'

& $dotnet restore $solution --configfile $nugetConfig --disable-parallel --disable-build-servers -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build $solution --configuration $Configuration --no-restore -p:UseSharedCompilation=false --disable-build-servers -m:1
exit $LASTEXITCODE
