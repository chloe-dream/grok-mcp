# Changelog

All notable changes to grok-mcp are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-05-10

First public release. This rebuild fixes the chronic "MCP failed" reconnect
storms that the prior stdio prototype hit whenever more than one Claude Code
session was open at the same time.

### Changed
- **Transport**: stdio replaced by HTTP. Bind defaults to
  `http://127.0.0.1:6677/mcp`, loopback only. One server process now serves
  many parallel Claude Code clients. `ChatSessionStore` is consequently shared
  across all clients — a `session_id` set from one window is visible in
  another.
- **Distribution**: stops shipping launcher scripts inside the project
  directory. New Inno Setup installer puts a single-file
  `grok-mcp.exe` (~2.3 MB, framework-dependent on .NET 10 Desktop Runtime)
  into `%LOCALAPPDATA%\Programs\grok-mcp\` and registers a per-user Scheduled
  Task `grok-mcp-server` that runs at logon and auto-restarts on crash. No
  UAC.
- **Config**: `XAI_API_KEY` and friends now live in
  `%LOCALAPPDATA%\grok-mcp\config.env` (or `%USERPROFILE%\.grok-mcp\config.env`
  as fallback). Real process env vars still override both. Removes the need
  to inline the key in every `.mcp.json` or `claude mcp add` command.

### Added
- **Wizard-driven setup**: installer prompts for the API key, pre-fills it on
  re-install so you can review or change it, validates that it looks like
  `xai-…`, and writes/updates the key line in place — preserving any
  customised model overrides in `config.env`.
- **Auto-registration with Claude Code**: installer runs
  `claude mcp add grok --scope user --transport http http://127.0.0.1:6677/mcp`
  at the end (idempotent: prior registration is removed first). Final wizard
  page reports what was done; if the `claude` CLI isn't on PATH, falls back
  to a printed manual command.
- **`GROK_MCP_PORT`** env var to change the listen port from the default 6677.
- **`/health` endpoint**: returns `status`, `pid`, `version`, `port`,
  `apiKeyConfigured` (bool — never the key itself), and `uptimeSeconds`.
  Useful for quick probes and watchdog scripts.
- **`GROK_MCP_LOG_RETENTION_DAYS`** (default 30): on startup, log files
  older than this many days are deleted from the log directory. Failures
  while deleting locked files never block startup.
- **`scripts/dev-update.ps1`**: developer hot-reload — stops the Scheduled
  Task, waits for the file lock to drop, `dotnet publish`-es straight into
  the installed location, restarts the task. Replaces the old
  `BuildWatcher` + shadow-copy launcher hack.
- **`.github/workflows/release.yml`**: GitHub Actions release pipeline. On a
  `v*` tag, builds the installer on `windows-latest` and attaches it to a
  GitHub Release.
- **`CLAUDE.md`** with project-specific guidance for Claude Code instances
  working on this repo.

### Removed
- `run-grok-mcp.cmd` and `run-grok-mcp.sh` (the stdio shadow-copy launchers).
- `Services/BuildWatcher.cs` (file-system watcher that triggered self-shutdown
  on rebuild) — the Scheduled Task handles process lifecycle now.
- Need for a per-project `.mcp.json` in every repository. The user-scope HTTP
  registration covers all projects at once. `.mcp.json.example` is kept as a
  template for users who still prefer per-project pinning.

### Fixed
- **Reconnect storms across multi-project use.** Root cause was `xcopy`-locking
  on the shared `%LOCALAPPDATA%\grok-mcp\runtime\` directory whenever two
  Claude Code instances launched at the same time. With one process for all
  clients, this race is gone.
- **`Tools/` vs `tools/` collision on Windows.** Dev helpers moved to
  `scripts/` so they don't share a (case-insensitive) directory with the
  `[McpServerTool]` C# namespace.
