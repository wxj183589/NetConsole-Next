@echo off
setlocal
PowerShell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Reset-WebPassword.ps1" %*
exit /b %errorlevel%
