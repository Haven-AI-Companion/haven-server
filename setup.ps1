# Haven Server Unified Windows Installer Script
# Must be run as Administrator

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "      HAVEN SERVER WINDOWS INSTALLER     " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

# Check Admin Privileges
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This installer must be run as Administrator! Please right-click PowerShell and choose 'Run as Administrator'."
    Exit
}

# 1. Select Installation Folder
$defaultPath = "C:\Program Files\HavenServer"
$installPath = Read-Host "Select installation folder [Default: $defaultPath]"
if ([string]::IsNullOrWhiteSpace($installPath)) {
    $installPath = $defaultPath
}

Write-Host "Installing Haven Server to: $installPath" -ForegroundColor Green
New-Item -ItemType Directory -Force -Path $installPath | Out-Null

# 2. Copy files to target folder
Write-Host "Copying server files..."
Copy-Item -Path "bin\Debug\net10.0\win-x64\*", "wwwroot", "appsettings.json" -Destination $installPath -Recurse -Force -ErrorAction SilentlyContinue

# 3. Ask if Stable Diffusion is required
$installSD = Read-Host "Do you want to install Stable Diffusion local image generator (sd-server.exe) alongside Haven Server? (Y/N) [Default: N]"
$sdInstalled = $false
if ($installSD -eq 'Y' -or $installSD -eq 'y') {
    Write-Host "Configuring Stable Diffusion (sd-server)..." -ForegroundColor Cyan
    $sdPath = Join-Path $installPath "sd-server"
    New-Item -ItemType Directory -Force -Path $sdPath | Out-Null

    # Check if sd-server exists locally, copy if present
    if (Test-Path "..\sd-server\sd-server.exe") {
        Write-Host "Found local sd-server.exe. Copying..."
        Copy-Item -Path "..\sd-server\*" -Destination $sdPath -Recurse -Force
        $sdInstalled = $true
    } elseif (Test-Path "sd-server.exe") {
        Write-Host "Found local sd-server.exe. Copying..."
        Copy-Item -Path "sd-server.exe" -Destination $sdPath -Force
        $sdInstalled = $true
    } else {
        Write-Host "Local sd-server.exe not found." -ForegroundColor Yellow
        Write-Host "Please place your sd-server.exe and models inside: $sdPath" -ForegroundColor Yellow
    }
}

# 4. Generate startup batch script
$startScriptContent = @"
@echo off
title Haven AI Server Stack
echo Starting Haven Server...
start "" "%installPath%\haven-server.exe"
"@
if ($sdInstalled) {
    $startScriptContent += "`necho Starting Stable Diffusion Server...`nstart \"\" \"$installPath\sd-server\sd-server.exe\""
}
$startScriptContent += "`necho Done. Haven Server is listening on http://localhost:18799"
$startScriptContent += "`npause"

$startScriptContent | Out-File -FilePath (Join-Path $installPath "start_servers.bat") -Encoding ASCII -Force
Write-Host "Created startup script at: $(Join-Path $installPath 'start_servers.bat')" -ForegroundColor Green

# 5. Ask to register as Windows Service
$installService = Read-Host "Do you want to run Haven Server as a Windows background Service? (Y/N) [Default: Y]"
if ($installService -ne 'N' -and $installService -ne 'n') {
    Write-Host "Registering Haven Server Windows Service..."
    Set-Location $installPath
    # Call the self-installer
    & ".\haven-server.exe" "install"
}

Write-Host ""
Write-Host "Haven Server installation complete!" -ForegroundColor Green
Write-Host "Double-click $(Join-Path $installPath 'start_servers.bat') to run the system manually," -ForegroundColor Green
Write-Host "or start the background service with: Start-Service haven-server" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Cyan
