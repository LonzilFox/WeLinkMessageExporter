@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul 2>nul
title WeLink CLI - Scan QR code to sign in
echo.
echo ===============================================
echo   WeLink CLI authorization
echo ===============================================
echo.
echo 1. Open WeLink on your phone.
echo 2. Scan the QR code displayed by WeLink CLI.
echo 3. Approve authorization on the phone.
echo.
echo This window stays open until the CLI finishes.
echo Do not send the QR code or login credentials to anyone.
echo.
call welink-cli.cmd auth login
set "LOGIN_RESULT=%ERRORLEVEL%"
if not "%LOGIN_RESULT%"=="0" (
  echo.
  echo WeLink CLI login failed or was cancelled [exit %LOGIN_RESULT%].
  echo Review the error above, then press any key to close this window.
  pause >nul
) else (
  echo.
  echo CLI login command finished. Checking authorization in the app...
)
exit /b %LOGIN_RESULT%
