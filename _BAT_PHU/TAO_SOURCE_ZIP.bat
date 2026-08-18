@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "ROOT=%%~fI"
cd /d "%ROOT%"

echo ========================================
echo TAO SOURCE ZIP SACH - TOOL TIKTOK V13
echo ========================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%TAO_SOURCE_ZIP.ps1" -Root "%ROOT%"
if errorlevel 1 (
    echo.
    echo [LOI] Khong tao duoc source ZIP sach.
    pause
    exit /b 1
)
echo.
pause
