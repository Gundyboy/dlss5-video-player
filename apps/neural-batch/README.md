# DLSS Neural Batch

Standalone Windows application for processing a queue of still images through
the same verified neural-only runner used by the Photoshop plugin. Photoshop is
not required. The app accepts PNG, JPG/JPEG, TIFF and BMP and saves lossless
8-bit sRGB PNG files, retaining alpha and applying JPEG/TIFF EXIF orientation.
Multi-frame TIFFs use their first frame. Original files are never overwritten.

1. Install `DLSS-Neural-Batch-Setup-win64.exe` and choose the existing unpacked
   player's `DLSSVideoPlayer.exe`. The runtime is verified and copied into this
   application's own renderer folder. The full player is not launched.
2. Add images, add a folder, or drop files/folders into the queue. Folder imports
   include supported files directly inside that folder. You can add more images
   while processing; they use the current batch settings.
3. Choose the output folder and Mix (0–200%). Enable Pano for full-width 360°
   panorama images. Start the queue.

The queue runs one image at a time. Pause takes effect after the current image;
Stop cancels the active helper and leaves waiting images queued. Retry failed
also retries stopped images. One failed image is logged and the queue continues.
The window can be resized. The bounded activity log contains the current stages
and output paths, and the status row shows elapsed time.

Output names include Mix and Pano, for example
`Tower - DLSS 100% - Pano.png`. A numeric suffix is added on name collisions.
PNG output is committed only after processing and encoding succeed; cancelled
jobs leave no partial final image. Output is flattened and does not preserve
source metadata or a source TIFF's higher bit depth.

Pano uses the original view plus an exact horizontal half-width wrap, supplying
the seam from the second view and feathering into the original render over
12.5% of the width per side. Mix runs once after the seam combination. It does
not fix seams already present in the source or pole distortion. At 0% the app
saves the original decoded pixels without running the model.

Images must be 64–8192 pixels per side and at most 64 megapixels. A compatible
RTX GPU and the existing verified neural runtime are required. The application
bundles .NET and the native helpers' C++ runtime; recipients do not need to
install .NET, Visual C++ separately, or Creative Cloud. Processing
and all temporary helpers stay local and helpers open no console window.

The two most recent neural frames are cached on disk. Duplicate inputs and Mix
changes can reuse these frames, including both Pano views, while the app remains
open. Cache keys include image pixels, dimensions, runner/worker versions and
rendering settings. The app decodes only the current image and builds no thumbnail
cache. Each verified frame still uses the existing runner's GPU helper lifecycle.

App settings, renderer and work cache live in
`%LOCALAPPDATA%\DLSSNeuralBatch`. Uninstall removes the app, copied renderer and
work cache, while retaining settings and user output images. The installer does
not redistribute the experimental runtime; it copies it from the user's existing
player folder. See `THIRD_PARTY.md` and the copied runtime notices for terms.

## Build and verify

Run `installer/Build.ps1` with .NET 10 SDK, the compiled neural-only runner and
worker, and NSIS. It can reuse this checkout's bundled .NET and NSIS toolchains.
The self-contained EXE, portable ZIP and setup EXE are created under `dist`.
The portable ZIP also includes `RuntimeFiles`; choose the player executable from
the application on first launch to configure the renderer.

To add the batch installer to an existing player and Photoshop bundle, run
`installer/Build-CombinedBundle.ps1 -SourceBundle <existing bundle.zip>`.
It produces `dist/DLSS Video Player - PS Plugin.zip`, adds the batch installer
and setup instructions, and verifies that every original payload file is
unchanged and the added installer matches its SHA256.

Run `dotnet run --project tests/DLSSNeuralBatch.Tests.csproj -- dist` from this
folder for pixel math, orientation/alpha, output collision/cleanup, queue behavior,
atomic runtime installation, owned process cancellation and offscreen UI layout
checks. Add `--gpu` to exercise two Pano images and a deliberately bad image using
the existing installed Photoshop neural runner. That test does not open or alter
Photoshop documents. It removes only its own generated fixture folder.
Add `--packaged-runtime` together with `--gpu` to install and exercise the real
runtime in an isolated fixture using the portable package's helpers and bundled
C++ runtime. This deployment test currently uses the player folder on William's
Desktop; adjust that fixture path when testing on another development computer.
