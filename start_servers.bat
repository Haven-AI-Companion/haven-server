@echo off
title Haven Services Unified Control Panel
color 0B

echo ==========================================================
echo           Haven Services Unified Control Panel
echo ==========================================================
echo.
echo  Starting Haven Server...
echo  (Llama and Stable Diffusion servers will be auto-managed
echo  in the background as unified sidecars)
echo.

cd /d "C:\Users\admin\haven-server"
if not exist "bin\Debug\net10.0\win-x64\haven-server.exe" (
    echo [ERROR] haven-server.exe not found in C:\Users\admin\haven-server\bin\Debug\net10.0\win-x64
    goto error
)

:: Run Haven Server in the foreground
bin\Debug\net10.0\win-x64\haven-server.exe
exit

:error
echo.
echo [ERROR] Haven Server failed to start. Check paths or rebuild.
pause
exit
