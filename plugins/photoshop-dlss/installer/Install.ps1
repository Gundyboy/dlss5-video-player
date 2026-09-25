param(
    [string]$RuntimePath = (Join-Path $env:USERPROFILE 'Desktop\DLSSVideoPlayer-v0.26.0-win64'),
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'DLSS-Neural-Mix.ccx'
$payload = Join-Path $PSScriptRoot 'payload'
$player = Join-Path $payload 'DLSSVideoPlayer.exe'
$lockPath = Join-Path $payload 'runtime-lock.json'
$upia = Join-Path ${env:ProgramFiles} 'Common Files\Adobe\Adobe Desktop Common\RemoteComponents\UPI\UnifiedPluginInstallerAgent\UnifiedPluginInstallerAgent.exe'

foreach ($item in @($package, $player, $lockPath, $upia)) {
    if (-not (Test-Path -LiteralPath $item -PathType Leaf)) { throw "Installer file missing: $item" }
}
if (-not (Test-Path -LiteralPath $RuntimePath -PathType Container)) {
    throw "DLSS Video Player folder not found: $RuntimePath. Pass -RuntimePath <folder>."
}
$source = (Resolve-Path -LiteralPath $RuntimePath).Path
$runtime = Join-Path $source 'neural-runtime'
if (-not (Test-Path -LiteralPath $runtime -PathType Container)) {
    throw "The selected folder has no neural-runtime subfolder: $source"
}
$ffmpeg = Join-Path $source 'ffmpeg.exe'
if (-not (Test-Path -LiteralPath $ffmpeg -PathType Leaf)) { throw "Missing ffmpeg.exe in $source" }

$lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
foreach ($entry in $lock.entries) {
    $file = Join-Path $runtime $entry.destination
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing runtime file: $file" }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($actual -ne $entry.sha256) { throw "Runtime version mismatch: $file" }
}

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnet -or -not ((& $dotnet.Source --list-runtimes) -match '^Microsoft\.NETCore\.App 3\.1\.')) {
    throw 'The .NET Core 3.1 x64 runtime is required for the local bridge.'
}

Write-Output "Verified neural runtime: $source"
if ($ValidateOnly) { Write-Output 'Validation passed; no files were installed.'; exit 0 }

$installRoot = Join-Path $env:LOCALAPPDATA 'DLSSNeuralMix'
$renderer = Join-Path $installRoot 'Renderer'
$rendererRuntime = Join-Path $renderer 'neural-runtime'
New-Item -ItemType Directory -Path $rendererRuntime -Force | Out-Null
Copy-Item -LiteralPath $player -Destination (Join-Path $renderer 'DLSSVideoPlayer.exe') -Force
Copy-Item -LiteralPath $ffmpeg -Destination (Join-Path $renderer 'ffmpeg.exe') -Force
foreach ($entry in $lock.entries) {
    Copy-Item -LiteralPath (Join-Path $runtime $entry.destination) -Destination (Join-Path $rendererRuntime $entry.destination) -Force
}
foreach ($name in @('ReShade.ini', 'ReShadePreset.ini')) {
    $file = Join-Path $runtime $name
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        Copy-Item -LiteralPath $file -Destination (Join-Path $rendererRuntime $name) -Force
    }
}
$settings = Join-Path $source 'DLSSVideoPlayer.ini'
if (Test-Path -LiteralPath $settings -PathType Leaf) {
    Copy-Item -LiteralPath $settings -Destination (Join-Path $renderer 'DLSSVideoPlayer.ini') -Force
}
foreach ($name in @('LICENSE', 'THIRD_PARTY.md', 'EXPERIMENTAL_RUNTIME_NOTICE.txt')) {
    $file = Join-Path $source $name
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        Copy-Item -LiteralPath $file -Destination (Join-Path $renderer $name) -Force
    }
}

$bridgeSettings = Join-Path $env:LOCALAPPDATA 'DLSSPhotoshopBridge'
New-Item -ItemType Directory -Path $bridgeSettings -Force | Out-Null
@{ playerPath = (Join-Path $renderer 'DLSSVideoPlayer.exe') } |
    ConvertTo-Json -Compress |
    Set-Content -LiteralPath (Join-Path $bridgeSettings 'bridge-config.json') -Encoding UTF8

& $upia /install $package
if ($LASTEXITCODE -ne 0) { throw "Adobe UPIA installation failed with exit code $LASTEXITCODE." }
Write-Output 'DLSS Neural Mix installed. Restart Photoshop, then open Plugins > DLSS Neural Mix.'
