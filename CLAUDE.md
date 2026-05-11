# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A local **HTTP** MCP server (.NET 10, C#, ASP.NET Core) that wraps the xAI Grok REST API. Exposes four tools to Claude Code: `grok_chat`, `grok_generate_image`, `grok_edit_image`, `grok_describe_image`. One background process binds to `http://127.0.0.1:6677` and serves many parallel Claude Code clients — loopback only, no auth.

## Build & install

```powershell
powershell.exe -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Pipeline: `dotnet publish -c Release -o bin\win-x64` (single-file `grok-mcp.exe`, ~2.3 MB, framework-dependent on .NET 10 Desktop Runtime) → ISCC compiles `installer\grok-mcp.iss` → installer at `bin\installer\GrokMcpSetup-<version>-win-x64.exe`.

The installer is end-to-end one-shot: a custom wizard page asks for `XAI_API_KEY` (skipped if `config.env` already has one), writes `%LOCALAPPDATA%\grok-mcp\config.env`, drops `grok-mcp.exe` into `%LOCALAPPDATA%\Programs\grok-mcp\`, registers the `grok-mcp-server` Scheduled Task (on-logon, auto-restart on crash), bounces the service so the new key is loaded, and registers the MCP with Claude Code via `claude mcp add grok --scope user --transport http ...` (silently no-ops if the CLI isn't on PATH). Plain user-scope install (`PrivilegesRequired=lowest`, no UAC). The final wizard page reports what was done.

There is no test project, no linter, no CI yet. Verification is manual via the smoke tests in `README.md`.

## Hot reload during development

```powershell
powershell.exe -ExecutionPolicy Bypass -File scripts\dev-update.ps1
```

The script stops the Scheduled Task, waits up to 15 s for `grok-mcp.exe` to release the file lock, `dotnet publish`-es straight into `%LOCALAPPDATA%\Programs\grok-mcp\`, then starts the task again. Claude Code clients reconnect on their own. **Do not** use plain `dotnet build -c Release` for hot reload — that writes to `bin\` and the installed copy is untouched.

There is intentionally no `BuildWatcher` or shadow-copy launcher. The Scheduled Task is the only restart mechanism.

## Architecture

- **`Program.cs`** — `WebApplication` host. Binds `GrokOptions` from `config.env` + env vars *before* building the DI container (logging level needs it). Fail-fast on missing `XAI_API_KEY` (exit 2). Registers MCP server with `WithHttpTransport(o => o.Stateless = true).WithToolsFromAssembly()`. Maps `/mcp` (the streamable-HTTP MCP endpoint) and `/health` (JSON probe with `pid`, `version`). Always binds to `http://127.0.0.1:6677` — loopback only.

- **`Tools\GrokTools.cs`** — The four MCP tool methods. Thin wrappers that validate arguments, resolve image inputs, call `GrokClient`, and package results. Image tools return `CallToolResult` with both a `TextContentBlock` (saved-path summary) and an `ImageContentBlock` per saved image so the calling agent sees the bytes inline without re-reading the file.

- **`Services\GrokClient.cs`** — HTTP layer to xAI. Three retry attempts at delays `[0, 2s, 6s]`. Treats HTTP 5xx and 429 as transient; 4xx breaks out of the retry loop. Generation hits `/images/generations`, edits hit `/images/edits` (presence of `inputs` decides). Vision uses `/chat/completions` with multimodal `content` arrays. Token usage logged on every chat response.

- **`Services\ImageInputResolver.cs`** — Normalizes four input shapes (`http(s)://`, `data:`, absolute file path, raw base64) into `{ url = "data:...;base64,..." }` objects for xAI's `image_url` content blocks.

- **`Services\ImageWriter.cs`** — Writes bytes to `output_path`. Enforces absolute paths (the MCP's CWD is not the agent's CWD — this is a real bug source). Sniffs PNG/JPG/GIF/WebP magic bytes and **appends** the true extension if the user-typed name doesn't already end in it. For `n>1`, indexes go before the extension.

- **`Services\ChatSessionStore.cs`** — In-memory only. `ConcurrentDictionary<string, List<ChatTurn>>` keyed by `session_id`. With HTTP transport the server is a singleton process, so a `session_id` used by Claude Code session A is visible to Claude Code session B. Lifetime = server process. FIFO trim at `GROK_MCP_SESSION_CAP` turns.

- **`Config\GrokOptions.cs`** — Resolution order at startup: `%LOCALAPPDATA%\grok-mcp\config.env` → `%USERPROFILE%\.grok-mcp\config.env` → real process env vars (each step only sets keys not already present, so later steps win). Per-call tool params override these.

- **`installer\grok-mcp.iss`** — Inno Setup script. Style copied from `firepit-ai\installer\firepit.iss`: `{localappdata}\Programs\<Name>`, `PrivilegesRequired=lowest`, `Compression=lzma2/ultra`, `ArchitecturesAllowed=x64` (NOTE: `x64compatible` requires Inno Setup 6.3+; we target 6.2.1+).

- **`installer\install-task.ps1` / `uninstall-task.ps1`** — Called from `[Run]` / `[UninstallRun]`. Register/remove the `grok-mcp-server` Scheduled Task with `RestartCount=999, RestartInterval=1m`, run under interactive user, Limited runlevel (no elevation).

- **`installer\seed-config.ps1`** — Writes a `config.env` template to `%LOCALAPPDATA%\grok-mcp\config.env` only if the file doesn't exist (idempotent across re-installs).

## Conventions

- All Grok HTTP calls go through `GrokClient.PostWithRetryAsync`. New endpoints should use it so retry + logging stay uniform.
- New MCP tools: add a `[McpServerTool]` method to `GrokTools` (or another `[McpServerToolType]` class — auto-discovered via `WithToolsFromAssembly()`). Descriptions in `[Description]` attributes are what the agent sees; be precise about required vs optional and absolute-path requirements.
- Never log the API key. Current code only logs model name, attempt number, payload size, and a 100-char prompt preview.
- Image-output tools must accept `output_path` as **absolute** and reject relative paths with the existing error message.
- The script directory is `scripts\`, **not** `tools\` — `Tools\` already holds the C# `[McpServerTool]` namespace, and Windows is case-insensitive.
- `GrokMcp.csproj` is `<OutputType>WinExe</OutputType>` so the Scheduled Task launches grok-mcp without a visible console window. Consequence: `Console.Error`/`Console.Out` is detached when stderr/stdout aren't redirected, so anything you want a user to see when manually running the exe must also be written to the file logger or to a known path on disk. The startup `FATAL: XAI_API_KEY missing` branch in `Program.cs` already does this — follow the same pattern for new early-fail paths.
- `.ps1` files in this repo: keep ASCII-only or save with **UTF-8 BOM**. `powershell.exe` (PS 5.1) is the install/dev shell and parses BOM-less files as Windows-1252, which mangles em-dashes/arrows/umlauts into garbage and produces confusing `TerminatorExpectedAtEndOfString` parse errors.

## Wiring into Claude Code

The installer does this automatically (user-scope). Manual fallback if `claude` isn't on PATH at install time:

```powershell
claude mcp add grok --scope user --transport http http://127.0.0.1:6677/mcp
```

One registration, all projects pick it up. No per-project `.mcp.json` required. `.mcp.json.example` is kept for users who prefer per-project pinning.
