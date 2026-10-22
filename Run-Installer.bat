@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-C3DBridge.ps1"
if errorlevel 1 pause
