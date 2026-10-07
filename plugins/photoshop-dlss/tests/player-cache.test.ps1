param([string]$Runner = (Join-Path $PSScriptRoot '..\..\..\build-upscaling\Release\DLSSPhotoshopNeural.exe'))
$ErrorActionPreference = 'Stop'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture = Join-Path $tempRoot ('dlss-player-cache-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
try {
    $player = Join-Path $fixture 'DLSSVideoPlayer.exe'
    [IO.File]::WriteAllText($player, 'fixture EXE path; never executed')
    $ini = Join-Path $fixture 'DLSSVideoPlayer.ini'
    $original = Join-Path $fixture 'original.png'
    [IO.File]::WriteAllText($original, 'keep original')
    $portable = Join-Path $fixture 'cache\v1'
    foreach ($bucket in @('sources','renders','staging','frame-generation','live','thumbs')) {
        $folder = Join-Path $portable $bucket
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $folder 'fixture.bin'), [byte[]](1,2,3))
    }
    $history = Join-Path $portable 'recent-videos.dat'
    [IO.File]::WriteAllText($history, 'keep history')
    # Automatic storage must follow the EXE even when a saved path is stale.
    [IO.File]::WriteAllText($ini, "[Storage]" + [Environment]::NewLine + "CacheDirectory=C:\moved-player\cache\v1" + [Environment]::NewLine + "CacheDirectoryAutomatic=1")
    $result = & $Runner --clear-player-cache $player | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 0) 'Portable cache clear failed.'
    Check ($result.bytes -eq 18 -and [IO.Path]::GetFullPath($result.root) -eq $portable) 'Portable cache location or byte count was incorrect.'
    Check ([IO.File]::ReadAllText($original) -eq 'keep original') 'Original image changed.'
    Check ([IO.File]::ReadAllText($history) -eq 'keep history') 'Player history changed.'
    $second = & $Runner --clear-player-cache $player | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 0 -and $second.bytes -eq 0) 'Repeated clear must be harmless.'
    # A private ping process stands in for an open player at the selected EXE
    # path. No real player is closed or used as a destructive test target.
    Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\ping.exe') -Destination $player -Force
    $guardFrame = Join-Path $portable 'renders\guard.bin'
    [IO.File]::WriteAllBytes($guardFrame, [byte[]](1,2,3))
    $standIn = Start-Process -FilePath $player -ArgumentList '-t','127.0.0.1' -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $fixture 'process.log') -RedirectStandardError (Join-Path $fixture 'process-error.log')
    try {
        Check (-not $standIn.WaitForExit(250)) 'The private player stand-in failed to start.'
        $refused = & $Runner --clear-player-cache $player 2>&1
        Check ($LASTEXITCODE -ne 0 -and ($refused -join ' ').Contains('Close the selected DLSS Video Player')) 'An open selected player must block cache clearing.'
        Check (Test-Path -LiteralPath $guardFrame) 'An open player cache was deleted.'
    } finally {
        if (-not $standIn.HasExited) { Stop-Process -Id $standIn.Id -Force; $standIn.WaitForExit() }
        $standIn.Dispose()
    }
    $custom = Join-Path $fixture 'custom-cache-Ü'
    New-Item -ItemType Directory -Path (Join-Path $custom 'renders\test'), (Join-Path $custom "staging\source-$PID-1"), (Join-Path $custom "live\pid$PID") -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $custom 'renders\test\neural.mkv'), [byte[]](1,2,3,4))
    [IO.File]::WriteAllText((Join-Path $custom "staging\source-$PID-1\working.bin"), 'keep live job')
    [IO.File]::WriteAllText((Join-Path $custom "live\pid$PID\segment.bin"), 'keep live segment')
    [IO.File]::WriteAllText($ini, "[Storage]" + [Environment]::NewLine + "CacheDirectory=$custom" + [Environment]::NewLine + "CacheDirectoryAutomatic=0", [Text.Encoding]::Unicode)
    $result = & $Runner --clear-player-cache $player | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 0 -and $result.bytes -eq 4 -and [IO.Path]::GetFullPath($result.root) -eq $custom) 'Custom cache clear failed.'
    Check (Test-Path -LiteralPath (Join-Path $custom "staging\source-$PID-1\working.bin")) 'An active staging job was deleted.'
    Check (Test-Path -LiteralPath (Join-Path $custom "live\pid$PID\segment.bin")) 'An active live session was deleted.'
    $missing = Join-Path $fixture 'missing-custom-cache'
    [IO.File]::WriteAllText($ini, "[Storage]" + [Environment]::NewLine + "CacheDirectory=$missing" + [Environment]::NewLine + "CacheDirectoryAutomatic=0")
    $result = & $Runner --clear-player-cache $player | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 0 -and $result.bytes -eq 0 -and -not (Test-Path -LiteralPath $missing)) 'An absent cache must not be created.'
    # Reject a cache bucket redirected outside the selected root.
    $unsafe = Join-Path $fixture 'redirected-cache'
    $external = Join-Path $fixture 'external-originals'
    New-Item -ItemType Directory -Path $unsafe, $external -Force | Out-Null
    $sentinel = Join-Path $external 'original.png'
    [IO.File]::WriteAllText($sentinel, 'keep external original')
    $junction = Join-Path $unsafe 'renders'
    New-Item -ItemType Junction -Path $junction -Target $external | Out-Null
    [IO.File]::WriteAllText($ini, "[Storage]" + [Environment]::NewLine + "CacheDirectory=$unsafe" + [Environment]::NewLine + "CacheDirectoryAutomatic=0")
    $refused = & $Runner --clear-player-cache $player 2>&1
    Check ($LASTEXITCODE -ne 0) 'A redirected bucket must be rejected.'
    Check ([IO.File]::ReadAllText($sentinel) -eq 'keep external original') 'A redirected bucket deleted an external original.'
    Remove-Item -LiteralPath $junction -Force
    Write-Output 'Player cache passed: portable/custom paths, original/history preservation, open-player guard, active jobs, empty cache, repeated clear, and junction protection.'
    $global:LASTEXITCODE = 0
} finally {
    $absolute = [IO.Path]::GetFullPath($fixture)
    if (-not $absolute.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($absolute).StartsWith('dlss-player-cache-test-', [StringComparison]::Ordinal)) { throw 'Unsafe fixture cleanup.' }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
