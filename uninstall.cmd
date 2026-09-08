@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Uninstall-FlashNext.ps1" %*
exit /b %ERRORLEVEL%
