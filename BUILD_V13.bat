@echo off
setlocal
cd /d "%~dp0"
set "NOPAUSE=%~1"

if not exist "VERSION.txt" goto :versionfail
set /p APP_VERSION=<"VERSION.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\SYNC_VERSION.ps1"
if errorlevel 1 goto :versionfail

echo ========================================
echo BUILD TOOL TIKTOK V%APP_VERSION% - VM OPTIMIZED
echo ========================================
echo.
echo [1/2] Build Worker V%APP_VERSION%...
dotnet build ".\ToolTikTokWorkerV13\ToolTikTokWorkerV13.csproj" -c Release
if errorlevel 1 goto :fail

echo.
echo [2/2] Build Manager V%APP_VERSION%...
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

:versionfail
echo.
echo ========================================
echo VERSION SYNC FAILED
echo Kiem tra VERSION.txt va SYNC_VERSION.ps1
echo ========================================
echo.
if /I not "%NOPAUSE%"=="--no-pause" pause
exit /b 1
