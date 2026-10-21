@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK not found. Install .NET 8 SDK or newer.
  pause
  exit /b 1
)
dotnet publish WeLinkMessageExporter.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false --output "publish\win-x64"
if errorlevel 1 (
  echo.
  echo Publish failed. Check the build errors above.
  pause
  exit /b 1
)
echo.
echo Published to: %CD%\publish\win-x64
pause
endlocal
