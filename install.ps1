#Requires -Version 5.1
# =============================================================================
# Ash Server — Install from source (Windows)
# =============================================================================
# One-liner install (run PowerShell as Administrator):
#   git clone https://github.com/ssfdre38/ash-server; cd ash-server; .\install.ps1
#
# Or to just run without installing as a service:
#   .\install.ps1 -Run
# =============================================================================
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$RepoDir  = $PSScriptRoot
$OutDir   = Join-Path $RepoDir "build\dist\win-x64"
$Binary   = Join-Path $OutDir "ash-server.exe"

function Info($msg)  { Write-Host "[ash-server] $msg" -ForegroundColor Cyan }
function Ok($msg)    { Write-Host "[ash-server] $msg" -ForegroundColor Green }
function Warn($msg)  { Write-Host "[ash-server] $msg" -ForegroundColor Yellow }
function Die($msg)   { Write-Error "[error] $msg"; exit 1 }

# ── Check for .NET SDK ─────────────────────────────────────────────────────────
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Warn ".NET SDK not found. Install it from:"
    Warn "  https://dotnet.microsoft.com/download"
    Warn "  winget install Microsoft.DotNet.SDK.10"
    Die "Please install .NET 10 SDK and re-run this script."
}
$dotnetVer = dotnet --version 2>$null
Info ".NET SDK version: $dotnetVer"

# ── Uninstall ──────────────────────────────────────────────────────────────────
if ($Uninstall) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Die "Run as Administrator for uninstall: Right-click PowerShell → Run as Administrator"
    }
    if (Test-Path $Binary) {
        & $Binary uninstall-service
    }
    Ok "Ash Server service removed."
    exit 0
}

# ── Build ──────────────────────────────────────────────────────────────────────
Info "Building self-contained binary for win-x64…"
dotnet publish "$RepoDir\ash-server-cs.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutDir

if ($LASTEXITCODE -ne 0) { Die "Build failed." }
Ok "Build complete: $Binary"

# ── --Run mode: start in the foreground ───────────────────────────────────────
if ($Run) {
    Info "Starting ash-server in the foreground (Ctrl+C to stop)…"
    Push-Location $OutDir
    & $Binary
    Pop-Location
    exit 0
}

# ── Install as Windows Service ─────────────────────────────────────────────────
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Die "Run as Administrator to install the service.`n  Or use: .\install.ps1 -Run  to start without installing."
}

Info "Installing Windows Service…"
Push-Location $OutDir
& $Binary install-service
Pop-Location

Info "Starting service…"
Start-Service -Name "ash-server" -ErrorAction SilentlyContinue

Ok "Ash Server installed and running."
Write-Host ""
Write-Host "  Web UI:   http://localhost:18799"
Write-Host "  Admin:    http://localhost:18799/admin.html"
Write-Host "  Logs:     Get-EventLog -LogName Application -Source ash-server -Newest 50"
Write-Host "  Stop:     Stop-Service ash-server"
Write-Host "  Remove:   .\install.ps1 -Uninstall  (as Administrator)"
Write-Host ""
