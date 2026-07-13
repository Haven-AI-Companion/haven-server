@echo off
title Haven Services Control Panel
color 0B

echo ==========================================================
echo           Haven Services Startup Control Panel
echo ==========================================================
echo.

:: 1. Start Llama Server in a new window (listening strictly on localhost to let NetBird IP Helper proxy it)
echo [1/3] Starting Llama Server (Inference)...
start "Llama Server" /d "C:\Users\admin\haven-server" powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\admin\source\ash-bot-cs\start-ash-server.ps1" -BindHost 127.0.0.1
echo [OK] Llama Server launched in a new window.
echo.

:: 2. Start Stable Diffusion Server in a new window
echo [2/3] Starting Stable Diffusion Server...
cd /d "C:\Users\admin\stable-diffusion-cpp"
if not exist "sd-server.exe" (
    echo [ERROR] sd-server.exe not found in C:\Users\admin\stable-diffusion-cpp
    goto error
)
start "Stable Diffusion Server" /d "C:\Users\admin\stable-diffusion-cpp" sd-server.exe --model "C:\Users\admin\stable-diffusion-cpp\models\DreamShaper8_LCM_q8_0.gguf" --taesd "C:\Users\admin\stable-diffusion-cpp\models\taesd.safetensors" --embd-dir "C:\Users\admin\stable-diffusion-cpp\models\embeddings" --listen-ip 127.0.0.1 --listen-port 8080 --steps 8 --sampling-method lcm --cfg-scale 2.0
echo [OK] SD Server launched in a new window.
echo.

:: 3. Start Haven Server in a new window
echo [3/3] Starting Haven Server (C#)...
cd /d "C:\Users\admin\haven-server"
if not exist "bin\Debug\net10.0\win-x64\haven-server.exe" (
    echo [ERROR] haven-server.exe not found in bin\Debug\net10.0\win-x64
    goto error
)
start "Haven Server" /d "C:\Users\admin\haven-server" bin\Debug\net10.0\win-x64\haven-server.exe
echo [OK] Haven Server launched in a new window.
echo.
echo ==========================================================
echo  All services have been launched!
echo  - Llama Server (API):      http://100.95.198.162:11436
echo  - Stable Diffusion (SD):   http://100.95.198.162:8080
echo  - Haven Server (C# Web):   http://100.95.198.162:18799
echo ==========================================================
echo.
pause
exit

:error
echo.
echo [ERROR] One or more services failed to start. Check paths or rebuild.
pause
exit
