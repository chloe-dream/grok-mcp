# grok-mcp

Local stdio MCP server that wraps the xAI Grok API. Built so Claude Code can:

- **Talk to Grok as a buddy** — second opinion, debugging help, design review
- **Generate, edit, and analyze images** — game graphics, app assets, etc.

The server is general-purpose, single-process, no external state, and intentionally small.

## Tools

| Tool | What it does |
|---|---|
| `grok_chat` | Chat completion. Stateless by default; pass `session_id` to keep an in-memory thread within the server's process lifetime. |
| `grok_generate_image` | Text → image, saved to `output_path` and returned inline so Claude sees it. |
| `grok_edit_image` | Input image(s) + prompt → modified image, same output handling. |
| `grok_describe_image` | Vision: ask Grok to analyze image(s). Returns text. |

`output_path` on the image tools must be **absolute**. For `n>1`, the index is inserted before the extension (`mushroom.png` → `mushroom-1.png`, `mushroom-2.png`, …). Image-input parameters accept any of: `https://…` URL, absolute file path, `data:image/...;base64,…` URI, or raw base64.

## Build

Requires .NET 10 SDK.

```powershell
dotnet build -c Release
```

Output: `bin\Release\net10.0\grok-mcp.dll`. Don't single-file-publish — `dotnet <dll>` gives the cleanest stdio behavior.

## Wire into Claude Code (Windows)

Easiest — user-scoped install, every project gets it:

```powershell
claude mcp add grok --scope user `
  --env XAI_API_KEY=xai-... `
  -- "C:\Users\goosefx\SynologyDrive\PROJECTS\grok-mcp\run-grok-mcp.cmd"
```

Or copy `.mcp.json.example` to a project's `.mcp.json` and edit the API key. Verify with `/mcp` in a fresh Claude Code session — should show `grok` connected with 4 tools.

`run-grok-mcp.cmd` is a small wrapper that mirrors `bin\Release\net10.0\` into `%LOCALAPPDATA%\grok-mcp\runtime\` on every launch and runs from there. The build directory is never locked by a running MCP, so you can rebuild while Claude Code has the server loaded — see [Hot rebuild](#hot-rebuild-while-claude-code-is-running) below. On Linux/macOS the lock issue doesn't apply; invoke `dotnet path/to/grok-mcp.dll` directly.

## Hot rebuild while Claude Code is running

With the wrapper in place, the typical edit cycle is:

1. Edit code.
2. `dotnet build -c Release` — succeeds even if Claude Code has the old DLL loaded; the build only writes to `bin\`, which the running MCP doesn't hold.
3. To pick up the new code, kill the MCP child process and reconnect. The MCP shows up as a `dotnet.exe` whose command line points at `%LOCALAPPDATA%\grok-mcp\runtime\grok-mcp.dll`:

   ```powershell
   Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
     Where-Object { $_.CommandLine -match 'grok-mcp\\runtime\\grok-mcp\.dll' } |
     ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
   ```

4. In Claude Code, run `/mcp` and reconnect `grok` — the wrapper re-syncs from the fresh `bin\` and the new code is live. No Claude Code restart needed.

If you don't want to deal with finding the PID, restarting Claude Code also works — the next session refreshes the runtime copy from `bin\` automatically.

## Configuration (env vars)

| Var | Default | Purpose |
|---|---|---|
| `XAI_API_KEY` | — (required, fail fast) | Bearer token |
| `GROK_MCP_CHAT_MODEL` | `grok-3-mini` | default chat model |
| `GROK_MCP_CREATIVE_MODEL` | `grok-4-latest` | default vision/heavy model |
| `GROK_MCP_IMAGE_MODEL` | `grok-imagine-image` | default image gen/edit model |
| `GROK_MCP_LOG_LEVEL` | `Information` | `Trace`/`Debug`/`Information`/`Warning`/`Error` |
| `GROK_MCP_LOG_DIR` | `%LOCALAPPDATA%\grok-mcp\logs` | rolling-file log directory |
| `GROK_MCP_HTTP_TIMEOUT_SEC` | `300` | HttpClient timeout |
| `GROK_MCP_SESSION_CAP` | `50` | max turns retained per session |

Per-call tool params (`model`, `temperature`, `aspect_ratio`, etc.) override the env defaults. The API key is never logged.

## Logs

Two sinks:

- **stderr** — every level (the `LogToStandardErrorThreshold = Trace` setting). stdout is reserved for JSON-RPC; do not write to it.
- **rolling file** — `%LOCALAPPDATA%\grok-mcp\logs\grok-mcp-YYYY-MM-DD.log`.

Each Grok call logs the model, attempt, payload size, prompt preview (≤100 chars), and on success the `usage` token counts.

## Troubleshooting

- **`/mcp` shows red / disconnected.** Check the first lines of today's log file. Most common: missing `XAI_API_KEY`, or path to `grok-mcp.dll` is wrong (note backslash escaping in JSON).
- **`HTTP 400 model not found`.** xAI deprecates legacy aliases periodically. Set `GROK_MCP_CHAT_MODEL` / `GROK_MCP_CREATIVE_MODEL` / `GROK_MCP_IMAGE_MODEL` to the current model id from the xAI console and restart.
- **Slow first launch.** Windows Defender may scan `dotnet grok-mcp.dll` from `SynologyDrive` on first run. Subsequent launches are warm.
- **`output_path must be absolute`.** Claude Code's working directory is *not* the server's working directory. Always pass a full path.

## Project layout

```
grok-mcp\
├── GrokMcp.csproj
├── Program.cs                 (host bootstrap + DI + logging)
├── Config\GrokOptions.cs      (env-bound config)
├── Services\
│   ├── GrokClient.cs          (HTTP + retry; chat/images/vision)
│   ├── ChatSessionStore.cs    (in-memory session history)
│   ├── ImageInputResolver.cs  (URL/path/data-URI/base64 → data URI)
│   └── ImageWriter.cs         (resolve path, write bytes, return paths)
└── Tools\GrokTools.cs         (the 4 [McpServerTool] methods)
```

## Smoke tests

After build, in a fresh Claude Code session with the MCP wired up:

1. `/mcp` — `grok` connected, 4 tools listed.
2. Ask Claude: *"Use grok_chat to greet me in five languages."* Expect <2s round-trip; log shows token usage.
3. *"Use grok_chat with session_id='test1' to remember my name is Chloe. Then in a separate call with the same session_id ask what my name is."* Expect "Chloe".
4. *"Use grok_generate_image with prompt='a tiny pixel-art mushroom on transparent background' and output_path='C:\\Users\\you\\Desktop\\grok-test\\mushroom.png'."* Expect the file on disk + Claude can describe the inline image without re-reading it.
5. *"Use grok_edit_image with images=['…\\mushroom.png'], prompt='now make it a glowing crystal mushroom', output_path='…\\crystal.png'."* Expect a recognizably-derived new image.
6. *"Use grok_describe_image on `crystal.png` and tell me its color palette."* Expect coherent description.
