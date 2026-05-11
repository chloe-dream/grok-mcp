# Remove the grok-mcp-server Scheduled Task and stop any running instance.
# Called from the Inno Setup [UninstallRun] section.
$ErrorActionPreference = "Continue"

Stop-ScheduledTask  -TaskName "grok-mcp-server" -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName "grok-mcp-server" -Confirm:$false -ErrorAction SilentlyContinue
Get-Process grok-mcp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Scheduled Task removed (if it existed) and grok-mcp processes stopped."
