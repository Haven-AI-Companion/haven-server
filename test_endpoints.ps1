# Ash Server End-to-End API Integration Test Suite
# Runs a sandboxed instance of Ash Server to verify authentication, registration,
# profile changes, and admin toggling functions to prevent regression bugs before a release.

$ErrorActionPreference = "Continue" # Prevent parser/terminating script crashes

# Configuration
$TestPort = 18798
$BaseUrl = "http://localhost:$TestPort"
$BinaryDir = "C:\Users\admin\source\ash-server-cs\bin\Debug\net10.0\win-x64"
$BinaryPath = Join-Path $BinaryDir "ash-server.exe"
$ConfigPath = Join-Path $BinaryDir "config.json"
$ConfigBakPath = Join-Path $BinaryDir "config.json.bak"
$DbPath = Join-Path $PSScriptRoot "ash_server_test.db"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   Ash Server Pre-Release Integration Test Runner         " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Compile the latest changes
Write-Host "[1/7] Compiling backend code..." -ForegroundColor Gray
dotnet build -c Debug
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Compilation failed. Correct errors before running tests." -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Compilation successful." -ForegroundColor Green

# Ensure compiled binary exists
if (-not (Test-Path $BinaryPath)) {
    Write-Host "[ERROR] Compiled binary not found at $BinaryPath" -ForegroundColor Red
    exit 1
}

# 2. Setup Sandbox Configuration
Write-Host "[2/7] Sandboxing test environment..." -ForegroundColor Gray

# Backup existing config if present
if (Test-Path $ConfigPath) {
    Copy-Item $ConfigPath $ConfigBakPath -Force
    Write-Host "[OK] Backed up existing config.json" -ForegroundColor Gray
}

# Create test configuration
$TestConfig = @{
    "Port" = $TestPort
    "Host" = "127.0.0.1"
    "DatabasePath" = "ash_server_test.db"
    "RequireAuth" = $true
    "AllowRegistration" = $true
} | ConvertTo-Json

[System.IO.File]::WriteAllText($ConfigPath, $TestConfig)
Write-Host "[OK] Wrote sandboxed test config.json to $ConfigPath" -ForegroundColor Gray

# Clean up any stale test database
if (Test-Path $DbPath) {
    Remove-Item $DbPath -Force
    Write-Host "[OK] Cleaned stale test database." -ForegroundColor Gray
}

# 3. Spin up Server
Write-Host "[3/7] Bootstrapping background server..." -ForegroundColor Gray
$ServerProcess = Start-Process -FilePath $BinaryPath -NoNewWindow -PassThru

# Ensure server stops on script completion
$PIDToKill = $ServerProcess.Id
Write-Host "[OK] Spawned Server Process (PID: $PIDToKill) on port $TestPort" -ForegroundColor Green

# 4. Wait for Health Check
Write-Host "[4/7] Waiting for Kestrel binding..." -ForegroundColor Gray
$Healthy = $false
$Retries = 20
while (-not $Healthy -and $Retries -gt 0) {
    try {
        $HealthResponse = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get -TimeoutSec 1
        if ($HealthResponse.status -eq "ok") {
            $Healthy = $true
        }
    } catch {
        $Retries--
        Start-Sleep -Milliseconds 500
    }
}

if (-not $Healthy) {
    Write-Host "[ERROR] Server failed to bind to http://localhost:$TestPort after 10 seconds." -ForegroundColor Red
    Stop-Process -Id $PIDToKill -Force
    exit 1
}
Write-Host "[OK] Kestrel online and database auto-migrated successfully." -ForegroundColor Green

# 5. Run API Tests
Write-Host "[5/7] Running test query battery..." -ForegroundColor Gray
$TestsFailed = 0

# Helper function to execute WebRequests without throwing on HTTP status errors (PS 5.1 compatible)
function Invoke-WebRequestSafe {
    param (
        [string]$Uri,
        [string]$Method = "Get",
        [hashtable]$Headers = $null,
        [string]$ContentType = $null,
        [string]$Body = $null
    )
    $Result = @{ StatusCode = 0; Content = "" }
    try {
        $Params = @{
            Uri = $Uri
            Method = $Method
            UseBasicParsing = $true
        }
        if ($Headers) { $Params["Headers"] = $Headers }
        if ($ContentType) { $Params["ContentType"] = $ContentType }
        if ($Body) { $Params["Body"] = $Body }
        
        $Resp = Invoke-WebRequest @Params
        $Result.StatusCode = $Resp.StatusCode
        $Result.Content = $Resp.Content
    } catch {
        if ($_.Exception.Response) {
            $Result.StatusCode = [int]$_.Exception.Response.StatusCode
            # Read response stream body
            $Stream = $_.Exception.Response.GetResponseStream()
            if ($Stream) {
                $Reader = New-Object System.IO.StreamReader($Stream)
                $Result.Content = $Reader.ReadToEnd()
            }
        } else {
            $Result.StatusCode = 500
            $Result.Content = $_.Exception.Message
        }
    }
    return [PSCustomObject]$Result
}

