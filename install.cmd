@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Install-FlashNext.ps1" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" echo Installation ended with exit code %RC%.
exit /b %RC%
