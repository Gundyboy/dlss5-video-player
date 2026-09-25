# DLSS Neural Mix for Photoshop

The UXP panel applies the neural filter to one selected layer at its native
pixel size. Mix blends the verified neural frame with the original RGB pixels.
Photoshop adds the result above the source as a Smart Object named
`DLSS - 50% Mix` (with the chosen percentage). The source layer and its alpha
are retained. At 0%, the plugin duplicates the layer without starting a render.

The panel has one strength control, an Apply button, progress, and a bounded
activity log. Its preferred height is 700 pixels when docked and 760 when
floating. Photoshop allows resizing within the manifest bounds where its
workspace permits; the content scrolls in smaller panels and the log grows in
taller ones.

## Neural-only path

The Photoshop plugin launches a local .NET bridge, which passes one still image
to `DLSSPhotoshopNeural.exe`. That small runner calls the repository's isolated
`NeuralWorker.exe` for the neural stage only. It then returns one verified frame
for the bridge to decode. Photoshop computes Mix and creates the Smart Object.
The video player's UI, playback, upscaling, frame generation, and multi-stage
export are not run or installed with this plugin.

The bridge listens on `127.0.0.1:47837`. Image pixels stay on the local machine.
Its work files and log live under `%LOCALAPPDATA%\DLSSPhotoshopBridge`; each
finished job's work files are removed. It remembers the last neural RGB frame,
so changing Mix on the same unchanged layer does not invoke the model again.

The panel reads RGB pixels at 8 bits per component. A layer must be at least
64 × 64, no more than 8192 pixels on either side, and at most 64 megapixels.
Photoshop converts other document modes to sRGB for this pass. The worker
accepts an opaque still image; the plugin restores the selected layer's alpha
after neural rendering.

## Local development

1. Build the `DLSSPhotoshopNeural` CMake target. It builds `NeuralWorker` too.
   Stage the experimental runtime, FFmpeg, and FFprobe beside the runner as in
   `build-upscaling/Release/`.
2. Run `plugins/photoshop-dlss/build-local.ps1`. This builds the bridge and
   points it at the neural-only runner.
3. Add `plugins/photoshop-dlss/manifest.json` in Adobe UXP Developer Tool,
   load it in Photoshop, then open **Plugins > DLSS Neural Mix**.

After building the installer staging files, run
`plugins/photoshop-dlss/runner/Smoke-Test.ps1` for an isolated GPU test. It
renders one frame from a folder without the full player executable.

The bridge needs the local .NET Core 3.1 x64 runtime. The bridge log is
`%LOCALAPPDATA%\DLSSPhotoshopBridge\bridge.log`; the worker's own log stays
beside `NeuralWorker.exe`.

## Windows installer

Save your work and close Photoshop before running the installer; an open panel
keeps the previous plugin code loaded. Then run
`dist/DLSS-Neural-Mix-Setup-win64.exe` and select the existing unpacked
video player's `DLSSVideoPlayer.exe`. Setup uses that folder only to verify and
copy the separately licensed neural runtime, FFmpeg, and FFprobe. It installs
the neural-only runner, worker, bridge, and Photoshop `.ccx`; it does not copy
or launch the full video player. See `installer/README.md` for requirements and
manual installation.
