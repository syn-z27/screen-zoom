@echo off
rem Build with csc.exe bundled with .NET Framework 4.x (no extra install needed).
rem Magnification API does not support WOW64, so build as x64.
rem The manifest makes the exe run as administrator.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ ^
  /win32manifest:"%~dp0ScreenZoom.manifest" ^
  /out:"%~dp0ScreenZoom.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0ScreenZoom.cs"
