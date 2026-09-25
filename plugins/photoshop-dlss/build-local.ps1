param(
    [string]$PlayerPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'build-upscaling\Release\DLSSVideoPlayer.exe')
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
if (-not (Test-Path -LiteralPath $PlayerPath -PathType Leaf)) {
    throw "Build the patched DLSSVideoPlayer target first, or pass -PlayerPath. Missing: $PlayerPath"
}
$native = Join-Path $PSScriptRoot 'native'
dotnet publish (Join-Path $PSScriptRoot 'bridge\DLSSPhotoshopBridge.csproj') -c Release -o $native
if ($LASTEXITCODE -ne 0) { throw 'Bridge build failed.' }
@{ playerPath = (Resolve-Path -LiteralPath $PlayerPath).Path } |
    ConvertTo-Json -Compress |
    Set-Content -LiteralPath (Join-Path $native 'bridge-config.json') -Encoding UTF8
Write-Output "UXP plugin ready: $PSScriptRoot"
Write-Output "Bridge configured for: $PlayerPath"