function Assert-Status {
    param (
        [string]$Name,
        [int]$Expected,
        [int]$Actual,
        [string]$Content = $null
    )
    if ($Expected -eq $Actual) {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] $Name (Expected: $Expected, Actual: $Actual)" -ForegroundColor Red
        if ($Content) {
            Write-Host "    Response: $Content" -ForegroundColor Yellow
        }
        $script:TestsFailed++
    }
}

# Test 5.1: Admin Registration
Write-Host "" -ForegroundColor Yellow
Write-Host "Running Auth & Registration Tests..." -ForegroundColor Yellow
$RegBody = "username=testadmin&password=AdminPassword123!&email=admin@example.com"
$RegResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/register" -Method Post -ContentType "application/x-www-form-urlencoded" -Body $RegBody
Assert-Status "Register Admin User" 200 $RegResp.StatusCode $RegResp.Content

$RegJson = $RegResp.Content | ConvertFrom-Json
$AdminToken = $RegJson.access_token
$AdminId = $RegJson.user.id

# Test 5.2: Admin Login
$LoginBody = "username=testadmin&password=AdminPassword123!"
$LoginResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/login" -Method Post -ContentType "application/x-www-form-urlencoded" -Body $LoginBody
Assert-Status "Login Admin User" 200 $LoginResp.StatusCode

# Test 5.3: Profile Identity (GET /me)
$Headers = @{ "Authorization" = "Bearer $AdminToken" }
$MeResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/me" -Method Get -Headers $Headers
Assert-Status "Retrieve Me Profile (Auth JWT Validation)" 200 $MeResp.StatusCode

# Test 5.4: Profile Update Email (PATCH /me/email with JSON boundary)
$EmailBody = @{ "email" = "newadmin@example.com" } | ConvertTo-Json
$EmailResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/me/email" -Method Patch -Headers $Headers -ContentType "application/json" -Body $EmailBody
Assert-Status "Update Profile Email (JSON Media Binding Check)" 200 $EmailResp.StatusCode

# Verify Email actually saved
$MeResp2 = Invoke-RestMethod -Uri "$BaseUrl/api/auth/me" -Method Get -Headers $Headers
if ($MeResp2.user.email -eq "newadmin@example.com") {
    Write-Host "  [PASS] Profile Email Verified in Database" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] Profile Email Verified in Database (Got: $($MeResp2.user.email))" -ForegroundColor Red
    $TestsFailed++
}

# Test 5.5: Profile Update Password (PATCH /me/password with JSON boundary)
$PassBody = @{
    "current_password" = "AdminPassword123!"
    "new_password" = "NewAdminSecurePassword456!"
} | ConvertTo-Json
$PassResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/me/password" -Method Patch -Headers $Headers -ContentType "application/json" -Body $PassBody
Assert-Status "Update Profile Password (JSON Media Binding Check)" 200 $PassResp.StatusCode

# Verify Password changed
$LoginOldBody = "username=testadmin&password=AdminPassword123!"
$LoginOldResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/login" -Method Post -ContentType "application/x-www-form-urlencoded" -Body $LoginOldBody
if ($LoginOldResp.StatusCode -eq 401) {
    Write-Host "  [PASS] Verify Old Password Deprecation" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] Verify Old Password Deprecation (Expected: 401, Actual: $($LoginOldResp.StatusCode))" -ForegroundColor Red
    $TestsFailed++
}

$LoginNewBody = "username=testadmin&password=NewAdminSecurePassword456!"
$LoginNewResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/login" -Method Post -ContentType "application/x-www-form-urlencoded" -Body $LoginNewBody
Assert-Status "Re-auth with New Password (Should succeed)" 200 $LoginNewResp.StatusCode

# Update admin token with new valid session
$NewLoginJson = $LoginNewResp.Content | ConvertFrom-Json
$AdminToken = $NewLoginJson.access_token
$Headers["Authorization"] = "Bearer $AdminToken"

# Test 5.6: Register Second User (for admin toggling tests)
Write-Host "" -ForegroundColor Yellow
Write-Host "Running User & Admin Control Tests..." -ForegroundColor Yellow
$UserRegBody = "username=testuser&password=UserPassword123!&email=user@example.com"
$UserRegResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/register" -Method Post -ContentType "application/x-www-form-urlencoded" -Body $UserRegBody
Assert-Status "Register Second User" 200 $UserRegResp.StatusCode

