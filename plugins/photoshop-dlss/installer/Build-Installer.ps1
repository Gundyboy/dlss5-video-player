param(
    [string]$PlayerPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path 'build-upscaling\Release\DLSSVideoPlayer.exe'),
    [string]$CcxFile,
    [string]$NsisCompiler
)

$ErrorActionPreference = 'Stop'
$plugin = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $plugin '..\..')).Path
$dist = Join-Path $plugin 'dist'
$uxp = Join-Path $dist 'uxp'
$setup = Join-Path $dist 'setup'

# Recreate generated staging folders so repeated builds cannot package stale files.
$distPath = [IO.Path]::GetFullPath($dist)
foreach ($stagePath in @($uxp, $setup)) {
    $fullPath = [IO.Path]::GetFullPath($stagePath)
    if (-not $fullPath.StartsWith($distPath + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a staging folder outside dist: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

if (-not (Test-Path -LiteralPath $PlayerPath -PathType Leaf)) {
    throw "Build the patched player first: $PlayerPath"
}
$bridgeBuild = Join-Path $dist 'bridge-build'
$env:DOTNET_CLI_HOME = Join-Path $plugin '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
dotnet publish (Join-Path $plugin 'bridge\DLSSPhotoshopBridge.csproj') -c Release -o $bridgeBuild
if ($LASTEXITCODE -ne 0) { throw 'The bridge build failed.' }

# Only the open-source panel, bridge, and patched player are packaged here.
# The experimental neural runtime and FFmpeg are taken from the user's existing
# local player folder by Install.ps1; their redistribution terms differ.
New-Item -ItemType Directory -Path (Join-Path $uxp 'native'), (Join-Path $setup 'payload') -Force | Out-Null
foreach ($name in @('manifest.json', 'index.html', 'panel.js')) {
    Copy-Item -LiteralPath (Join-Path $plugin $name) -Destination (Join-Path $uxp $name) -Force
}
Copy-Item -LiteralPath (Join-Path $plugin 'icons') -Destination (Join-Path $uxp 'icons') -Recurse -Force
foreach ($name in @('DLSSPhotoshopBridge.exe', 'DLSSPhotoshopBridge.dll',
        'DLSSPhotoshopBridge.deps.json', 'DLSSPhotoshopBridge.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $bridgeBuild $name) -Destination (Join-Path $uxp "native\$name") -Force
}
foreach ($name in @('Install.ps1', 'Install.cmd', 'Uninstall.ps1', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $setup $name) -Force
}
Copy-Item -LiteralPath $PlayerPath -Destination (Join-Path $setup 'payload\DLSSVideoPlayer.exe') -Force
Copy-Item -LiteralPath (Join-Path $repo 'packaging\runtime-lock.json') -Destination (Join-Path $setup 'payload\runtime-lock.json') -Force
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $setup 'payload\LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY.md') -Destination (Join-Path $setup 'payload\THIRD_PARTY.md') -Force

if ($CcxFile) {
    if (-not (Test-Path -LiteralPath $CcxFile -PathType Leaf)) { throw "Missing .ccx: $CcxFile" }
    Copy-Item -LiteralPath $CcxFile -Destination (Join-Path $setup 'DLSS-Neural-Mix.ccx') -Force
    $zip = Join-Path $dist 'DLSS-Neural-Mix-Setup-win64.zip'
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $setup '*') -DestinationPath $zip -Force
    Write-Output "Installer bundle: $zip"

    if (-not $NsisCompiler) {
        $portable = Join-Path $dist 'nsis-toolchain\tools\Bin\makensis.exe'
        if (Test-Path -LiteralPath $portable -PathType Leaf) {
            $NsisCompiler = $portable
        } else {
            $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
            if ($command) { $NsisCompiler = $command.Source }
        }
    }
    if (-not $NsisCompiler -or -not (Test-Path -LiteralPath $NsisCompiler -PathType Leaf)) {
        throw 'NSIS makensis.exe is required to build the setup EXE. Pass -NsisCompiler <path>.'
    }
    $compiler = (Resolve-Path -LiteralPath $NsisCompiler).Path
    Push-Location $PSScriptRoot
    try {
        & $compiler 'Setup.nsi'
        if ($LASTEXITCODE -ne 0) { throw "NSIS compilation failed with exit code $LASTEXITCODE." }
    } finally { Pop-Location }
    Write-Output "Setup executable: $(Join-Path $dist 'DLSS-Neural-Mix-Setup-win64.exe')"
} else {
    Write-Output "UXP package source: $uxp"
    Write-Output 'In UXP Developer Tool, Add Plugin using uxp\manifest.json, then Package to a .ccx file.'
    Write-Output 'Run this script again with -CcxFile <path to generated .ccx> to finish the installer ZIP.'
}
