@echo off
echo C3D Print Bridge setup - running in sequence.
echo [1/3] Enabling PowerShell scripts for this account...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope CurrentUser -Force"
if %ERRORLEVEL% neq 0 echo Warning: enable step returned %ERRORLEVEL%, continuing anyway.
echo Checking for the project file...
if not exist "%~dp0src\C3DBridge\C3DBridge.csproj" (
  echo PROJECT FILE NOT FOUND.
  echo Extract ALL files from the version folder / zip, including the src subfolder,
  echo and run this file from inside that extracted folder, not from inside the zip.
  pause
  exit /b 1
)
echo [2/3] Running the bridge installer script...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-C3DBridge.ps1"
if %ERRORLEVEL% neq 0 (
  echo Installer failed with error %ERRORLEVEL%.
  pause
  exit /b %ERRORLEVEL%
)
echo [3/3] Done. Bridge installed to C:\C3D_Bridge - check the tray icon by the clock.
pause
