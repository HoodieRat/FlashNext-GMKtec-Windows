@echo off
setlocal EnableExtensions
set "APP=%ProgramFiles%\FlashNextManager\app\current"
if exist "%LOCALAPPDATA%\FlashNextManager\app\" set "APP=%LOCALAPPDATA%\FlashNextManager\app\current"
set "DASH=%APP%\FlashNext.Dashboard.exe"
set "EXE=%APP%\FlashNext.Manager.exe"
if /I "%~1"=="--console" (
  if not exist "%EXE%" (
    echo FlashNext Manager is not installed. Run install.cmd first.
    exit /b 2
  )
  "%EXE%" %*
  exit /b %ERRORLEVEL%
)
if exist "%DASH%" (
  start "" "%DASH%"
  exit /b 0
)
if not exist "%EXE%" (
  echo FlashNext is not installed. Run install.cmd first.
  exit /b 2
)
"%EXE%" %*
exit /b %ERRORLEVEL%
