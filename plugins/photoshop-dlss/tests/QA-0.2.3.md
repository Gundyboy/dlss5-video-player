# v0.2.3 verification — 25 September 2026

Tested on Windows with Photoshop 27.5.0 / UXP API 2 and the installed GPU runtime.

## Root cause

Adobe's plugin discovery selected `com.reality3d.dlssneuralmix_0.2.0` while
0.2.1 and 0.2.2 also existed in its External plugin directory. Photoshop had
already restarted after the 0.2.2 install. A restart alone did not fix discovery.
The corrected modal capture was therefore never used by the installed panel.

Live testing also found that UXP rejected `http://127.0.0.1:47837` despite the
matching manifest permission. `http://localhost:47837` succeeded with the same
bridge and a narrow matching permission. The bridge remains bound to loopback.

## Checks passed

- Exact panel source in an isolated development plugin captured pixels inside
  `executeAsModal`, rendered outside it, and returned Smart Objects inside it.
- A real 512 × 512 source completed at 50%, 100%, and 0%. Names and layer types
  matched each percentage; the original source layer remained present.
- The packaged native bridge started through the panel's normal launch path.
  The first run waited for Photoshop's launch request; GPU processing then took
  about 8 seconds. The cached 100% run took 598 ms; the 0% duplicate took 461 ms.
  These are measurements for this test image, not general performance promises.
- The scratch document closed without saving. The original user document and
  source layer remained open; the temporary development plugin was unloaded.
- The installer's shutdown function stopped the verified idle test bridge.
- `modal-flow.test.js` checked modal scope, pixel mixing, image-data disposal,
  matching localhost permissions, and explicit permission-error reporting.
- `upgrade.test.ps1` checked failed-install preservation, backup path boundaries,
  three obsolete versions, unrelated plugins, repeated cleanup, downgrade
  protection, and package identity/hash extraction.
- Adobe's strict package validator passed. NSIS compiled the v0.2.3 EXE.
  The CCX panel hash matched source, installer PowerShell parsed successfully,
  and the staged upgrade helper matched source.

## Remaining host validation

The new release installer has not been run against the user's installed plugin
folders: Photoshop remains open with the user's document. Save and close it,
run the new installer, then confirm the panel displays v0.2.3. Upgrade cleanup
was exercised against representative fixture directories.

The user's selected layer is 15000 × 11250 pixels. It exceeds the existing
8192-pixels-per-side / 64-megapixel renderer limits. The new panel reports actual
dimensions in the oversized-image error; this release does not add tiling.
