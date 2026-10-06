@echo off
echo ========================================================
echo   Building Checkers Chinook Solution and Standalone Exe
echo ========================================================

echo 1. Building CheckersApp solution...
dotnet build CheckersApp.slnx -c Release

echo.
echo 2. Publishing single-file executable to publish/ ...
dotnet publish Checkers.Api/Checkers.Api.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../publish

echo.
echo ========================================================
echo   Build and Publish completed successfully!
echo ========================================================
echo.
echo Standalone executable is ready at:
echo   publish\Checkers.Api.exe
echo.
echo Double-click publish\Checkers.Api.exe to launch the API
echo and open the checkers board in your browser automatically.
echo ========================================================
pause