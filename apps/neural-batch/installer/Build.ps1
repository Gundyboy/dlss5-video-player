param([string]$DotnetPath, [string]$NsisCompiler,
    [string]$CrtPath = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Redist\MSVC\14.42.34433\x64\Microsoft.VC143.CRT')
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $app '..\..')).Path
$pluginDist = Join-Path $repo 'plugins\photoshop-dlss\dist'
$dist = Join-Path $app 'dist'
$publish = Join-Path $dist 'publish'
$portable = Join-Path $dist 'portable'
if (-not $DotnetPath) { $DotnetPath = Join-Path $pluginDist 'toolchain\dotnet\dotnet.exe' }
if (-not $NsisCompiler) { $NsisCompiler = Join-Path $pluginDist 'nsis-toolchain\tools\Bin\makensis.exe' }
$runner = Join-Path $repo 'build-upscaling\Release\DLSSPhotoshopNeural.exe'
$worker = Join-Path $repo 'build-upscaling\Release\neural-runtime\NeuralWorker.exe'
foreach ($path in @($DotnetPath, $NsisCompiler, $runner, $worker)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Build dependency missing: $path" } }
foreach ($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')) { if (-not (Test-Path -LiteralPath (Join-Path $CrtPath $name))) { throw "C++ redistributable missing: $name. Pass -CrtPath <official x64 CRT directory>." } }
foreach ($path in @($publish, $portable)) {
    $absolute = [IO.Path]::GetFullPath($path)
    if (-not $absolute.StartsWith([IO.Path]::GetFullPath($dist) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging folder' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
$env:DOTNET_CLI_HOME = Join-Path $app '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
& $DotnetPath publish (Join-Path $app 'DLSSNeuralBatch.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
$payload = Join-Path $portable 'RuntimeFiles'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payload 'CRT') -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $CrtPath -Filter '*.dll' -File) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') { throw "C++ runtime signature could not be verified: $($file.Name)" }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $payload 'CRT')
}
Copy-Item -LiteralPath (Join-Path $publish 'DLSSNeuralBatch.exe') -Destination $portable
Copy-Item -LiteralPath $runner, $worker -Destination $payload
foreach ($name in @('ReShade.ini', 'ReShadePreset.ini', 'runtime-lock.json')) { Copy-Item -LiteralPath (Join-Path $repo "packaging\$name") -Destination $payload }
foreach ($name in @('LICENSE', 'THIRD_PARTY.md')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $portable }
Copy-Item -LiteralPath (Join-Path $app 'README.md') -Destination $portable
Copy-Item -LiteralPath (Join-Path $app 'BUNDLED-RUNTIMES.md') -Destination $portable
$sdkRoot = Split-Path -Parent (Resolve-Path -LiteralPath $DotnetPath).Path
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) { Copy-Item -LiteralPath (Join-Path $sdkRoot $name) -Destination (Join-Path $portable "DOTNET-$name") }
$zip = Join-Path $dist 'DLSS-Neural-Batch-win64.zip'
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -Force
Push-Location $PSScriptRoot
try { & $NsisCompiler 'Setup.nsi'; if ($LASTEXITCODE -ne 0) { throw 'NSIS build failed.' } }
finally { Pop-Location }
Write-Output "Portable app: $(Join-Path $portable 'DLSSNeuralBatch.exe')"
Write-Output "Portable ZIP: $zip"
Write-Output "Installer: $(Join-Path $dist 'DLSS-Neural-Batch-Setup-win64.exe')"
