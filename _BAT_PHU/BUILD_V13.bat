@echo off
setlocal EnableExtensions
set "NOPAUSE=%~1"
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "ROOT=%%~fI"
cd /d "%ROOT%"

if not exist "VERSION.txt" goto :versionfail
set /p APP_VERSION=<"VERSION.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%SYNC_VERSION.ps1" -Root "%ROOT%"
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
echo Output: %ROOT%\dist_v13
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
echo Kiem tra VERSION.txt va _BAT_PHU\SYNC_VERSION.ps1
echo ========================================
echo.
if /I not "%NOPAUSE%"=="--no-pause" pause
exit /b 1
