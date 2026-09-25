# Shared by setup and the upgrade regression test. Old plugin directories must
# leave Adobe's External folder: Photoshop may otherwise discover the oldest one.
function Get-DlssPackageIdentity {
    param([Parameter(Mandatory)][string]$Package)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $manifestEntry = $archive.GetEntry('manifest.json')
        $panelEntry = $archive.GetEntry('panel.js')
        if (-not $manifestEntry -or -not $panelEntry) { throw 'The plugin package is incomplete.' }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.id -cne 'com.reality3d.dlssneuralmix') { throw 'Unexpected plugin package ID.' }
        $null = [version]$manifest.version
        $stream = $panelEntry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $sha.Dispose() }
        return [pscustomobject]@{ Id = $manifest.id; Version = $manifest.version; PanelHash = $hash }
    } finally { $archive.Dispose() }
}

function Assert-DlssDirectory {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected a regular directory: $Path"
    }
    # Check ancestors too, so a junction cannot redirect a validated path.
    $parent = $item.Parent
    while ($parent) {
        if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing a directory under a junction or symbolic link: $Path"
        }
        $parent = $parent.Parent
    }
}

function Move-ObsoleteDlssPlugins {
    param(
        [Parameter(Mandatory)][string]$ExternalRoot,
        [Parameter(Mandatory)][string]$BackupRoot,
        [Parameter(Mandatory)]$Identity
    )
    Assert-DlssDirectory $ExternalRoot
    $external = (Resolve-Path -LiteralPath $ExternalRoot).Path.TrimEnd('\')
    $current = Join-Path $external ($Identity.Id + '_' + $Identity.Version)
    Assert-DlssDirectory $current
    $manifest = Get-Content -LiteralPath (Join-Path $current 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.id -cne $Identity.Id -or $manifest.version -cne $Identity.Version -or
        (Get-FileHash -LiteralPath (Join-Path $current 'panel.js') -Algorithm SHA256).Hash -ne $Identity.PanelHash) {
        throw 'Adobe did not install the expected plugin files. Previous versions were retained.'
    }

    $obsolete = @()
    foreach ($folder in Get-ChildItem -LiteralPath $external -Directory -Filter ($Identity.Id + '_*')) {
        if ($folder.FullName -ieq $current) { continue }
        Assert-DlssDirectory $folder.FullName
        $oldManifestPath = Join-Path $folder.FullName 'manifest.json'
        if (-not (Test-Path -LiteralPath $oldManifestPath -PathType Leaf)) { continue }
        $old = Get-Content -LiteralPath $oldManifestPath -Raw | ConvertFrom-Json
        if ($old.id -cne $Identity.Id) { continue }
        if ([version]$old.version -gt [version]$Identity.Version) {
            throw 'A newer DLSS Neural Mix version is installed. Upgrade cleanup was cancelled.'
        }
        if (Get-ChildItem -LiteralPath $folder.FullName -Recurse -Force |
                Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
                Select-Object -First 1) {
            throw "Refusing to move a plugin containing symbolic links: $($folder.FullName)"
        }
        $obsolete += $folder
    }
    if ($obsolete.Count -eq 0) { return }

    $backup = [IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
    if ($backup -ieq $external -or $backup.StartsWith($external + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Plugin backups must be outside Adobe plugin discovery.'
    }
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    Assert-DlssDirectory $backup
    $batch = Join-Path $backup ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $batch | Out-Null
    foreach ($folder in $obsolete) {
        $source = (Resolve-Path -LiteralPath $folder.FullName).Path
        $destination = [IO.Path]::GetFullPath((Join-Path $batch $folder.Name))
        # Verify absolute targets immediately before each recursive directory move.
        if ((Split-Path -Parent $source) -ine $external -or
            -not $destination.StartsWith($backup + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to move plugin files outside the verified upgrade paths.'
        }
        $moveError = $null
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            try {
                Move-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
                $moveError = $null
                break
            } catch {
                $moveError = $_
                if ($attempt -lt 5) { Start-Sleep -Milliseconds 250 }
            }
        }
        if ($moveError) {
            throw "Could not archive $($folder.Name). A process may still be using its files: $($moveError.Exception.Message)"
        }
        Write-Output "Archived obsolete plugin: $($folder.Name)"
    }
    Write-Output "Previous plugin files saved in $batch"
}

function Stop-DlssBridgeForUpgrade {
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $bridges = @(Get-CimInstance Win32_Process -Filter "Name = 'DLSSPhotoshopBridge.exe'" |
        Where-Object { $_.SessionId -eq $session })
    if ($bridges.Count) {
        $state = $null
        try { $state = Invoke-RestMethod 'http://127.0.0.1:47837/status' -TimeoutSec 2 -ErrorAction Stop }
        catch { # Legacy bridges do not expose a status endpoint.
        }
        if ($state -and $state.busy) { throw 'The DLSS bridge is busy. Wait for the render to finish before installing.' }
    }
    foreach ($bridge in $bridges) {
        if (-not $bridge.ExecutablePath) { throw 'Cannot verify the running DLSS bridge. Close it before installing.' }
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($bridge.ProcessId)" |
            Where-Object { $_.ExecutablePath -ine (Join-Path $env:WINDIR 'System32\conhost.exe') })
        if ($children.Count) { throw 'The DLSS bridge has an active renderer. Wait for it to finish before installing.' }
    }
    foreach ($bridge in $bridges) {
        # ExecutablePath and session were already verified through Win32_Process.
        # Get-Process.Path can be blank when 32-bit setup inspects this 64-bit
        # process, which previously caused setup to skip the stop without error.
        if (-not (Get-Process -Id $bridge.ProcessId -ErrorAction SilentlyContinue)) { continue }
        Stop-Process -Id $bridge.ProcessId -ErrorAction Stop
        Wait-Process -Id $bridge.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
        if (Get-Process -Id $bridge.ProcessId -ErrorAction SilentlyContinue) {
            throw 'The previous DLSS bridge did not stop.'
        }
        Write-Output 'Stopped the previous local renderer bridge.'
    }
}
