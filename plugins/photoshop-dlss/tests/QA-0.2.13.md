# Video player cache correction — 7 October 2026

Clear cache targets the DLSS Video Player selected in setup. Its EXE path is
saved alongside the neural runner path. The background helper calls the same
NeuralCacheManager as the player's own Clear neural cache command and reads
the player's Storage settings on each click.

## Passed

- Native portable cache selection follows the player folder when an old
  automatic path is saved. Explicit custom cache directories are respected.
- Source, render, staging, frame-generation, live, and thumbnail cache data are
  cleared; original images and player history are preserved.
- A running process at the selected player EXE blocks cache clearing. This was
  tested with a private temporary process; the actual open player was retained.
- Active staging jobs and live sessions belonging to another process survive;
  redirected cache buckets are rejected without touching external originals.
- A missing custom cache is not created; repeated clearing reports zero freed
  bytes. Counts and reported paths match private fixture data.
- The HTTP render gate rejects clearing during plugin processing, and an idle
  clear preserves Photoshop's cached frame and its in-memory key.
- Panel logs identify the player cache directory and freed space. Missing or
  outdated bridges, busy requests, and helper failures restore the controls.
- Panel modal/layer/Pano regressions and v0.2.13 upgrade fixtures passed.
- Adobe CCX and NSIS setup v0.2.13 were built. Panel, HTML, and bridge hashes
  match staging; the setup contains the tested native helper and saves the
  selected player EXE. Unicode custom cache paths also passed.

## Limits

Tests used private cache folders. The actual user's video player cache was not
cleared, and their Photoshop documents were not changed. Cache settings,
history, and preflight receipts follow the player's existing clear policy.
