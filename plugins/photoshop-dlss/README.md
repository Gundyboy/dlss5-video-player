# DLSS Neural Mix for Photoshop

A Photoshop 2025/2026 UXP panel that sends the selected layer's cropped pixels
through this project's neural renderer. It adds a new Smart Object named
`DLSS - 50% Mix` (using the chosen percentage) immediately above the original.
The original remains available underneath. Mix is computed in RGB before the
Smart Object is created, so transparency is retained without stacking alpha
twice. Zero percent duplicates the layer without launching the renderer.

## Local development

1. Build `DLSSVideoPlayer` from this repository with the complete experimental
   runtime staged in `build-upscaling/Release/neural-runtime/`.
2. Run `./plugins/photoshop-dlss/build-local.ps1` from PowerShell. This builds
   the local companion and writes its player path to `native/bridge-config.json`.
3. In Adobe UXP Developer Tool, click **Add Plugin**, choose
   `plugins/photoshop-dlss/manifest.json`, then **Load** it in Photoshop.
   Open **Plugins > DLSS Neural Mix**.
4. Select one layer, choose 0–100%, and press **Process selected layer**.
   Photoshop asks before it launches the local companion for the first time.

The panel's Activity log shows each step and the bridge's current render stage.
During a long render, the status line shows elapsed time and the log adds an
update every 30 seconds. A failed step stays visible in the log for diagnosis.

The companion listens only on `127.0.0.1:47837` and uses the player's existing
`--render` image path. Pixel data goes over the local loopback connection; the
bridge uses temporary files under `%LOCALAPPDATA%\DLSSPhotoshopBridge` and
removes each completed job's files. It keeps the last neural RGB result while
running, so changing Mix on identical pixels does not run the model again.

The bridge needs the local .NET Core 3.1 runtime. The Photoshop panel currently
handles RGB pixels at 8 bits per component, with each layer at least 64 pixels
wide and tall, no more than 8192 pixels on either side, and no more than 64
megapixels. Photoshop
converts other document modes to sRGB for this pass. The selected layer's alpha
is retained; its pixels are sent to the model as RGB because the current neural
worker accepts opaque still images. The model processes the layer at its native
pixel dimensions. No playback upscaling, frame generation, video range work, or
comparison rendering is requested.

The bridge log is `%LOCALAPPDATA%\DLSSPhotoshopBridge\bridge.log`. The existing
player and worker logs remain beside their executables. This is a local plugin;
it has not been packaged or signed for Adobe Marketplace distribution.

## Windows installer

Run `dist/DLSS-Neural-Mix-Setup-win64.exe`, choose the existing video player's
`DLSSVideoPlayer.exe`, and complete the wizard. It verifies the selected neural
runtime, installs the patched player, and installs the Photoshop `.ccx` through
Adobe's Unified Plugin Installer Agent. See `installer/README.md` for requirements
and manual installation.
