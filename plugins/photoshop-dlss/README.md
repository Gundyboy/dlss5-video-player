# DLSS Neural Mix for Photoshop

The UXP panel applies the neural filter to one selected layer at its native
pixel size. Mix blends the verified neural frame with the original RGB pixels.
Photoshop adds the result above the source as a Smart Object named
`DLSS - 50% Mix` (with the chosen percentage). The source layer and its alpha
are retained. At 0%, the plugin duplicates the layer without starting a render.
Mix runs from 0–200%. From 100–200%, it matches the player's guarded luminance
ratio extension, preserving the neural frame's hue rather than extrapolating
RGB channel values past their valid range.

The panel has a strength control, a Pano toggle, an Apply button, progress, and a bounded
activity log with a **Clear cache** button. Its preferred height is 700 pixels when docked and 760 when
floating. Photoshop allows resizing within the manifest bounds where its
workspace permits; the content scrolls in smaller panels and the log grows in
taller ones.

## Panorama mode

Enable **Pano: On** for a layer covering the full horizontal 360° view. The panel
renders the original view, wraps the input horizontally by half its width, and
renders again with the original left/right seam in the middle of the image.
It restores the offset when combining the frames. The shifted view supplies
the outer 2% on each side, with a cosine feather out to 12.5%; the middle 75%
keeps the original render. Mix is then applied once and source alpha is retained.
The new Smart Object keeps the same `DLSS - 50% Mix` naming convention.

The initial Pano operation uses two renders. Both frames are cached so Mix
changes on an unchanged layer can reuse them. Offsets use whole pixels and one
scratch row, with no resampling or extra image width; odd widths use the nearest
whole-pixel half-width offset and its exact inverse. Pano protects the horizontal
wrap seam; it does not repair a seam already present in the source or correct
distortion at the top and bottom poles. At 0% it skips both renders.

## Neural-only path

The Photoshop plugin launches a local .NET bridge, which passes one still image
to `DLSSPhotoshopNeural.exe`. That small runner calls the repository's isolated
`NeuralWorker.exe` for the neural stage only. It then returns one verified frame
for the bridge to decode. Photoshop computes Mix and creates the Smart Object.
The video player's UI, playback, upscaling, frame generation, and multi-stage
export are not run or installed with this plugin.

The bridge listens on `127.0.0.1:47837`; the panel connects using `localhost`
because Photoshop rejects numeric IP addresses in its UXP permission allowlist.
Image pixels stay on the local machine.
Its work files and log live under `%LOCALAPPDATA%\DLSSPhotoshopBridge`; each
finished job's work files are removed. It remembers the two most recent neural
RGB frames on disk, so changing Mix on the same unchanged regular layer or
panorama does not invoke the model again. Cache keys include pixels, dimensions,
runner/worker versions, and the rendering settings contents.

**Clear cache** clears the **DLSS Video Player's** media cache using the same
cache manager as the player's own command. Setup remembers the player EXE you
select. The button reads its current Storage settings, including a custom cache
directory or automatic portable/LocalAppData storage, and logs the cleared path
and freed space. Close that player before clearing. Your Photoshop layers,
original images, player settings/history, and the plugin's two cached Mix frames
are kept. Clearing works without an open Photoshop document and cannot run during
a plugin render. Reinstall and select the new EXE if you move the player folder.

The panel reads RGB pixels at 8 bits per component. A layer must be at least
64 × 64, no more than 8192 pixels on either side, and at most 64 megapixels.
Photoshop converts other document modes to sRGB for this pass. The worker
accepts an opaque still image; the plugin restores the selected layer's alpha
after neural rendering.
The panel commits an active Photoshop tool edit before reading the selected
layer. If Photoshop reports a Smart Object update error, the panel retries the
selected Smart Object or pixel layer in a temporary RGB document containing
only its duplicate. It rasterizes that temporary copy only if Photoshop still
cannot read the Smart Object. The temporary document closes after capture and
the source document is restored before rendering. A missing linked source may
still need to be relinked in Photoshop. The layer name and type appear in the
activity log to help diagnose document-specific failures.

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

The packaged bridge includes its .NET runtime, so the recipient does not need
to install .NET separately. The bridge log is
`%LOCALAPPDATA%\DLSSPhotoshopBridge\bridge.log`; the worker's own log stays
beside `NeuralWorker.exe`.

Run `node tests/modal-flow.test.js`, `node tests/panorama.test.js`,
`dotnet run --project tests/bridge-cache/BridgeCache.Tests.csproj`, and
`powershell -File tests/upgrade.test.ps1` from this plugin folder for the panel,
panorama, cache, and installer regressions. For a live
integration check, copy the panel into an isolated development plugin with a
different plugin ID, include `tests/photoshop-live.js` after `panel.js`, and load
it through Adobe UXP Developer Tool. It tests 50%, 100%, 200%, and 0% in a temporary
512 × 512 document, verifies Smart Object names and types, then closes only that
document. Inspect `globalThis.__dlssDiagnostic` in the development console for
the result. This check uses the real local GPU renderer and may prompt to open
the bridge on first use. Never include this test script in a release package.

Run `dotnet run --project tests/cache-endpoint/CacheEndpoint.Tests.csproj` for
cache clearing's HTTP response and render lock checks. It uses private temporary
frames and a separate loopback listener without touching the installed cache.
Run the player-cache PowerShell test in the tests folder after building the
native runner to check normal/custom cache paths, active players and jobs,
original preservation, and junction protection using temporary fixtures.

After `Build-Installer.ps1`, run `node tests/bridge-pano-smoke.js` with the local
renderer installed and no existing bridge running. It starts the rebuilt bridge
in the background, renders two 512 × 256 panorama views through the real GPU,
checks that repeating the operation reuses both frames, and closes its own bridge.

## Windows installer

Save your work and close Photoshop before running the installer; an open panel
keeps the previous plugin code loaded. Setup verifies the new plugin, archives
obsolete versions outside Adobe's plugin search folder, and stops the previous
idle bridge. Then run
`dist/DLSS-Neural-Mix-Setup-win64.exe` and select the existing unpacked
video player's `DLSSVideoPlayer.exe`. Setup uses that folder only to verify and
copy the separately licensed neural runtime, FFmpeg, and FFprobe. It installs
the neural-only runner, worker, bridge, and Photoshop `.ccx`; it does not copy
or launch the full video player. See `installer/README.md` for requirements and
manual installation.
