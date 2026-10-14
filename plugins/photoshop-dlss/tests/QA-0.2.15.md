# Photoshop agent control — 15 October 2026

## Implementation

- Bridge v4 accepts bounded, authenticated native loopback JSON commands for
  Mix 0–200 and Boolean Pano. Browser origins are rejected on agent routes;
  those routes do not inherit the renderer's wildcard CORS response.
- One panel session and one active Apply are allowed. Idle panels long-poll;
  running jobs send heartbeats. Apply refreshes selection before acceptance.
- The existing panel Apply function pins document/layer before its first await,
  freezes the requested settings and returns source/result layer IDs. Results
  are retained until acknowledged. No scripts or arbitrary file paths are exposed.
- The official MCP C# SDK 2.2.0 provides stdio tools `dlss_apply` and read-only
  `dlss_status`. The Windows EXE is self-contained. Long jobs return an ID after
  25 seconds; waiting for a job never submits another Apply.
- v0.2.15 setup includes the MCP EXE, notices and Codex connection helper.

## Verified

- Panel modal/capture, Mix/Pano, error cleanup and player-cache regressions.
- Agent 0/100/200 settings, typed validation, result IDs, UI setting updates,
  no-document handling and delivery after a lost completion acknowledgement.
- Queue selection/busy guards, long-poll dispatch, idempotent request IDs,
  single dispatch, queued expiry, disconnected/unknown running state and late
  completion. An unknown running job remains reserved rather than retried.
- Real HTTP token, native header and origin enforcement; strict JSON validation.
- Real self-contained MCP stdio initialization (2025-03-26), tool discovery,
  typed schemas, 200%/Pano arguments, completion/status results and invalid input.
- Live Photoshop 27.11.0 with an isolated development plugin, private bridge
  port, copied runtime and a private 512 × 256 layered document. Actual MCP
  Apply at 100% with Pano created Smart Object ID 5 named DLSS - 100% Mix;
  Apply at 0% without Pano created ID 7 named DLSS - 0% Mix. Both reported
  document 278 / source 3. Inspection confirmed the original pixel layer retained
  its ID, name and type and exactly two result layers were added. The fixture
  was closed; the test plugin and temporary services were unloaded/stopped.
- Installer upgrade fixtures preserve unrelated plugins and back up all fifteen
  previous versions; failed install, idempotency and downgrade checks passed.
- The Adobe CCX panel/HTML hashes and manifest content match source; the bridge,
  native payload and self-contained MCP binary match staging. Development hooks
  and test scripts are absent. NSIS includes the Agent folder and the complete
  MCP SDK license from its pinned source commit.
- The final NSIS EXE passed 7-Zip archive testing. Extracted Install.ps1,
  MCP EXE and SDK license hashes match staging/source. The installer skips
  identical agent files and stops only the owned MCP process when replacing it.
- Final installer: 57,942,546 bytes, SHA256
  `C4751953AC32BFE129CBACB656F719FFC098B0D1E9D1E614455E8DD32580932E`.
- MCP EXE: SHA256
  `8454AD60E0E3EFF0208378F985360ADD20EBC2A94CE0BDA90A1F019268FD76BF`.
- The MCP server was installed into the current user's Agent directory and
  registered as dlss-neural-mix in Codex. Production Photoshop still needs the
  plugin installer update; its open user document was retained.

## Limits

Photoshop and the DLSS panel must be loaded. Job history is in bridge memory
and resets with the bridge. A lost panel after dispatch is an unknown outcome,
not proof of failure; check the document/job before another Apply. Only one
Photoshop panel owns the connection at a time.

The previous brightness-dependent colour corruption at 8000 × 4000 remains
inside the experimental neural runtime. This release adds automation and does
not change that renderer. The installed Photoshop was 27.11.0; minimum support
remains 25.0.0.
