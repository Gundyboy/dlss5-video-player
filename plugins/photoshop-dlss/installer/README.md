# DLSS Neural Mix installer

Run `Install.cmd` on Windows. It installs the `.ccx` Photoshop plugin through
Adobe's Unified Plugin Installer Agent and prepares the local neural renderer.
The default source is `Desktop\DLSSVideoPlayer-v0.26.0-win64`. For a different
unpacked complete player, run PowerShell with `-RuntimePath`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -RuntimePath 'D:\DLSSVideoPlayer-v0.26.0-win64'
```

The installer checks the experimental runtime against this project's pinned
hashes. It copies those already-present files into
`%LOCALAPPDATA%\DLSSNeuralMix\Renderer`; the installer ZIP does not contain or
redistribute them. It includes the patched open-source player and bridge. Adobe
Photoshop 2025 or newer, Adobe Creative Cloud Desktop/UPIA, an RTX GPU and a
local .NET Core 3.1 x64 runtime are required. Adobe may ask for plugin permission
when the panel first starts its bridge.

To validate the source folder without installing, use `Install.ps1 -ValidateOnly`.
The plugin can be removed from Creative Cloud Desktop > Manage Plugins. The
copied renderer remains under `%LOCALAPPDATA%\DLSSNeuralMix` until you remove it.

This is an independent Windows package, not a Marketplace release. See
`payload\THIRD_PARTY.md` for the runtime's separate terms and provenance.
