param(
    [string]$RuntimeSource = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path 'build-upscaling\Release')
)

$ErrorActionPreference = 'Stop'
$plugin = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = (Resolve-Path (Join-Path $plugin 'dist')).Path
$repo = (Resolve-Path (Join-Path $plugin '..\..')).Path
$payload = Join-Path $dist 'setup\payload'
$testRoot = Join-Path $dist ('isolated-runner-' + [Guid]::NewGuid().ToString('N'))
$runtime = Join-Path $testRoot 'neural-runtime'
$fullTestRoot = [IO.Path]::GetFullPath($testRoot)
if (-not $fullTestRoot.StartsWith($dist + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing a test folder outside dist: $fullTestRoot"
}

try {
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    foreach ($name in @('DLSSPhotoshopNeural.exe', 'ffmpeg.exe', 'ffprobe.exe')) {
        $source = if ($name -eq 'DLSSPhotoshopNeural.exe') {
            Join-Path $payload $name
        } else {
            Join-Path $RuntimeSource $name
        }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing test source: $source" }
        if ($name -eq 'DLSSPhotoshopNeural.exe') {
            Copy-Item -LiteralPath $source -Destination (Join-Path $testRoot $name)
        } else {
            New-Item -ItemType HardLink -Path (Join-Path $testRoot $name) -Target $source | Out-Null
        }
    }
    foreach ($name in @('NeuralWorker.exe', 'ReShade.ini', 'ReShadePreset.ini')) {
        Copy-Item -LiteralPath (Join-Path $payload $name) -Destination (Join-Path $runtime $name)
    }
    $lock = Get-Content -Raw -LiteralPath (Join-Path $repo 'packaging\runtime-lock.json') | ConvertFrom-Json
    foreach ($entry in $lock.entries) {
        $name = [string]$entry.destination
        if ([IO.Path]::GetFileName($name) -ne $name) { throw "Invalid runtime name: $name" }
        $source = Join-Path (Join-Path $RuntimeSource 'neural-runtime') $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing runtime file: $source" }
        New-Item -ItemType HardLink -Path (Join-Path $runtime $name) -Target $source | Out-Null
    }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'DLSSVideoPlayer.exe')) {
        throw 'The isolated runner folder contains the full player.'
    }

    Add-Type -AssemblyName System.Drawing
    $bmp = [System.Drawing.Bitmap]::new(512, 512)
    try {
        for ($y = 0; $y -lt 512; $y++) {
            for ($x = 0; $x -lt 512; $x++) {
                $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(
                    ($x * 3) % 256, ($y * 3) % 256, (($x + $y) * 2) % 256))
            }
        }
        $bmp.Save((Join-Path $testRoot 'input.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
    } finally { $bmp.Dispose() }

    & (Join-Path $testRoot 'DLSSPhotoshopNeural.exe') --input (Join-Path $testRoot 'input.bmp') `
        --output (Join-Path $testRoot 'output.mkv') --width 512 --height 512
    if ($LASTEXITCODE -ne 0) { throw "Neural runner failed: $LASTEXITCODE" }
    & (Join-Path $testRoot 'ffmpeg.exe') -hide_banner -nostdin -loglevel error -y `
        -i (Join-Path $testRoot 'output.mkv') -frames:v 1 -f rawvideo -pix_fmt rgb24 `
        (Join-Path $testRoot 'output.rgb')
    if ($LASTEXITCODE -ne 0) { throw "Frame decode failed: $LASTEXITCODE" }
    $length = (Get-Item -LiteralPath (Join-Path $testRoot 'output.rgb')).Length
    if ($length -ne 512 * 512 * 3) { throw "Wrong RGB length: $length" }
    Write-Output 'Isolated neural-only render passed: one verified 512 x 512 frame.'
} finally {
    if (Test-Path -LiteralPath $fullTestRoot) {
        Remove-Item -LiteralPath $fullTestRoot -Recurse -Force
    }
}
