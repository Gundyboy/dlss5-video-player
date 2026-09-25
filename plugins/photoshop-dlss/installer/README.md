# DLSS Neural Mix installer

Run `DLSS-Neural-Mix-Setup-win64.exe` and browse to `DLSSVideoPlayer.exe` in an
unpacked video player folder. Setup verifies that folder, copies its existing
neural runtime, installs the patched player, and installs the Photoshop `.ccx`
through Adobe's Unified Plugin Installer Agent.

The ZIP and `Install.cmd` remain available for manual installation. For a
different unpacked player with the manual script, pass its executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -PlayerExePath 'D:\DLSSVideoPlayer\DLSSVideoPlayer.exe'
```

The installer checks the experimental runtime against this project's pinned
hashes. It copies those already-present files into
`%LOCALAPPDATA%\DLSSNeuralMix\Renderer`; the setup executable does not contain or
redistribute them. It includes the patched open-source player and bridge. Adobe
Photoshop 2025 or newer, Adobe Creative Cloud Desktop/UPIA, an RTX GPU and a
local .NET Core 3.1 x64 runtime are required. Adobe may ask for plugin permission
when the panel first starts its bridge.

To validate without installing, use
`Install.ps1 -ValidateOnly -PlayerExePath <path>`. Uninstall DLSS Neural Mix
from Windows Installed Apps.
The uninstaller removes the Photoshop plugin and copied binaries; saved settings
and cache are retained.

This is an independent Windows package, not a Marketplace release. See
`payload\THIRD_PARTY.md` for the runtime's separate terms and provenance.

To rebuild, run `Build-Installer.ps1` to stage the plugin, package
`dist\uxp\manifest.json` as a `.ccx` with Adobe UXP Developer Tool, then run
`Build-Installer.ps1 -CcxFile <path-to-ccx> -NsisCompiler <path-to-makensis.exe>`.
This creates the setup EXE and the manual ZIP under `dist`.
