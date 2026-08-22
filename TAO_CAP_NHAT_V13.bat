@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"

set "HELPER=%CD%\_BAT_PHU"
if not exist "%HELPER%\TAO_BAN_CAI_V13_5.bat" goto :missinghelper
if not exist "%HELPER%\TAO_SETUP_V13_5_AUTO_FIND_INNO.bat" goto :missinghelper

echo ============================================================
echo   TAO BAN CAP NHAT TOOL TIKTOK - TU DONG THEO VERSION
echo ============================================================
echo.

set "CURRENT_VERSION="
if exist "VERSION.txt" set /p CURRENT_VERSION=<"VERSION.txt"

echo Phien ban hien tai: %CURRENT_VERSION%
set "NEW_VERSION="
set /p "NEW_VERSION=Nhap phien ban muon tao (Enter = %CURRENT_VERSION%): "
if not defined NEW_VERSION set "NEW_VERSION=%CURRENT_VERSION%"

if not defined NEW_VERSION (
    echo.
    echo [LOI] Chua co version. Hay nhap theo dang 13.6.3
    pause
    exit /b 1
)

set "CHECK_VERSION=%NEW_VERSION%"
powershell -NoProfile -Command "if ($env:CHECK_VERSION -match '^\d+\.\d+\.\d+$') { exit 0 } else { exit 1 }"
if errorlevel 1 (
    echo.
    echo [LOI] Version khong hop le: %NEW_VERSION%
    echo Phai co dang X.Y.Z, vi du 13.6.3
    pause
    exit /b 1
)

set "SETUP_NAME=ToolTikTok_V%NEW_VERSION%_Setup.exe"
set "CLIENT_ZIP_NAME=ToolTikTok_V%NEW_VERSION%_VM_CLIENT_WIN_X64.zip"
set "SETUP_URL=https://github.com/AnhLeeeeee/TOOL-V13/releases/download/v%NEW_VERSION%/%SETUP_NAME%"

>"VERSION.txt" echo %NEW_VERSION%
echo.
echo [OK] VERSION.txt = %NEW_VERSION%
echo.

echo ============================================================
echo [1/3] TAO BAN PUBLISH / ZIP
echo ============================================================
call "%HELPER%\TAO_BAN_CAI_V13_5.bat" --no-pause
if errorlevel 1 (
    echo.
    echo [LOI] TAO_BAN_CAI_V13_5.bat that bai.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo [2/3] TAO SETUP
echo ============================================================
call "%HELPER%\TAO_SETUP_V13_5_AUTO_FIND_INNO.bat" --no-pause
if errorlevel 1 (
    echo.
    echo [LOI] TAO_SETUP_V13_5_AUTO_FIND_INNO.bat that bai.
    pause
    exit /b 1
)

set "RELEASE_DIR=%CD%\RELEASE_OUTPUT\V%NEW_VERSION%"
if not exist "%RELEASE_DIR%" mkdir "%RELEASE_DIR%"

if not exist "SETUP_OUTPUT\%SETUP_NAME%" (
    echo.
    echo [LOI] Khong tim thay file Setup sau khi build:
    echo SETUP_OUTPUT\%SETUP_NAME%
    pause
    exit /b 1
)

if not exist "ToolTikTok_V13.5_VM_CLIENT_WIN_X64.zip" (
    echo.
    echo [LOI] Khong tim thay file ZIP may khach sau khi build:
    echo ToolTikTok_V13.5_VM_CLIENT_WIN_X64.zip
    pause
    exit /b 1
)

copy /y "SETUP_OUTPUT\%SETUP_NAME%" "%RELEASE_DIR%\%SETUP_NAME%" >nul
if errorlevel 1 (
    echo [LOI] Khong copy duoc file Setup vao RELEASE_OUTPUT.
    pause
    exit /b 1
)

copy /y "ToolTikTok_V13.5_VM_CLIENT_WIN_X64.zip" "%RELEASE_DIR%\%CLIENT_ZIP_NAME%" >nul
if errorlevel 1 (
    echo [LOI] Khong copy duoc file ZIP may khach vao RELEASE_OUTPUT.
    pause
    exit /b 1
)

set "SETUP_SHA="
for /f "usebackq delims=" %%H in (`powershell -NoProfile -Command "(Get-FileHash -LiteralPath '%RELEASE_DIR%\%SETUP_NAME%' -Algorithm SHA256).Hash.ToLowerInvariant()"`) do set "SETUP_SHA=%%H"

if not defined SETUP_SHA (
    echo.
    echo [LOI] Khong tinh duoc SHA256 cua file Setup.
    pause
    exit /b 1
)

set "CHECK_SHA=%SETUP_SHA%"
powershell -NoProfile -Command "if ($env:CHECK_SHA -match '^[0-9a-fA-F]{64}$') { exit 0 } else { exit 1 }"
if errorlevel 1 (
    echo.
    echo [LOI] SHA256 khong hop le: %SETUP_SHA%
    pause
    exit /b 1
)

echo.
echo ============================================================
echo [3/3] DONG BO version.json + versions.json
echo ============================================================

powershell -NoProfile -ExecutionPolicy Bypass -File "%HELPER%\SYNC_VERSION.ps1" -Root "%CD%" -SetupPath "%RELEASE_DIR%\%SETUP_NAME%"
if errorlevel 1 (
    echo.
    echo [LOI] Khong dong bo duoc version.json / versions.json.
    pause
    exit /b 1
)

if not exist "version.json" (
    echo.
    echo [LOI] version.json khong ton tai sau khi dong bo.
    pause
    exit /b 1
)
if not exist "versions.json" (
    echo.
    echo [LOI] versions.json khong ton tai sau khi dong bo.
    pause
    exit /b 1
)

copy /y "version.json" "%RELEASE_DIR%\version.json" >nul
copy /y "versions.json" "%RELEASE_DIR%\versions.json" >nul
copy /y "VERSION.txt" "%RELEASE_DIR%\VERSION.txt" >nul

echo [OK] Da dong bo manifest:
echo      version  = %NEW_VERSION%
echo      setupUrl = %SETUP_URL%
echo      sha256   = %SETUP_SHA%
echo      version.json  = %CD%\version.json
echo      versions.json = %CD%\versions.json
echo.

echo ============================================================
echo   HOAN TAT BAN CAP NHAT V%NEW_VERSION%
echo ============================================================
echo Thu muc ban cap nhat:
echo %RELEASE_DIR%
echo.
echo FILE UPLOAD LEN GITHUB RELEASE:
echo %RELEASE_DIR%\%SETUP_NAME%
echo.
echo Tag GitHub:   v%NEW_VERSION%
echo Release:      Tool TikTok V%NEW_VERSION%
echo SHA256:       %SETUP_SHA%
echo.
echo File ZIP may khach:
echo %RELEASE_DIR%\%CLIENT_ZIP_NAME%
echo.
echo version.json + versions.json da tu dong cap nhat:
echo %CD%\version.json
echo %CD%\versions.json
echo ============================================================
echo.
pause
exit /b 0

:missinghelper
echo.
echo ============================================================
echo [LOI] THIEU FILE TRONG THU MUC _BAT_PHU
echo Hay kiem tra lai cac file BAT phu.
echo ============================================================
pause
exit /b 1
