@echo off
rem Build ScreenZoom from source and install it into Program Files, which only administrators can write to.
rem The exe runs as administrator (and the auto-start task launches it with the highest privileges),
rem so it is compiled straight into the install folder and never left in this user-writable folder.
rem System commands and the compiler are called by full path so that fakes in this folder are never used.
setlocal
set "SYS=%SystemRoot%\System32"
rem Magnification API does not support WOW64, so build as x64 with the 64-bit compiler.
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "DEST=%ProgramFiles%\ScreenZoom"

"%SYS%\net.exe" session >nul 2>&1
if errorlevel 1 (
  "%SYS%\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

if not exist "%DEST%" mkdir "%DEST%"

rem Build next to the installed exe first, so a failed build leaves the current install untouched.
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /win32manifest:"%~dp0ScreenZoom.manifest" ^
  /out:"%DEST%\ScreenZoom.new.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0ScreenZoom.cs"
if errorlevel 1 (
  echo Build failed.
  if exist "%DEST%\ScreenZoom.new.exe" del "%DEST%\ScreenZoom.new.exe"
  pause
  exit /b 1
)

"%SYS%\taskkill.exe" /F /IM ScreenZoom.exe >nul 2>&1
"%SYS%\timeout.exe" /t 1 >nul

move /Y "%DEST%\ScreenZoom.new.exe" "%DEST%\ScreenZoom.exe" >nul
if errorlevel 1 (
  echo Failed to replace "%DEST%\ScreenZoom.exe".
  pause
  exit /b 1
)

rem Remove an exe left by the old build.bat, if any.
if exist "%~dp0ScreenZoom.exe" del "%~dp0ScreenZoom.exe"

start "" "%DEST%\ScreenZoom.exe"
echo Installed to "%DEST%".
"%SYS%\timeout.exe" /t 3 >nul
