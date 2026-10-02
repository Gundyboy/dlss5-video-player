param(
    [Parameter(Mandatory = $true)][string]$SourceBundle,
    [string]$BatchInstaller,
    [string]$OutputBundle
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$app = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $app 'dist'
if (-not $BatchInstaller) { $BatchInstaller = Join-Path $dist 'DLSS-Neural-Batch-Setup-win64.exe' }
if (-not $OutputBundle) { $OutputBundle = Join-Path $dist 'DLSS Video Player - PS Plugin.zip' }
$destination = [IO.Path]::GetFullPath($OutputBundle)
$sourcePath = [IO.Path]::GetFullPath($SourceBundle)
if ($destination -ne (Join-Path $dist 'DLSS Video Player - PS Plugin.zip')) { throw 'The generated bundle must remain inside this app''s dist folder.' }
if ($sourcePath -eq $destination) { throw 'Source and generated bundle must be different files.' }
foreach ($file in @($sourcePath, $BatchInstaller)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing bundle input: $file" } }
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$stage = Join-Path $dist ('combined-' + [Guid]::NewGuid().ToString('N') + '.zip')
$additions = @('DLSS-Neural-Batch-Setup-win64.exe', 'START HERE.txt', 'DLSS Neural Batch/README.md')
function Entry-Hash($entry) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = $entry.Open()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $hash.Dispose() }
}
try {
    # Update a local copy to retain the original compressed player and plugin payloads.
    Copy-Item -LiteralPath $sourcePath -Destination $stage
    $archive = [IO.Compression.ZipFile]::Open($stage, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($name in @('DLSS Video Player/DLSSVideoPlayer.exe', 'DLSS Video Player/ffmpeg.exe', 'DLSS Video Player/ffprobe.exe', 'DLSS-Neural-Mix-Setup-win64.exe')) {
            if (-not $archive.GetEntry($name)) { throw "The source bundle is missing $name" }
        }
        foreach ($name in $additions) { foreach ($entry in @($archive.Entries | Where-Object FullName -EQ $name)) { $entry.Delete() } }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $BatchInstaller, $additions[0], [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $app 'README.md'), $additions[2], [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $entry = $archive.CreateEntry($additions[1])
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try {
            $writer.Write(@'
DLSS Video Player + Photoshop Plugin + Neural Batch
==================================================

1. Right-click this ZIP and choose Extract All. Keep the extracted player
   folder and its files together.

2. For the standalone image queue, run DLSS-Neural-Batch-Setup-win64.exe.
   During setup, browse to DLSS Video Player\DLSSVideoPlayer.exe inside
   this extracted bundle. Add images or folders, select Mix (0-200%), choose
   an output folder, and start the queue. Select 2x Upscaler to double the
   image dimensions with DLSS Super Resolution. At 0% Mix it upscales only.
   Enable Pano for a 360 panorama.
   Batch processing saves new PNG files and preserves your originals.

3. For Photoshop, run DLSS-Neural-Mix-Setup-win64.exe and select the same
   extracted DLSSVideoPlayer.exe. Open the DLSS Neural Mix panel in
   Photoshop, select your image layer, choose Mix/Pano, and apply.

4. To use the video player, open DLSS Video Player\DLSSVideoPlayer.exe.

Included: Photoshop plugin 0.2.11 and DLSS Neural Batch 0.2.0.
The batch app runs independently of Photoshop. Its installer bundles .NET
and the C++ runtime used by its native helper. All processing stays local;
the processing helpers run without a console window.

A compatible NVIDIA RTX GPU and driver are required. Both installers copy
the verified renderer from the included player folder. Images for Batch
and outputs must be 64-8192 pixels per side and at most 64 megapixels.

Detailed batch instructions: DLSS Neural Batch\README.md
Player instructions: DLSS Video Player\README.md
Made by Will
'@)
        } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }

    $source = [IO.Compression.ZipFile]::OpenRead($sourcePath)
    $result = [IO.Compression.ZipFile]::OpenRead($stage)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $result.Entries) { if (-not $seen.Add($entry.FullName)) { throw "Duplicate bundle entry: $($entry.FullName)" } }
        $preserved = 0
        foreach ($entry in $source.Entries) {
            if ($entry.FullName -in $additions) { continue }
            $copy = $result.GetEntry($entry.FullName)
            if (-not $copy -or $copy.Length -ne $entry.Length -or (Entry-Hash $copy) -ne (Entry-Hash $entry)) { throw "Original bundle file was changed: $($entry.FullName)" }
            $preserved++
        }
        $installer = $result.GetEntry($additions[0])
        if ((Entry-Hash $installer) -ne (Get-FileHash -LiteralPath $BatchInstaller -Algorithm SHA256).Hash) { throw 'The bundled batch installer does not match the tested installer.' }
        $lockPath = [IO.Path]::GetFullPath((Join-Path $app '..\..\packaging\runtime-lock.json'))
        $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
        foreach ($pin in $lock.entries) {
            $entry = $result.GetEntry('DLSS Video Player/neural-runtime/' + $pin.destination)
            if (-not $entry -or (Entry-Hash $entry) -ne $pin.sha256) { throw "The bundled player renderer does not match the installers: $($pin.destination)" }
        }
        Write-Output "Verified $preserved preserved files and $($result.Entries.Count) total entries; batch installer matched SHA256."
    } finally { $source.Dispose(); $result.Dispose() }
    Move-Item -LiteralPath $stage -Destination $destination -Force
    Write-Output "Bundle: $destination"
    Write-Output "SHA256: $((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash)"
} finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Force } }
