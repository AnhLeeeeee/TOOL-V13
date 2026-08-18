@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if not exist ".\_BAT_PHU\BUILD_V13.bat" (
    echo ========================================
    echo LOI: KHONG TIM THAY _BAT_PHU\BUILD_V13.bat
    echo Hay ap dung lai PATCH_DON_GON_BAT_V13.
    echo ========================================
    pause
    exit /b 1
)

call ".\_BAT_PHU\BUILD_V13.bat" --no-pause
if errorlevel 1 (
    echo.
    echo ========================================
    echo BUILD FAILED - KHONG MO TOOL
    echo ========================================
    pause
    exit /b 1
)

if not exist ".\dist_v13\ToolTikTokManagerV13.exe" (
    echo.
    echo ========================================
    echo BUILD OK NHUNG KHONG TIM THAY FILE EXE
    echo .\dist_v13\ToolTikTokManagerV13.exe
    echo ========================================
    pause
    exit /b 1
)

start "" ".\dist_v13\ToolTikTokManagerV13.exe"
exit /b 0
