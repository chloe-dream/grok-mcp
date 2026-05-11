# Build grok-mcp single-file exe + Inno Setup installer.
#
# Output: bin\installer\GrokMcpSetup-<version>-win-x64.exe
#
# Override version: pwsh installer\build-installer.ps1 -AppVersion 1.0.1
param(
    [string]$AppVersion = "1.0.0"
)
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    Write-Host "==> dotnet publish" -ForegroundColor Cyan
    dotnet publish "GrokMcp.csproj" -c Release -o "bin\win-x64"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $isccPath = $null
    $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmd) { $isccPath = $cmd.Source }
    if (-not $isccPath) {
        $candidate = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        if (Test-Path $candidate) { $isccPath = $candidate }
    }
    if (-not $isccPath) {
        throw "Inno Setup not found. Install from https://jrsoftware.org/isdl.php (Inno Setup 6+)."
    }

    Write-Host "==> ISCC.exe (version $AppVersion)" -ForegroundColor Cyan
    & $isccPath "/DAppVersion=$AppVersion" "installer\grok-mcp.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed" }

    $outFile = "bin\installer\GrokMcpSetup-$AppVersion-win-x64.exe"
    Write-Host "==> Done. Installer at $outFile" -ForegroundColor Green
} finally {
    Pop-Location
}
