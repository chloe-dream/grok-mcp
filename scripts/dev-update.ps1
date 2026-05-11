# Dev hot-reload: rebuild grok-mcp and redeploy into the installed location.
#
# Workflow:
#   1) Stop the running Scheduled Task (releases the file lock on grok-mcp.exe).
#   2) Wait for the process to actually exit.
#   3) `dotnet publish` straight into %LOCALAPPDATA%\Programs\grok-mcp\.
#   4) Start the Scheduled Task again — Claude Code clients auto-reconnect.
#
# Run from any working directory.
$ErrorActionPreference = "Stop"

$repo       = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA "Programs\grok-mcp"
$exePath    = Join-Path $installDir "grok-mcp.exe"
$taskName   = "grok-mcp-server"

if (-not (Test-Path $exePath)) {
    throw "grok-mcp is not installed at $installDir. Run the installer first (bin\installer\GrokMcpSetup-*.exe)."
}

$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) {
    Write-Host "==> Stopping Scheduled Task '$taskName'" -ForegroundColor Cyan
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
} else {
    Write-Host "Scheduled Task '$taskName' is not registered — continuing without service-stop."
}

# Wait until any lingering grok-mcp.exe actually releases the file lock.
Get-Process grok-mcp -ErrorAction SilentlyContinue |
    Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
if (Get-Process grok-mcp -ErrorAction SilentlyContinue) {
    Write-Warning "grok-mcp.exe still running after 15s - forcing stop."
    Get-Process grok-mcp | Stop-Process -Force
    Start-Sleep -Seconds 1
}

Write-Host "==> dotnet publish → $installDir" -ForegroundColor Cyan
& dotnet publish (Join-Path $repo "GrokMcp.csproj") -c Release -o $installDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

if ($task) {
    Write-Host "==> Starting Scheduled Task '$taskName'" -ForegroundColor Cyan
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 2
    try {
        $health = (Invoke-WebRequest -Uri "http://127.0.0.1:6677/health" -UseBasicParsing -TimeoutSec 5).Content
        Write-Host "==> Service healthy: $health" -ForegroundColor Green
    } catch {
        Write-Warning "Service started but /health probe failed: $($_.Exception.Message)"
    }
} else {
    Write-Host "==> Done. Start grok-mcp manually with: & `"$exePath`"" -ForegroundColor Yellow
}
