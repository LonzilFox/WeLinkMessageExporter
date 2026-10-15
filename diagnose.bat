@echo off
setlocal
cd /d "%~dp0"
set "LOG_DIR=%LOCALAPPDATA%\WeLinkMessageExporter\logs"
echo ================================================
echo WeLink Message Exporter - local diagnostics
echo ================================================
echo.
echo dotnet locations:
where dotnet 2>nul
if errorlevel 1 echo dotnet not found.
echo.
echo dotnet version:
dotnet --version 2>nul
echo.
echo welink-cli locations:
where welink-cli.cmd 2>nul
if errorlevel 1 echo welink-cli.cmd not found.
echo.
echo welink-cli version:
call welink-cli.cmd --version 2>nul
if errorlevel 1 echo Failed to read welink-cli version.
echo.
echo Log folder: %LOG_DIR%
echo.
if exist "%LOG_DIR%\startup-crash.log" (
  echo Startup/crash log exists: %LOG_DIR%\startup-crash.log
) else (
  echo No startup/crash log in default folder yet.
)
if exist "%TEMP%\WeLinkMessageExporter-startup-crash.log" (
  echo Fallback crash log: %TEMP%\WeLinkMessageExporter-startup-crash.log
)
echo.
pause
endlocal
