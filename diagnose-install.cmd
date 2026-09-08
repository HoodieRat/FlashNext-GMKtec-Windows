@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Get-InstallDiagnostics.ps1" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" echo Diagnostic collection ended with exit code %RC%.
exit /b %RC%
