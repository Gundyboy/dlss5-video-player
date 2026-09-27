param(
    [string]$RunnerPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'build-upscaling\Release\DLSSPhotoshopNeural.exe'),
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$bundledDotnet = Join-Path $PSScriptRoot 'dist\toolchain\dotnet\dotnet.exe'
if (-not $DotnetPath) {
    if (Test-Path -LiteralPath $bundledDotnet -PathType Leaf) { $DotnetPath = $bundledDotnet }
    else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$sdkVersion = & $DotnetPath --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 10) {
    throw 'The bridge build requires the .NET 10 SDK or newer. Pass -DotnetPath <path-to-dotnet.exe>.'
}
if (-not (Test-Path -LiteralPath $RunnerPath -PathType Leaf)) {
    throw "Build the DLSSPhotoshopNeural target first, or pass -RunnerPath. Missing: $RunnerPath"
}
$native = Join-Path $PSScriptRoot 'native'
New-Item -ItemType Directory -Path $native -Force | Out-Null
Get-ChildItem -LiteralPath $native -File |
    Where-Object Name -ne 'bridge-config.json' |
    Remove-Item -Force
& $DotnetPath publish (Join-Path $PSScriptRoot 'bridge\DLSSPhotoshopBridge.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $native
if ($LASTEXITCODE -ne 0) { throw 'Bridge build failed.' }
@{ runnerPath = (Resolve-Path -LiteralPath $RunnerPath).Path } |
    ConvertTo-Json -Compress |
    Set-Content -LiteralPath (Join-Path $native 'bridge-config.json') -Encoding UTF8
Write-Output "UXP plugin ready: $PSScriptRoot"
Write-Output "Bridge configured for: $RunnerPath"
