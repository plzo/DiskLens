@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Package.ps1"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" echo Packaging failed. See output above.
pause
exit /b %RC%
