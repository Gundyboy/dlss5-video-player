param([string]$ServerPath = (Join-Path $PSScriptRoot 'DLSSNeuralMixMcp.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf)) { throw "MCP server missing: $ServerPath" }
$server = (Resolve-Path -LiteralPath $ServerPath).Path
$codex = Get-Command codex.exe -ErrorAction SilentlyContinue
if (-not $codex) { throw 'Codex CLI was not found. Add the command shown in README.md to your MCP client settings.' }
& $codex.Source mcp add dlss-neural-mix -- $server
if ($LASTEXITCODE -ne 0) { throw 'Codex could not register DLSS Neural Mix.' }
Write-Output 'DLSS Neural Mix registered with Codex. Restart Codex to load the new tools, then open the Photoshop plugin panel.'
