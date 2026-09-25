param(
    [string]$RuntimePath = (Join-Path $env:USERPROFILE 'Desktop\DLSSVideoPlayer-v0.26.0-win64'),
    [string]$PlayerExePath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
try {
. (Join-Path $PSScriptRoot 'Upgrade.ps1')
if (Get-Process -Name Photoshop -ErrorAction SilentlyContinue) {
    throw 'Photoshop is still running. Save your work and close Photoshop before continuing; an open panel keeps the previous plugin code loaded.'
}
if ($PlayerExePath) {
    if (-not (Test-Path -LiteralPath $PlayerExePath -PathType Leaf)) {
        throw "Selected video player executable not found: $PlayerExePath"
    }
    if ([IO.Path]::GetFileName($PlayerExePath) -ine 'DLSSVideoPlayer.exe') {
        throw 'Select DLSSVideoPlayer.exe from the unpacked video player folder.'
    }
    $RuntimePath = Split-Path -Parent (Resolve-Path -LiteralPath $PlayerExePath).Path
}
$package = Join-Path $PSScriptRoot 'DLSS-Neural-Mix.ccx'
$payload = Join-Path $PSScriptRoot 'payload'
$runner = Join-Path $payload 'DLSSPhotoshopNeural.exe'
$worker = Join-Path $payload 'NeuralWorker.exe'
$reshadeSettings = Join-Path $payload 'ReShade.ini'
$reshadePreset = Join-Path $payload 'ReShadePreset.ini'
$lockPath = Join-Path $payload 'runtime-lock.json'
$programFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$upia = Join-Path $programFiles64 'Common Files\Adobe\Adobe Desktop Common\RemoteComponents\UPI\UnifiedPluginInstallerAgent\UnifiedPluginInstallerAgent.exe'

foreach ($item in @($package, $runner, $worker, $reshadeSettings, $reshadePreset, $lockPath, $upia)) {
    if (-not (Test-Path -LiteralPath $item -PathType Leaf)) { throw "Installer file missing: $item" }
}
$identity = Get-DlssPackageIdentity -Package $package
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
$ffprobe = Join-Path $source 'ffprobe.exe'
if (-not (Test-Path -LiteralPath $ffprobe -PathType Leaf)) { throw "Missing ffprobe.exe in $source" }

$lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
foreach ($entry in $lock.entries) {
    $file = Join-Path $runtime $entry.destination
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing runtime file: $file" }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($actual -ne $entry.sha256) { throw "Runtime version mismatch: $file" }
}

$dotnet = Join-Path $programFiles64 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf) -or
    -not ((& $dotnet --list-runtimes) -match '^Microsoft\.NETCore\.App 3\.1\.')) {
    throw 'The .NET Core 3.1 x64 runtime is required for the local bridge.'
}

Write-Output "Verified neural runtime: $source"
if ($ValidateOnly) { Write-Output 'Validation passed; no files were installed.'; exit 0 }
Stop-DlssBridgeForUpgrade

$installRoot = Join-Path $env:LOCALAPPDATA 'DLSSNeuralMix'
$renderer = Join-Path $installRoot 'Renderer'
$rendererRuntime = Join-Path $renderer 'neural-runtime'
New-Item -ItemType Directory -Path $rendererRuntime -Force | Out-Null
Copy-Item -LiteralPath $runner -Destination (Join-Path $renderer 'DLSSPhotoshopNeural.exe') -Force
Copy-Item -LiteralPath $worker -Destination (Join-Path $rendererRuntime 'NeuralWorker.exe') -Force
Copy-Item -LiteralPath $ffmpeg -Destination (Join-Path $renderer 'ffmpeg.exe') -Force
Copy-Item -LiteralPath $ffprobe -Destination (Join-Path $renderer 'ffprobe.exe') -Force
foreach ($entry in $lock.entries) {
    Copy-Item -LiteralPath (Join-Path $runtime $entry.destination) -Destination (Join-Path $rendererRuntime $entry.destination) -Force
}
Copy-Item -LiteralPath $reshadeSettings -Destination (Join-Path $rendererRuntime 'ReShade.ini') -Force
Copy-Item -LiteralPath $reshadePreset -Destination (Join-Path $rendererRuntime 'ReShadePreset.ini') -Force
foreach ($name in @('LICENSE', 'THIRD_PARTY.md', 'EXPERIMENTAL_RUNTIME_NOTICE.txt')) {
    $file = Join-Path $source $name
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        Copy-Item -LiteralPath $file -Destination (Join-Path $renderer $name) -Force
    }
}

$bridgeSettings = Join-Path $env:LOCALAPPDATA 'DLSSPhotoshopBridge'
New-Item -ItemType Directory -Path $bridgeSettings -Force | Out-Null
@{ runnerPath = (Join-Path $renderer 'DLSSPhotoshopNeural.exe') } |
    ConvertTo-Json -Compress |
    Set-Content -LiteralPath (Join-Path $bridgeSettings 'bridge-config.json') -Encoding UTF8

& $upia /install $package
if ($LASTEXITCODE -ne 0) { throw "Adobe UPIA installation failed with exit code $LASTEXITCODE." }
# UPIA and Adobe background components may briefly touch the old package after
# the initial preflight. Recheck immediately before moving obsolete versions.
Stop-DlssBridgeForUpgrade
Move-ObsoleteDlssPlugins -ExternalRoot (Join-Path $env:APPDATA 'Adobe\UXP\Plugins\External') `
    -BackupRoot (Join-Path $installRoot 'PluginBackups') -Identity $identity
Write-Output "DLSS Neural Mix v$($identity.Version) installed. Start Photoshop, then open Plugins > DLSS Neural Mix."
} catch {
    Write-Output ('ERROR: ' + $_.Exception.Message)
    exit 1
}
