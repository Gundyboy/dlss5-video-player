# v0.2.5 installer verification — 25 September 2026

The v0.2.4 setup installed its CCX, then failed while archiving v0.2.3 with
`Access to the path ...com.reality3d.dlssneuralmix_0.2.3 is denied`.

The folder owner and ACL granted the current user full control. Process and
module inspection identified an idle `DLSSPhotoshopBridge.exe` running directly
from the v0.2.3 `native` directory and holding its EXE and DLL open.

Setup had attempted to stop the bridge, but `Stop-DlssBridgeForUpgrade` checked
`Get-Process.Path` again after already verifying `Win32_Process.ExecutablePath`.
That property can be unavailable when the 32-bit installer PowerShell inspects
the 64-bit bridge, causing the stop to be skipped without an error.

## Checks passed

- The revised routine verifies bridge name, executable path, Windows session,
  renderer status, and child processes through `Win32_Process`, then stops the
  verified PID directly.
- The routine stopped the real idle v0.2.3 bridge when invoked through 32-bit
  Windows PowerShell, matching the installer's process environment.
- The previously failing real v0.2.3 folder then moved into the reversible
  `%LOCALAPPDATA%\DLSSNeuralMix\PluginBackups` archive successfully.
- Setup repeats bridge shutdown immediately after Adobe's installer returns and
  retries a transient folder move five times before showing a specific error.
- Modal/Mix and upgrade fixture regressions passed. All installer PowerShell
  scripts parsed successfully.
- v0.2.5 packaged and the NSIS installer compiled. The corrected package was
  installed using the selected DLSS Video Player v0.26.0 path. It archived the
  verified v0.2.4 folder and reported installation success.
