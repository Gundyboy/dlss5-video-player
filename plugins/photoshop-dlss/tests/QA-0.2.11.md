# Panorama mode verification — 30 September 2026

Pano renders the original view and a horizontally wrapped half-width view. The
second view supplies the left/right seam, with a cosine feather into the first
view; Mix runs once after this combination. The source alpha and result naming
are retained. The renderer input dimensions do not grow.

## Passed

- Even and odd widths, including 8192 pixels: exact reversible pixel offset,
  correct orientation, and no vertical row mixing or resampling.
- An identity renderer preserves every source pixel. Simulated damage at each
  render's border is excluded from the final image; a symmetric feather leaves
  the middle unchanged and introduces no hard mask boundary.
- Two views run outside Photoshop's modal scope. Mix is applied after combining
  the views. Alpha survives 50%, 100%, and 200% Mix. Pano at 0% skips both renders.
- A failed second pass inserts no partial result and re-enables panel controls.
- The two-frame disk cache retains both views across repeated Mix requests,
  evicts the least recently used frame, rejects missing/truncated files, and
  invalidates changed rendering settings. Touching identical settings does not
  invalidate the frames.
- Real local GPU test at 512 × 256: two verified neural renders, followed by two
  cache hits, with identical combined RGB pixels. The test ran the rebuilt
  background bridge and installed neural-only runner and closed its own process.
- The first GPU test exposed a request handoff race returning HTTP 409 after a
  cache hit. The bridge now allows a short semaphore wait for prior cleanup;
  the repeated GPU test passed all four requests with HTTP 200.
- Modal, layer isolation, bridge permissions, and installer upgrade regressions.
- Adobe CCX packaging and NSIS installer build. The CCX identifies v0.2.11,
  contains the background bridge, and its panel bytes match the source. Installer
  validation passed against the existing v0.26.0 player runtime. The download
  bundle's embedded setup matches the generated installer SHA256.

## Limits

No actual user panorama was supplied for visual comparison, and the new panel
has not been exercised in the affected Photoshop document. The method protects
the horizontal wrap seam; it does not repair an existing source seam or correct
equirectangular pole distortion. Differing details generated in the two views
may still be visible within the feather region on some images.
