$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\installer\Upgrade.ps1')
$fixture = Join-Path $PSScriptRoot ('..\dist\upgrade-test-' + [guid]::NewGuid().ToString('N'))
$external = Join-Path $fixture 'External'
$backups = Join-Path $fixture 'Backups'
$id = 'com.reality3d.dlssneuralmix'
New-Item -ItemType Directory -Path $external -Force | Out-Null

function Add-Plugin([string]$Version, [string]$Id = $id) {
    $folder = Join-Path $external ($Id + '_' + $Version)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    @{ id = $Id; version = $Version } | ConvertTo-Json | Set-Content (Join-Path $folder 'manifest.json')
    'expected panel' | Set-Content (Join-Path $folder 'panel.js')
    return $folder
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Expect-Failure([scriptblock]$Action, [string]$MessagePattern) {
    $failed = $false
    try { & $Action } catch {
        $failed = $true
        Assert ($_.Exception.Message -match $MessagePattern) ('Unexpected error: ' + $_.Exception.Message)
    }
    Assert $failed 'Expected upgrade validation to fail.'
}

$old = Add-Plugin '0.2.0'
$null = Add-Plugin '0.2.1'
$null = Add-Plugin '0.2.2'
$null = Add-Plugin '0.2.3'
$current = Add-Plugin '0.2.4'
$unrelated = Add-Plugin '0.1.0' 'com.example.unrelated'
$impostor = Add-Plugin '0.0.1'
@{ id = 'com.example.unrelated'; version = '0.0.1' } | ConvertTo-Json |
    Set-Content (Join-Path $impostor 'manifest.json')
$identity = [pscustomobject]@{ Id = $id; Version = '0.2.4'; PanelHash = 'WRONG' }
Expect-Failure { Move-ObsoleteDlssPlugins $external $backups $identity } 'expected plugin files'
Assert (Test-Path $old) 'A failed installation must preserve the old plugin.'
$identity.PanelHash = (Get-FileHash (Join-Path $current 'panel.js') -Algorithm SHA256).Hash
Expect-Failure { Move-ObsoleteDlssPlugins $external (Join-Path $external 'Backup') $identity } 'outside Adobe'
Assert (Test-Path $old) 'An invalid backup location must not move files.'
Move-ObsoleteDlssPlugins $external $backups $identity
Assert (-not (Test-Path $old)) 'The old plugin must leave Adobe discovery.'
Assert (Test-Path $current) 'The new plugin must stay installed.'
Assert (Test-Path $unrelated) 'Unrelated plugins must remain untouched.'
Assert (Test-Path $impostor) 'A folder name alone must not establish plugin ownership.'
$archived = @(Get-ChildItem $backups -Directory | Get-ChildItem -Directory)
Assert ($archived.Count -eq 4) 'All four obsolete versions must be backed up.'
Assert ((Get-FileHash (Join-Path $archived[0].FullName 'panel.js')).Hash -eq $identity.PanelHash) 'Backup content changed.'
Move-ObsoleteDlssPlugins $external $backups $identity
Assert (@(Get-ChildItem $backups -Directory).Count -eq 1) 'Repeated cleanup should be a no-op.'
$newer = Add-Plugin '0.3.0'
Expect-Failure { Move-ObsoleteDlssPlugins $external $backups $identity } 'newer DLSS'
Assert (Test-Path $newer) 'A newer plugin must never be archived by a downgrade.'

# Prove the identity comes from the actual archive, including its panel bytes.
$package = Join-Path $fixture 'test.ccx'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($current, $package)
$packaged = Get-DlssPackageIdentity $package
Assert ($packaged.Id -eq $identity.Id -and $packaged.Version -eq $identity.Version -and
    $packaged.PanelHash -eq $identity.PanelHash) 'Package identity did not match its content.'
Write-Output 'Upgrade regression passed: failed install, backup boundaries, old copies, unrelated plugins, idempotency, downgrade, package integrity.'
Write-Output "Test artifacts: $fixture"
