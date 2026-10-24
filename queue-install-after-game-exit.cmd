@echo off
setlocal
set "SCRIPT=%~dp0install-after-game-exit.ps1"
start "ADOFAI Editor Studio Auto Install" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
echo Installation queued. It will build now and install after the game exits.
timeout /t 2 /nobreak >nul
