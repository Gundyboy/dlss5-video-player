# DLSS Neural Batch 0.1.0 verification

Verified on William's Windows workstation on 30 September 2026.

## Processing and queue

- Real GPU queue: two 256 × 128 images with Pano enabled and Mix 100%, with a
  deliberately invalid image between them. Both valid images completed; the
  failed image was logged and did not stop the queue.
- Repeated that GPU test using an isolated runtime installed from the selected
  player folder and the portable package's native helpers and C++ runtime.
- Output dimensions and all alpha bytes matched their inputs. Completed jobs
  left no temporary job folders. Tests did not open Photoshop or alter its
  documents, installed renderer, or source images.
- Queue order, pause after the current image, resume, adding images during a
  run, pausing the last image, and cancellation retaining waiting images passed.
- A spawned hidden helper was cancelled in under five seconds. Cancellation
  targets only that helper's process tree.
- Mix 0 bypasses the neural runner. Mix 50/100/200 and the plugin's guarded
  200% blend matched known outputs. Even and odd panorama offsets reverse
  exactly; seam composition excludes artificial borders from either render.

## Files and layout

- PNG color/alpha, JPEG EXIF orientation, all eight orientation transforms,
  output name collisions, original-file preservation, cancelled output cleanup,
  and dimension validation passed.
- Atomic runtime installation passed. An invalid upgrade retained the previous
  renderer. Extra CRT modules are excluded from the pinned model folder; the
  worker inherits the runner folder in its private process PATH.
- WPF startup bindings and offscreen layouts passed at 1120 × 840 and
  820 × 760. Both rendered layouts were visually inspected, including the log,
  queue controls, renderer controls, and Made by Will footer.
- Activity output is bounded to 200 messages, with each message limited to
  4096 characters. Native error details are bounded as well.

## Packaging

- Release publish and NSIS installer build completed successfully.
- The self-contained portable EXE validated the real selected runtime and
  exited successfully with a report; it does not require a separately installed
  .NET runtime. The package includes Microsoft-signed C++ redistributable DLLs.
- Setup validates only when continuing from its executable picker page and
  uses hidden configuration commands. Runtime copies are staged before swap.
- Uninstall is restricted to this product's exact local application folder,
  refuses while the app is open, and preserves settings and user output images.

## Remaining checks

The installer has not been exercised on a completely fresh Windows installation
or a second GPU. GPU tests used synthetic images; actual panorama aesthetics,
source seams, pole distortion, and available VRAM depend on the user's inputs
and hardware. The app uses the existing runner's verified frame lifecycle,
including GPU helper initialization for an uncached frame. It writes flattened
8-bit sRGB PNGs and does not preserve full source metadata or HDR/TIFF bit depth.
