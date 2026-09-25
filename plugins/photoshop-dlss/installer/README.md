# DLSS Neural Mix installer

Save your work and close Photoshop before installing or upgrading. An open panel
keeps its previously loaded JavaScript even after a newer `.ccx` is installed;
setup now stops at the file-selection page if Photoshop is still running.

Run `DLSS-Neural-Mix-Setup-win64.exe` and browse to `DLSSVideoPlayer.exe` in an
unpacked video player folder. Setup verifies the pinned neural runtime in that
folder and copies its files, FFmpeg, and FFprobe. It installs this project's
neural-only runner and worker, then installs the Photoshop `.ccx` through
Adobe's Unified Plugin Installer Agent. The full video player executable is
only used to locate the source folder; it is not installed with the plugin.

The ZIP and `Install.cmd` remain available for manual installation. To use a
different unpacked source folder with the manual script, pass its executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -PlayerExePath 'D:\DLSSVideoPlayer\DLSSVideoPlayer.exe'
```

The installer checks the experimental runtime against this project's pinned
hashes. It copies those already-present files into
`%LOCALAPPDATA%\DLSSNeuralMix\Renderer`; the setup executable does not contain
or redistribute them. The Photoshop runner uses its own packaged ReShade
configuration, with the model at the layer's native resolution. Adobe Photoshop
2025 or newer, Creative Cloud Desktop/UPIA, an RTX GPU, and the .NET Core 3.1
x64 runtime are required. Adobe may ask for plugin permission when the panel
starts its local bridge.

When upgrading from an older plugin version, close any running
`DLSSPhotoshopBridge.exe` process before starting Photoshop again. The panel will
identify an old bridge if one is still using the local port.

To validate the selected folder without installing, run
`Install.ps1 -ValidateOnly -PlayerExePath <path>`. Uninstall DLSS Neural Mix
from Windows Installed Apps. The uninstaller removes the plugin and copied
binaries; saved settings and the last render cache are retained.

This is an independent Windows package, not a Marketplace release. See
`payload\THIRD_PARTY.md` for the runtime's separate terms and provenance.

To rebuild, compile the `DLSSPhotoshopNeural` CMake target, then run
`Build-Installer.ps1` to stage the package. Package `dist\uxp\manifest.json`
as a `.ccx` with Adobe UXP Developer Tool, then run
`Build-Installer.ps1 -CcxFile <path-to-ccx> -NsisCompiler <path-to-makensis.exe>`.
This creates the setup EXE and manual ZIP under `dist`.
