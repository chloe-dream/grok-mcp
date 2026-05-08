@echo off
rem ---------------------------------------------------------------------------
rem  grok-mcp shadow-copy launcher
rem
rem  Copies the build output into a stable runtime directory and runs from
rem  there. The build directory (bin\Release\net10.0) is therefore never
rem  locked by a running MCP process, so `dotnet build -c Release` works
rem  even while Claude Code has the server loaded.
rem
rem  Use this script as the MCP command in .mcp.json or `claude mcp add`
rem  instead of invoking the .dll directly.
rem ---------------------------------------------------------------------------
setlocal

set "SRC=%~dp0bin\Release\net10.0"
set "DST=%LOCALAPPDATA%\grok-mcp\runtime"

if not exist "%SRC%\grok-mcp.dll" (
    echo grok-mcp: build artifacts not found at "%SRC%". 1>&2
    echo Run "dotnet build -c Release" in the project directory first. 1>&2
    exit /b 1
)

if not exist "%DST%" mkdir "%DST%"

rem Mirror the build output into the runtime location. /Y overwrite, /Q quiet,
rem /E include subdirs (runtimes\, etc.), /I treat dest as directory.
xcopy /Y /Q /E /I "%SRC%\*" "%DST%\" >nul
if errorlevel 1 (
    echo grok-mcp: failed to refresh runtime copy at "%DST%". 1>&2
    echo Is another grok-mcp instance still running? 1>&2
    exit /b 2
)

rem Replace this process with dotnet so Claude Code's stdio pipes go straight
rem to the .NET host. /b avoids opening a new console window.
dotnet "%DST%\grok-mcp.dll" %*
exit /b %errorlevel%
