# Photoshop placement recovery — 7 October 2026

The reported v0.2.13 failure occurs during selected-layer capture, before DLSS
rendering. The old catch recognized Smart Object update errors but missed
Photoshop placement error -32005. v0.2.14 recognizes both and captures a
temporary rasterized duplicate in the source document when placement fails.
Temporary source edits are rolled back before rendering. No cache is cleared.

## Passed

- Panel regressions cover direct -32005 failure, failure while isolating a Smart
  Object, pixel-copy isolation, unrecoverable capture, alpha and bounds, control
  recovery, original Smart Object preservation, and temporary document cleanup.
- Existing modal, Mix, Pano, render-failure and video-player cache button checks
  passed; the direct capture path still performs one pixel read.
- Live Photoshop 27.11.0 checks used a private layered Smart Object fixture and
  injected read errors in an isolated development plugin. All four recovery and
  failure modes passed. Captured bytes (including alpha) matched the baseline
  within one level; bounds and selection were retained, source edits rolled
  back, and no temporary documents remained. The fixture closed and the test
  plugin and developer service were unloaded/stopped afterward.
- v0.2.14 installer upgrade fixtures passed, including backup of all fourteen
  previous versions, failed install preservation, unrelated plugin preservation,
  idempotency, downgrade rejection, and package identity checks.
- Adobe CCX and NSIS installer v0.2.14 were built. Packaged panel and HTML match
  source hashes; manifest content matches. Bridge and native payload hashes
  match staging/build output. Development hooks and scripts are absent.
- Installer: 33,347,537 bytes; SHA256
  `29CFC4AC8B3370697292722D67419F6F4C68713494480C768ABF18AC40FB5274`.

## Limits

The user's affected PSD was not supplied. Live tests forced the reported error
in a private fixture; they did not reproduce the underlying host failure in that
PSD. Photoshop 25 remains the minimum supported version; the APIs used are
available there, but this live check ran on the installed Photoshop 27.11.0.
An actually missing linked Smart Object source still needs to be relinked.

Adobe documents rollback and temporary document cleanup in its
[executeAsModal reference](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/media/executeasmodal/).
