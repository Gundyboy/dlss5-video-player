# DLSS Neural Mix agent control — v0.2.15

Setup installs a self-contained Windows MCP stdio server at
`%LOCALAPPDATA%\DLSSNeuralMix\Agent\DLSSNeuralMixMcp.exe`.
No separate Node, Python or .NET installation is required.

## Connect an agent

For Codex, run `Connect-Codex.ps1` from the installed Agent folder, then restart
Codex. It registers just the `dlss-neural-mix` server. The equivalent command is:

```powershell
codex mcp add dlss-neural-mix -- "$env:LOCALAPPDATA\DLSSNeuralMix\Agent\DLSSNeuralMixMcp.exe"
```

Other MCP clients can launch that EXE as a stdio server with no arguments.
Open **Photoshop > Plugins > DLSS Neural Mix**, then wait for **Agent control
connected** in its activity log. The panel must remain loaded in Photoshop.

## Tools

- `dlss_apply(mix, pano)`: integer Mix 0–200, Boolean Pano. Applies to the one
  layer selected when the panel begins the command. It uses the same capture,
  rendering, blending and Smart Object placement as the Apply button.
- `dlss_status(job_id?)`: read panel connection/selection/busy state, or the
  result of a particular Apply job. It does not change Photoshop.

An example tool call is `dlss_apply({"mix":100,"pano":true})`. Select the intended
document and layer with your Photoshop tools before calling it. A successful
result includes document ID, source layer ID, new Smart Object layer ID and name.

Apply waits up to 25 seconds. A longer render returns a running job ID. Poll
`dlss_status` with that ID until completed or failed. **Do not submit another
Apply to wait for the existing job.** A disconnect after dispatch reports unknown,
because Photoshop may still be processing. Check the document and job before retrying.
Only one panel and one Apply can own control at a time; busy or invalid selection
requests are rejected. A selection change during processing prevents insertion
into a different document or layer. Bridge memory retains the last 64 job records;
records do not survive a bridge restart.

The agent interface uses native loopback HTTP on port 47837 and a rotating local
token in `%LOCALAPPDATA%\DLSSPhotoshopBridge\automation-token.txt`. Keep the token
private; do not include it in prompts, logs or source control. No arbitrary scripts,
remote commands, layer deletion or cache clearing are exposed by these MCP tools.

This version adds automation only. The previously reproduced brightness-dependent
neural corruption at 8000 × 4000 remains a renderer limitation. The test panorama
rendered cleanly at 4000 × 2000; agents must not treat a runtime success receipt as
proof of visual quality.

Development tests may set `DLSS_NEURAL_MIX_BRIDGE_URL` to another loopback HTTP
port and `DLSS_NEURAL_MIX_TOKEN_FILE` to a private fixture token file.

Codex configuration: https://learn.chatgpt.com/docs/extend/mcp?surface=cli
