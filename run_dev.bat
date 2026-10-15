@echo off
setlocal
cd /d "%~dp0"
set "LOG_DIR=%LOCALAPPDATA%\WeLinkMessageExporter\logs"
echo WeLink Message Exporter development launcher
echo Startup and crash logs: %LOG_DIR%
echo.
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK not found. Install .NET 8 SDK or newer.
  pause
  exit /b 1
)
dotnet build WeLinkMessageExporter.csproj --configuration Debug --nologo
if errorlevel 1 (
  echo.
  echo Build failed. See the errors above.
  pause
  exit /b 1
)
echo.
echo Starting the app...
dotnet run --project WeLinkMessageExporter.csproj --configuration Debug --no-build
set "APP_EXIT_CODE=%ERRORLEVEL%"
if not "%APP_EXIT_CODE%"=="0" (
  echo.
  echo Application exited with error code %APP_EXIT_CODE%.
  echo Check %LOG_DIR%\startup-crash.log and the dated diagnostic log.
  pause
  exit /b %APP_EXIT_CODE%
)
endlocal
