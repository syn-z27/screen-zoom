@echo off
rem Force-stop ScreenZoom.
rem ScreenZoom runs as administrator, so this script re-launches itself elevated (UAC prompt).
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
taskkill /F /IM ScreenZoom.exe
timeout /t 3 >nul
