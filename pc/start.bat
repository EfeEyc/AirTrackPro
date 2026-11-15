@echo off
cd /d "%~dp0"
if not exist "AirTrackBridge.exe" (
    powershell -ExecutionPolicy Bypass -File "%~dp0build_local.ps1"
    if errorlevel 1 pause & exit /b 1
)
"%~dp0AirTrackBridge.exe"
pause
