@echo off
setlocal EnableExtensions
set "ROOT=%~dp0.."
set "PY=%LOCALAPPDATA%\FlashNextManager\python-env\Scripts\python.exe"
set "DL=%ROOT%\scripts\model_download.py"
set "LOCK=%ROOT%\manifests\model.lock.json"
set "DEST=C:\FlashNextModels\Qwen3.8-Flash-Next-ROCmFP4-FAST"
set "CACHE=%LOCALAPPDATA%\FlashNextManager\download-cache"
set "LOGDIR=%LOCALAPPDATA%\FlashNextManager\logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"
set "LOG=%LOGDIR%\model-download-2026-08-28.log"
set "PYTHONUNBUFFERED=1"
set "PYTHONIOENCODING=utf-8"
"%PY%" -u "%DL%" --mode download --lock "%LOCK%" --destination "%DEST%" --cache "%CACHE%" --workers 2 --accept-license >>"%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
echo EXIT=%RC%>>"%LOG%"
if "%RC%"=="0" (
  echo DONE
) else (
  echo FAILED
)
exit /b %RC%
