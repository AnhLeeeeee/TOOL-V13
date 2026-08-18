@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if not exist "VERSION.txt" goto :versionfail
set /p APP_VERSION=<"VERSION.txt"

powershell -NoProfile -ExecutionPolicy Bypass -File ".\SYNC_VERSION.ps1"
if errorlevel 1 goto :versionfail

echo ========================================
echo BUILD + RUN TOOL TIKTOK V%APP_VERSION%
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
if not exist ".\dist_v13\ToolTikTokManagerV13.exe" (
    echo ========================================
    echo BUILD OK NHUNG KHONG TIM THAY FILE EXE
    echo .\dist_v13\ToolTikTokManagerV13.exe
    echo ========================================
    pause
    exit /b 1
)

echo ========================================
echo BUILD OK - DANG MO TOOL...
echo ========================================
start "" ".\dist_v13\ToolTikTokManagerV13.exe"
exit /b 0

:fail
echo.
echo ========================================
echo BUILD FAILED - KHONG MO TOOL
echo ========================================
pause
exit /b 1

:versionfail
echo.
echo ========================================
echo VERSION SYNC FAILED
echo Kiem tra VERSION.txt va SYNC_VERSION.ps1
echo ========================================
pause
exit /b 1
