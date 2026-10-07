@echo off
rem Install ScreenZoom.exe into Program Files, which only administrators can write to.
rem The auto-start task launches the exe with the highest privileges, so the exe must not
rem live in a folder that normal programs can modify. Run build.bat first.
rem System commands are called by full path so that fakes in this folder are never used.
setlocal
set "SYS=%SystemRoot%\System32"
set "DEST=%ProgramFiles%\ScreenZoom"

"%SYS%\net.exe" session >nul 2>&1
if errorlevel 1 (
  "%SYS%\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

if not exist "%~dp0ScreenZoom.exe" (
  echo ScreenZoom.exe not found. Run build.bat first.
  "%SYS%\timeout.exe" /t 5 >nul
  exit /b 1
)

"%SYS%\taskkill.exe" /F /IM ScreenZoom.exe >nul 2>&1
"%SYS%\timeout.exe" /t 1 >nul

if not exist "%DEST%" mkdir "%DEST%"
copy /Y "%~dp0ScreenZoom.exe" "%DEST%\ScreenZoom.exe" >nul
if errorlevel 1 (
  echo Failed to copy ScreenZoom.exe to "%DEST%".
  "%SYS%\timeout.exe" /t 5 >nul
  exit /b 1
)

start "" "%DEST%\ScreenZoom.exe"
echo Installed to "%DEST%".
"%SYS%\timeout.exe" /t 3 >nul
