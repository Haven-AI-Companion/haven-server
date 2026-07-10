@echo off
title Haven Services Control Panel
color 0B

echo ==========================================================
echo           Haven Services Startup Control Panel
echo ==========================================================
echo.

:: 1. Start Stable Diffusion Server
echo [1/2] Starting Stable Diffusion Server...
cd /d "C:\Users\admin\stable-diffusion-cpp"
if not exist "sd-server.exe" (
    echo [ERROR] sd-server.exe not found in C:\Users\admin\stable-diffusion-cpp
    goto error
)
start "Stable Diffusion Server" sd-server.exe --model "C:\Users\admin\stable-diffusion-cpp\models\DreamShaper8_LCM_q8_0.gguf" --taesd "C:\Users\admin\stable-diffusion-cpp\models\taesd.safetensors" --listen-ip 0.0.0.0 --listen-port 8080 --steps 4 --sampling-method lcm --cfg-scale 1.5
echo [OK] SD Server launched in a new window.
echo.

:: 2. Start Haven Server
echo [2/2] Starting Haven Server...
cd /d "C:\Users\admin\ash-server"
if not exist "bin\Debug\net10.0\win-x64\haven-server.exe" (
    echo [ERROR] haven-server.exe not found in C:\Users\admin\ash-server\bin\Debug\net10.0\win-x64
    goto error
)
start "Haven Server" "bin\Debug\net10.0\win-x64\haven-server.exe"
echo [OK] Haven Server launched in a new window.
echo.

echo ==========================================================
echo  Both services have been launched!
echo  - Stable Diffusion: http://localhost:8080
echo  - Ash Server:       http://localhost:18799
echo ==========================================================
echo.
pause
exit

:error
echo.
echo [ERROR] One or more services failed to start. Check paths.
pause
exit
