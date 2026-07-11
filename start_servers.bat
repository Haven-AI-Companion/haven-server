@echo off
title Haven Services Control Panel
color 0B

echo ==========================================================
echo           Haven Services Startup Control Panel
echo ==========================================================
echo.

:: 1. Start Llama Server in a new window (bypassing sidecar, listening on 0.0.0.0)
echo [1/3] Starting Llama Server (Inference)...
start "Llama Server" powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\admin\source\ash-bot-cs\start-ash-server.ps1" -BindHost 0.0.0.0
echo [OK] Llama Server launched in a new window.
echo.

:: 2. Start Stable Diffusion Server in a new window (listening on 0.0.0.0)
echo [2/3] Starting Stable Diffusion Server...
cd /d "C:\Users\admin\stable-diffusion-cpp"
if not exist "sd-server.exe" (
    echo [ERROR] sd-server.exe not found in C:\Users\admin\stable-diffusion-cpp
    goto error
)
start "Stable Diffusion Server" sd-server.exe --model "C:\Users\admin\stable-diffusion-cpp\models\DreamShaper8_LCM_q8_0.gguf" --taesd "C:\Users\admin\stable-diffusion-cpp\models\taesd.safetensors" --listen-ip 0.0.0.0 --listen-port 8080 --steps 4 --sampling-method lcm --cfg-scale 1.5
echo [OK] SD Server launched in a new window.
echo.

:: 3. Run Haven Server in the foreground
echo [3/3] Starting Haven Server (C#)...
cd /d "%~dp0"
if not exist "bin\Debug\net10.0\win-x64\haven-server.exe" (
    echo [ERROR] haven-server.exe not found in bin\Debug\net10.0\win-x64
    goto error
)
echo.
echo ==========================================================
echo  All services have been launched!
echo  - Llama Server (API):      http://0.0.0.0:11436
echo  - Stable Diffusion (SD):   http://0.0.0.0:8080
echo  - Haven Server (C# Web):   http://0.0.0.0:18799
echo ==========================================================
echo.
bin\Debug\net10.0\win-x64\haven-server.exe
exit

:error
echo.
echo [ERROR] One or more services failed to start. Check paths or rebuild.
pause
exit
