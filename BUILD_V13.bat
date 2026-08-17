@echo off
setlocal
cd /d "%~dp0"
set "NOPAUSE=%~1"

echo ========================================
echo BUILD TOOL TIKTOK V13.5 - VM OPTIMIZED
echo ========================================
echo.
echo [1/2] Build V13.5 worker...
dotnet build ".\ToolTikTokWorkerV13\ToolTikTokWorkerV13.csproj" -c Release
if errorlevel 1 goto :fail

echo.
echo [2/2] Build V13.5 manager...
dotnet build ".\ToolTikTokManagerV13\ToolTikTokManagerV13.csproj" -c Release
if errorlevel 1 goto :fail

echo.
echo ========================================
echo BUILD OK
echo Output: %CD%\dist_v13
echo ========================================
echo.
if /I not "%NOPAUSE%"=="--no-pause" pause
exit /b 0

:fail
echo.
echo ========================================
echo BUILD FAILED
echo ========================================
echo.
if /I not "%NOPAUSE%"=="--no-pause" pause
exit /b 1
