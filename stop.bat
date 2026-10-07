@echo off
rem Force-stop ScreenZoom.
rem ScreenZoom runs as administrator, so this script re-launches itself elevated (UAC prompt).
rem System commands are called by full path so that fakes in this folder are never used.
setlocal
set "SYS=%SystemRoot%\System32"
"%SYS%\net.exe" session >nul 2>&1
if errorlevel 1 (
  "%SYS%\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
"%SYS%\taskkill.exe" /F /IM ScreenZoom.exe
"%SYS%\timeout.exe" /t 3 >nul
