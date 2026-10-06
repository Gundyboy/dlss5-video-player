# Clear cache verification — 7 October 2026

The Clear cache button beside Activity log removes the Photoshop bridge's two
cached neural RGB frames. It reports the number removed and freed disk space;
the next Apply has to render again. It works without an open Photoshop document.

## Passed

- Panel capture, Mix, Pano, layer isolation, and modal scope regressions.
- Clearing without an active document or Photoshop modal scope. Action buttons
  remain disabled while clearing, duplicate clicks are ignored, and successful,
  empty, outdated bridge, busy, and deletion error responses restore the controls.
- The activity log retains existing entries and reports the clear result.
- Both cache files and in-memory keys are cleared. Files from a previous bridge
  session are removed, unrelated settings are preserved, empty clearing is
  idempotent, and a fresh frame can be cached afterwards.
- A locked file produces an error, invalidates stale keys, and can be cleared
  after it unlocks.
- Real loopback HTTP response from the production clear handler with a private
  fixture cache: busy render gate returns 409 without touching frames or
  releasing another request's lock; idle clearing returns exact JSON counts,
  releases the lock, and repeated clearing succeeds.
- Upgrade from v0.2.11: obsolete versions are backed up outside Adobe's plugin
  search path; failed installs and downgrades retain the existing versions.
- Adobe CCX packaging and NSIS v0.2.12 installer build. Embedded panel, HTML,
  and background bridge hashes match the staging files; setup reports v0.2.12.

## Limits

Photoshop is running on this PC. The installed plugin and active documents were
not changed. Install the updated setup with Photoshop closed to load the new UI.
This button clears rendered image frames, not GPU driver shader caches.
