@echo off
rem ---------------------------------------------------------------------------
rem Double-click to publish LongShot to GitHub.
rem
rem Why this wrapper exists: .ps1 files that carry a "downloaded from Internet"
rem mark (Zone.Identifier) are refused by the default RemoteSigned execution
rem policy -- double-clicking the script just prints a security error. This .cmd
rem launches it with -ExecutionPolicy Bypass so it always runs.
rem
rem Usage:
rem   double-click            -> publishes to the default repo, tag v1.0.0
rem   upload-to-github.cmd <owner/repo> <tag>
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0"

set "REPO=%~1"
set "TAG=%~2"
if "%REPO%"=="" set "REPO=07vvvv/LongShot"
if "%TAG%"==""  set "TAG=v1.0.0"

echo Repo: %REPO%    Tag: %TAG%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload-to-github.ps1" -Repo "%REPO%" -Tag "%TAG%"
set "CODE=%ERRORLEVEL%"

echo.
if "%CODE%"=="0" (
  echo [OK] Published.
) else (
  echo [FAILED] exit code %CODE%
)
echo.
pause