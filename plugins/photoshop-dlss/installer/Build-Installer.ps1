param(
    [string]$RunnerPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path 'build-upscaling\Release\DLSSPhotoshopNeural.exe'),
    [string]$WorkerPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path 'build-upscaling\Release\neural-runtime\NeuralWorker.exe'),
    [string]$CcxFile,
    [string]$NsisCompiler,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$plugin = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $plugin '..\..')).Path
$dist = Join-Path $plugin 'dist'
$uxp = Join-Path $dist 'uxp'
$setup = Join-Path $dist 'setup'
$bridgeBuild = Join-Path $dist 'bridge-build'

# Recreate generated staging folders so repeated builds cannot package stale files.
$distPath = [IO.Path]::GetFullPath($dist)
foreach ($stagePath in @($uxp, $setup, $bridgeBuild)) {
    $fullPath = [IO.Path]::GetFullPath($stagePath)
    if (-not $fullPath.StartsWith($distPath + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a staging folder outside dist: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

if (-not (Test-Path -LiteralPath $RunnerPath -PathType Leaf)) {
    throw "Build the DLSSPhotoshopNeural target first: $RunnerPath"
}
if (-not (Test-Path -LiteralPath $WorkerPath -PathType Leaf)) {
    throw "Build the NeuralWorker target first: $WorkerPath"
}
$bundledDotnet = Join-Path $dist 'toolchain\dotnet\dotnet.exe'
if (-not $DotnetPath) {
    if (Test-Path -LiteralPath $bundledDotnet -PathType Leaf) { $DotnetPath = $bundledDotnet }
    else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$sdkVersion = & $DotnetPath --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 10) {
    throw 'The bridge build requires the .NET 10 SDK or newer. Pass -DotnetPath <path-to-dotnet.exe>.'
}
$env:DOTNET_CLI_HOME = Join-Path $plugin '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
& $DotnetPath publish (Join-Path $plugin 'bridge\DLSSPhotoshopBridge.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $bridgeBuild
if ($LASTEXITCODE -ne 0) { throw 'The bridge build failed.' }

# Only the open-source panel, bridge, neural runner and worker are packaged here.
# The experimental neural runtime and FFmpeg are taken from the user's existing
# local player folder by Install.ps1; their redistribution terms differ.
New-Item -ItemType Directory -Path (Join-Path $uxp 'native'), (Join-Path $setup 'payload') -Force | Out-Null
foreach ($name in @('manifest.json', 'index.html', 'panel.js')) {
    Copy-Item -LiteralPath (Join-Path $plugin $name) -Destination (Join-Path $uxp $name) -Force
}
Copy-Item -LiteralPath (Join-Path $plugin 'icons') -Destination (Join-Path $uxp 'icons') -Recurse -Force
if (-not (Test-Path -LiteralPath (Join-Path $bridgeBuild 'DLSSPhotoshopBridge.exe') -PathType Leaf)) {
    throw 'The self-contained bridge executable is missing from the publish output.'
}
foreach ($file in Get-ChildItem -LiteralPath $bridgeBuild -File) {
    if ($file.Extension -ne '.pdb') {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $uxp 'native') -Force
    }
}
$dotnetRoot = Split-Path -Parent (Resolve-Path -LiteralPath $DotnetPath).Path
foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $noticePath = Join-Path $dotnetRoot $notice
    if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
        throw "The .NET redistribution notice is missing: $noticePath"
    }
    Copy-Item -LiteralPath $noticePath -Destination (Join-Path $uxp "native\DOTNET-$notice") -Force
}
foreach ($name in @('Install.ps1', 'Upgrade.ps1', 'Install.cmd', 'Uninstall.ps1', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $setup $name) -Force
}
Copy-Item -LiteralPath $RunnerPath -Destination (Join-Path $setup 'payload\DLSSPhotoshopNeural.exe') -Force
Copy-Item -LiteralPath $WorkerPath -Destination (Join-Path $setup 'payload\NeuralWorker.exe') -Force
Copy-Item -LiteralPath (Join-Path $repo 'packaging\ReShade.ini') -Destination (Join-Path $setup 'payload\ReShade.ini') -Force
Copy-Item -LiteralPath (Join-Path $repo 'packaging\ReShadePreset.ini') -Destination (Join-Path $setup 'payload\ReShadePreset.ini') -Force
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
