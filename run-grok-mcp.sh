#!/usr/bin/env bash
# ----------------------------------------------------------------------------
#  grok-mcp shadow-copy launcher (Linux / macOS)
#
#  Linux and macOS allow overwriting an executable file even while a process
#  has it open, so the build doesn't actually need this on those platforms.
#  The wrapper exists for cross-platform parity with run-grok-mcp.cmd: same
#  registration command shape, same runtime location semantics.
# ----------------------------------------------------------------------------
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$DIR/bin/Release/net10.0"
DST="${XDG_DATA_HOME:-$HOME/.local/share}/grok-mcp/runtime"

if [ ! -f "$SRC/grok-mcp.dll" ]; then
    echo "grok-mcp: build artifacts not found at $SRC." >&2
    echo "Run 'dotnet build -c Release' in the project directory first." >&2
    exit 1
fi

mkdir -p "$DST"
cp -R "$SRC/." "$DST/"

# Tell the MCP where to watch for new builds so it can self-shutdown on rebuild.
export GROK_MCP_BUILD_DIR="$SRC"

# Replace this shell with dotnet so stdio pipes and signals go straight to
# the .NET host — no intermediate shell process to manage.
exec dotnet "$DST/grok-mcp.dll" "$@"
