param(
    [string]$RunnerPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'build-upscaling\Release\DLSSPhotoshopNeural.exe')
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
if (-not (Test-Path -LiteralPath $RunnerPath -PathType Leaf)) {
    throw "Build the DLSSPhotoshopNeural target first, or pass -RunnerPath. Missing: $RunnerPath"
}
$native = Join-Path $PSScriptRoot 'native'
dotnet publish (Join-Path $PSScriptRoot 'bridge\DLSSPhotoshopBridge.csproj') -c Release -o $native
if ($LASTEXITCODE -ne 0) { throw 'Bridge build failed.' }
@{ runnerPath = (Resolve-Path -LiteralPath $RunnerPath).Path } |
    ConvertTo-Json -Compress |
    Set-Content -LiteralPath (Join-Path $native 'bridge-config.json') -Encoding UTF8
Write-Output "UXP plugin ready: $PSScriptRoot"
Write-Output "Bridge configured for: $RunnerPath"
