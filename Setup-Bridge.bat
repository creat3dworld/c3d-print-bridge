@echo off
echo C3D Print Bridge setup - running in sequence.
echo [1/3] Enabling PowerShell scripts for this account...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope CurrentUser -Force"
if %ERRORLEVEL% neq 0 echo Warning: enable step returned %ERRORLEVEL%, continuing anyway.
echo [2/3] Running the bridge installer script...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-C3DBridge.ps1"
if %ERRORLEVEL% neq 0 (
  echo Installer failed with error %ERRORLEVEL%.
  pause
  exit /b %ERRORLEVEL%
)
echo [3/3] Done. Bridge installed to C:\C3D_Bridge - check the tray icon by the clock.
pause
