$ErrorActionPreference = 'Stop'
try {
    $programFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $upia = Join-Path $programFiles64 'Common Files\Adobe\Adobe Desktop Common\RemoteComponents\UPI\UnifiedPluginInstallerAgent\UnifiedPluginInstallerAgent.exe'
    if (Test-Path -LiteralPath $upia -PathType Leaf) {
        & $upia /remove 'DLSS Neural Mix'
        if ($LASTEXITCODE -ne 0) { throw "Adobe could not remove DLSS Neural Mix (exit code $LASTEXITCODE)." }
    }

    $renderer = Join-Path $env:LOCALAPPDATA 'DLSSNeuralMix\Renderer'
    $runner = Join-Path $renderer 'DLSSPhotoshopNeural.exe'
    $legacyPlayer = Join-Path $renderer 'DLSSVideoPlayer.exe'
    $config = Join-Path $env:LOCALAPPDATA 'DLSSPhotoshopBridge\bridge-config.json'
    if (Test-Path -LiteralPath $config -PathType Leaf) {
        $configured = Get-Content -Raw -LiteralPath $config | ConvertFrom-Json
        if ($configured.runnerPath -ieq $runner -or $configured.playerPath -ieq $legacyPlayer) {
            Remove-Item -LiteralPath $config -Force
        }
    }
    $lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'runtime-lock.json') | ConvertFrom-Json
    foreach ($entry in $lock.entries) {
        $name = [IO.Path]::GetFileName([string]$entry.destination)
        if ($name -ne [string]$entry.destination) { throw "Invalid runtime filename: $($entry.destination)" }
        $file = Join-Path (Join-Path $renderer 'neural-runtime') $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force }
    }
    foreach ($name in @('DLSSPhotoshopNeural.exe', 'DLSSVideoPlayer.exe', 'ffmpeg.exe', 'ffprobe.exe')) {
        $file = Join-Path $renderer $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force }
    }
    $worker = Join-Path $renderer 'neural-runtime\NeuralWorker.exe'
    if (Test-Path -LiteralPath $worker -PathType Leaf) { Remove-Item -LiteralPath $worker -Force }
    Write-Output 'DLSS Neural Mix removed. Saved settings and cache were retained.'
} catch {
    Write-Output ('ERROR: ' + $_.Exception.Message)
    exit 1
}
