@echo off
title Checkers Chinook & KingsRow API Launcher
echo ========================================================
echo   Starting Checkers Chinook & KingsRow API...
echo ========================================================
echo.

cd /d "%~dp0publish"
if not exist "Checkers.Api.exe" (
    echo [ERROR] Checkers.Api.exe not found in publish/ directory.
    echo Running build_all.bat to build and publish first...
    cd /d "%~dp0CheckersApp"
    call build_all.bat
    cd /d "%~dp0publish"
)

echo [INFO] Starting Checkers.Api.exe...
echo [INFO] All live search metrics and logs will be shown in this window.
echo [INFO] The interactive checkers board will open in your browser.
echo [INFO] To stop the server: press Ctrl+C here or click 'Stop Server' in the web UI.
echo.

Checkers.Api.exe

echo.
echo ========================================================
echo   Checkers.Api server has stopped.
echo ========================================================
pause