$UserRegJson = $UserRegResp.Content | ConvertFrom-Json
$UserId = $UserRegJson.user.id

# Test 5.7: Toggle Admin status of testuser (requires admin JWT and JSON body)
$ToggleBody = @{ "is_admin" = $true } | ConvertTo-Json
$ToggleResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/admin/users/$UserId/toggle-admin" -Method Post -Headers $Headers -ContentType "application/json" -Body $ToggleBody
Assert-Status "Toggle Admin Status (JSON Media Binding Check)" 200 $ToggleResp.StatusCode

# Verify admin status toggled successfully in Database
$UsersResp = Invoke-RestMethod -Uri "$BaseUrl/api/admin/users" -Method Get -Headers $Headers
$TargetUser = $UsersResp.users | Where-Object { $_.id -eq $UserId }
if ($TargetUser.is_admin -eq $true) {
    Write-Host "  [PASS] Verify Admin Role Assignment in Database" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] Verify Admin Role Assignment in Database (is_admin: $($TargetUser.is_admin))" -ForegroundColor Red
    $TestsFailed++
}

# Test 5.8: Initiate Mobile Pairing
Write-Host "" -ForegroundColor Yellow
Write-Host "Running Mobile Pairing Tests..." -ForegroundColor Yellow

$PairInitResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/mobile/pair/initiate" -Method Post -Headers $Headers
Assert-Status "Initiate Mobile Pairing (Authorized)" 200 $PairInitResp.StatusCode

$PairInitJson = $PairInitResp.Content | ConvertFrom-Json
$PairingCode = $PairInitJson.code
Write-Host "  Generated Pairing Code: $PairingCode" -ForegroundColor Gray

# Test 5.9: Confirm Mobile Pairing
$ConfirmBody = @{ "code" = $PairingCode; "device_name" = "Test Device" } | ConvertTo-Json
$ConfirmResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/mobile/pair/confirm" -Method Post -ContentType "application/json" -Body $ConfirmBody
Assert-Status "Confirm Mobile Pairing (Public)" 200 $ConfirmResp.StatusCode

$ConfirmJson = $ConfirmResp.Content | ConvertFrom-Json
$LongLivedToken = $ConfirmJson.token

# Test 5.10: Re-confirming same code fails
$ConfirmResp2 = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/mobile/pair/confirm" -Method Post -ContentType "application/json" -Body $ConfirmBody
Assert-Status "Confirm Pairing Again Fails (Consumed Code)" 400 $ConfirmResp2.StatusCode

# Test 5.11: Use long-lived mobile token to access API
$MobileHeaders = @{ "Authorization" = "Bearer $LongLivedToken" }
$MobileMeResp = Invoke-WebRequestSafe -Uri "$BaseUrl/api/auth/me" -Method Get -Headers $MobileHeaders
Assert-Status "Access API using paired long-lived Mobile JWT" 200 $MobileMeResp.StatusCode

# 6. Tear down server
Write-Host "" -ForegroundColor Gray
Write-Host "[6/7] Tearing down background server..." -ForegroundColor Gray
Stop-Process -Id $PIDToKill -Force
Write-Host "[OK] Safely terminated test server process (PID: $PIDToKill)." -ForegroundColor Green

# 7. Cleanup Sandbox Configuration
Write-Host "[7/7] Cleaning sandbox artifacts..." -ForegroundColor Gray

# Restore original config.json
if (Test-Path $ConfigBakPath) {
    Move-Item $ConfigBakPath $ConfigPath -Force
    Write-Host "[OK] Restored original config.json" -ForegroundColor Green
} else {
    if (Test-Path $ConfigPath) {
        Remove-Item $ConfigPath -Force
        Write-Host "[OK] Removed test config.json" -ForegroundColor Green
    }
}

# Remove temporary test database files
Get-ChildItem -Path $PSScriptRoot -Filter "ash_server_test.db*" | Remove-Item -Force
Write-Host "[OK] Cleaned test database files." -ForegroundColor Green

# Print Final Summary Report
Write-Host "" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
if ($TestsFailed -eq 0) {
    Write-Host "   ALL TESTS PASSED! Ready for clean release." -ForegroundColor Green
} else {
    Write-Host "   TESTS FAILED ($TestsFailed error(s) detected). Retest required." -ForegroundColor Red
    exit 1
}
Write-Host "==========================================================" -ForegroundColor Cyan
