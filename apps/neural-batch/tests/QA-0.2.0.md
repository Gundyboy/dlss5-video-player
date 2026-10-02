# DLSS Neural Batch 0.2.0 — upscaler

Verified on William's Windows workstation on 2 October 2026.

- Native runner and batch application compile successfully. The native runner
  retains the original Photoshop command line and adds output dimensions and
  a neural on/off mode using the player's existing Super Resolution backend.
- Mix 0% with 2× uses the single-shot SR-only worker and validates zero neural
  frames plus a completed SR evaluation. Mix 100% uses the resident neural
  worker with SR preceding neural rendering in the same pass. Both validate a
  single output frame. Unsupported sizes fail before GPU processing.
- Real GPU tests passed on an isolated installed copy of the package's runtime:
  0%, 100%, 50%, and 200% Mix at 2×, with Pano, a 129 × 65 input, exact
  258 × 130 output, and varying alpha. Partial Mix matched the SR-only/neural
  reference blend; 200% matched the existing guarded enhancement calculation.
- Mix changes reused all four SR/NR panorama cache files without rerendering.
  Cache keys distinguish mode and output size; capacity stays at four frames.
  The Photoshop bridge's default two-frame cache regression tests passed.
- Original-size neural Pano processing still passed. The queue continued after
  an invalid image and an oversized 8194 × 128 upscale request. Source files
  were preserved and completed jobs left no temporary job directories.
- Alpha interpolation, horizontal alpha wrap, odd/even offset inversion,
  output limits, safe filenames, pause/resume/stop, and queue order passed.
- Offscreen WPF layouts and bindings passed at 1120 × 880 and 820 × 760 content
  sizes. The upscaler label and compact layout were visually inspected. Window
  minimum height is 800 to allow for window chrome; pixel dimensions also
  appear in the activity log and queue tooltips.

Tests use synthetic images. A fresh Windows installation and a second GPU
remain untested. Actual image quality and available VRAM depend on the input
and hardware. Output remains flattened 8-bit sRGB PNG with separately resized
alpha. DLSS upscaling is currently 2×; input and output must remain within
8192 pixels per side and 64 megapixels.

## Installer and portal publication

- The final self-contained EXE reports version 0.2.0. Runtime validation passed,
  and its packaged native runner matched the GPU-tested runner's SHA256.
- Release publish and NSIS setup build passed. Invalid/duplicate native options
  were rejected without starting GPU work.
- The combined ZIP preserved all 25 existing player/Photoshop payload files,
  contained the new installer byte for byte, and verified all 12 model pins.
- Published the combined ZIP to the NAS and Servermanagerwh, and added a direct
  `DLSS Neural Batch` installer quick link to the portal. Existing unrelated
  page content was preserved; rollback files remain in the generated dist area.
- The live page and both download APIs passed. Complete HTTP downloads returned
  status 200 and matched the local release checksums:
  - Installer: 43,915,454 bytes,
    `226EC0C69AEEF2BBBD30AE2DA2A6206465E6EA950651D6C7E9F5451298124DC1`.
  - Combined ZIP: 405,009,833 bytes,
    `EDDE01EF7F05C22DB8E8317FC4756199129D33E65FA485098CDDAF56C6AB9FB4`.
