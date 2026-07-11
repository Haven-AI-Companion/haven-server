# Haven AI Stack Daemon Health Watcher Script
# Automatically monitors and restarts haven-server, Ollama, and sd-server.
# Run this script in a PowerShell window to keep your local stack active.

$ErrorActionPreference = "SilentlyContinue"

# Configurations
$CheckIntervalSeconds = 30
$ServerPort = 18799
$OllamaPort = 11434

# Executable Paths (Supports both Dev and Production environments)
$OllamaPath = "C:\Users\admin\AppData\Local\Programs\Ollama\ollama.exe"

$DevServerPath = "C:\Users\admin\haven-server\bin\Debug\net10.0\win-x64\haven-server.exe"
$ProdServerPath = "C:\Program Files\HavenServer\haven-server.exe"
$ServerPath = if (Test-Path $ProdServerPath) { $ProdServerPath } else { $DevServerPath }
$ServerDir = Split-Path $ServerPath

$DevSDPath = "C:\Users\admin\haven-server\sd-server\sd-server.exe"
$ProdSDPath = "C:\Program Files\HavenServer\sd-server\sd-server.exe"
$SDPath = if (Test-Path $ProdSDPath) { $ProdSDPath } else { $DevSDPath }

# Log file path
$LogFile = Join-Path $ServerDir "watch_stack.log"

function Log-Message {
    param([string]$Message, [string]$Type = "INFO")
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logLine = "[$timestamp] [$Type] $Message"
    Write-Host $logLine
    $logLine | Out-File -FilePath $LogFile -Append -Encoding utf8
}

Log-Message "===================================================="
Log-Message "Haven Stack Watcher Daemon Started"
Log-Message "Check Interval: $CheckIntervalSeconds seconds"
Log-Message "Haven Server Path: $ServerPath"
Log-Message "Ollama Executable: $OllamaPath"
if (Test-Path $SDPath) {
    Log-Message "Stable Diffusion Path: $SDPath"
}
Log-Message "===================================================="

while ($true) {
    # 1. Verify Ollama Health
    $ollamaHealthy = $false
    try {
        $ollamaResponse = Invoke-WebRequest -Uri "http://localhost:$OllamaPort/api/tags" -Method Get -TimeoutSec 5 -UseBasicParsing
        if ($ollamaResponse.StatusCode -eq 200) {
            $ollamaHealthy = $true
        }
    } catch {
        # Response failed
    }

    if (-not $ollamaHealthy) {
        Log-Message "Ollama is down or unresponsive! Attempting restart..." "WARN"
        # Check if process is running but hung, kill it
        $ollamaProc = Get-Process "ollama"
        if ($ollamaProc) {
            Log-Message "Killing hung Ollama process (ID: $($ollamaProc.Id))..." "WARN"
            Stop-Process -Name "ollama" -Force
            Start-Sleep -Seconds 2
        }
        # Start Ollama
        if (Test-Path $OllamaPath) {
            Start-Process -FilePath $OllamaPath -WorkingDirectory (Split-Path $OllamaPath) -WindowStyle Hidden
            Log-Message "Ollama started in background."
        } else {
            Log-Message "Ollama executable not found at $OllamaPath!" "ERROR"
        }
    }

    # 2. Verify Haven Server Health
    $serverHealthy = $false
    try {
        $serverResponse = Invoke-WebRequest -Uri "http://localhost:$ServerPort/api/companions" -Method Get -TimeoutSec 5 -UseBasicParsing
        # Any response (even 401 Unauthorized because we lack JWT token) means the web server is alive!
        if ($serverResponse -or $true) {
            $serverHealthy = $true
        }
    } catch {
        # If we get an HTTP error (like 401) the server is active.
        # ConnectionRefused (ActionPreference) triggers the catch block.
        if ($_.Exception.InnerException -and $_.Exception.InnerException.Message -match "connection refused") {
            $serverHealthy = $false
        } else {
            # Other errors (e.g. 401 Unauthorized) mean the port is listening
            $serverHealthy = $true
        }
    }

    if (-not $serverHealthy) {
        Log-Message "Haven Server (port $ServerPort) is unresponsive! Attempting restart..." "WARN"
        $serverProc = Get-Process "haven-server"
        if ($serverProc) {
            Log-Message "Killing hung Haven Server process (ID: $($serverProc.Id))..." "WARN"
            Stop-Process -Name "haven-server" -Force
            Start-Sleep -Seconds 2
        }
        if (Test-Path $ServerPath) {
            Start-Process -FilePath $ServerPath -WorkingDirectory $ServerDir -WindowStyle Minimized
            Log-Message "Haven Server process launched."
        } else {
            Log-Message "Haven Server binary not found at $ServerPath!" "ERROR"
        }
    }

    # 3. Verify Stable Diffusion Server Health (if configured)
    if (Test-Path $SDPath) {
        $sdProc = Get-Process "sd-server"
        if (-not $sdProc) {
            Log-Message "Stable Diffusion Server (sd-server) is not running! Launching..." "WARN"
            Start-Process -FilePath $SDPath -WorkingDirectory (Split-Path $SDPath) -WindowStyle Minimized
            Log-Message "Stable Diffusion Server process launched."
        }
    }

    Start-Sleep -Seconds $CheckIntervalSeconds
}
