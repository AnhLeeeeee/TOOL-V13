@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"
title Tool TikTok - Tao ban cap nhat
color 0F

rem ===== Mau hien thi (Windows 10/11 Terminal / CMD moi) =====
for /F "delims=" %%E in ('echo prompt $E^| cmd') do set "ESC=%%E"
set "C_RESET=%ESC%[0m"
set "C_TITLE=%ESC%[96m"
set "C_STEP=%ESC%[93m"
set "C_OK=%ESC%[92m"
set "C_ERR=%ESC%[91m"
set "C_INFO=%ESC%[97m"
set "C_DIM=%ESC%[90m"
set "C_ACCENT=%ESC%[94m"

cls
set "HELPER=%CD%\_BAT_PHU"
if not exist "%HELPER%\TAO_BAN_CAI_V13_5.bat" goto :missinghelper
if not exist "%HELPER%\TAO_SETUP_V13_5_AUTO_FIND_INNO.bat" goto :missinghelper

echo %C_TITLE%============================================================%C_RESET%
echo %C_TITLE%   TAO BAN CAP NHAT TOOL TIKTOK - TU DONG THEO VERSION%C_RESET%
echo %C_TITLE%============================================================%C_RESET%
echo.

set "CURRENT_VERSION="
if exist "VERSION.txt" set /p CURRENT_VERSION=<"VERSION.txt"

echo %C_INFO%Phien ban hien tai:%C_RESET% %C_OK%%CURRENT_VERSION%%C_RESET%
set "NEW_VERSION="
set /p "NEW_VERSION=%C_INFO%Nhap phien ban muon tao%C_RESET% %C_DIM%(Enter = %CURRENT_VERSION%):%C_RESET% "
if not defined NEW_VERSION set "NEW_VERSION=%CURRENT_VERSION%"

if not defined NEW_VERSION (
    echo.
    echo %C_ERR%[LOI] Chua co version. Hay nhap theo dang 14.0.1%C_RESET%
    pause
    exit /b 1
)

set "CHECK_VERSION=%NEW_VERSION%"
powershell -NoProfile -Command "if ($env:CHECK_VERSION -match '^\d+\.\d+\.\d+$') { exit 0 } else { exit 1 }"
if errorlevel 1 (
    echo.
    echo %C_ERR%[LOI] Version khong hop le: %NEW_VERSION%%C_RESET%
    echo %C_DIM%Phai co dang X.Y.Z, vi du 14.0.1%C_RESET%
    pause
    exit /b 1
)

set "SETUP_NAME=ToolTikTok_V%NEW_VERSION%_Setup.exe"
set "CLIENT_ZIP_NAME=ToolTikTok_V%NEW_VERSION%_VM_CLIENT_WIN_X64.zip"
set "SETUP_URL=https://github.com/AnhLeeeeee/TOOL-V13/releases/download/v%NEW_VERSION%/%SETUP_NAME%"

>"VERSION.txt" echo %NEW_VERSION%
echo.
echo %C_OK%[OK] VERSION.txt = %NEW_VERSION%%C_RESET%
echo.

echo %C_STEP%============================================================%C_RESET%
echo %C_STEP% [1/3] TAO BAN PUBLISH / ZIP%C_RESET%
echo %C_STEP%============================================================%C_RESET%
call "%HELPER%\TAO_BAN_CAI_V13_5.bat" --no-pause
if errorlevel 1 (
    echo.
    echo %C_ERR%[LOI] TAO_BAN_CAI_V13_5.bat that bai.%C_RESET%
    pause
    exit /b 1
)
echo %C_OK%[OK] Publish / ZIP hoan tat.%C_RESET%

echo.
echo %C_STEP%============================================================%C_RESET%
echo %C_STEP% [2/3] TAO SETUP%C_RESET%
echo %C_STEP%============================================================%C_RESET%
call "%HELPER%\TAO_SETUP_V13_5_AUTO_FIND_INNO.bat" --no-pause
if errorlevel 1 (
    echo.
    echo %C_ERR%[LOI] TAO_SETUP_V13_5_AUTO_FIND_INNO.bat that bai.%C_RESET%
    pause
    exit /b 1
)
echo %C_OK%[OK] Setup hoan tat.%C_RESET%

set "RELEASE_DIR=%CD%\RELEASE_OUTPUT\V%NEW_VERSION%"
if not exist "%RELEASE_DIR%" mkdir "%RELEASE_DIR%"

if not exist "SETUP_OUTPUT\%SETUP_NAME%" (
    echo.
    echo %C_ERR%[LOI] Khong tim thay file Setup sau khi build:%C_RESET%
    echo %C_DIM%SETUP_OUTPUT\%SETUP_NAME%%C_RESET%
    pause
    exit /b 1
)

if not exist "ToolTikTok_V13.5_VM_CLIENT_WIN_X64.zip" (
    echo.
    echo %C_ERR%[LOI] Khong tim thay file ZIP may khach sau khi build:%C_RESET%
    echo %C_DIM%ToolTikTok_V13.5_VM_CLIENT_WIN_X64.zip%C_RESET%
    pause
    exit /b 1
)

copy /y "SETUP_OUTPUT\%SETUP_NAME%" "%RELEASE_DIR%\%SETUP_NAME%" >nul
if errorlevel 1 (
    echo %C_ERR%[LOI] Khong copy duoc file Setup vao RELEASE_OUTPUT.%C_RESET%
    pause
    exit /b 1
)

copy /y "ToolTikTok_V13.5_VM_CLIENT_WIN_X64.zip" "%RELEASE_DIR%\%CLIENT_ZIP_NAME%" >nul
if errorlevel 1 (
    echo %C_ERR%[LOI] Khong copy duoc file ZIP may khach vao RELEASE_OUTPUT.%C_RESET%
    pause
    exit /b 1
)

set "SETUP_SHA="
for /f "usebackq delims=" %%H in (`powershell -NoProfile -Command "(Get-FileHash -LiteralPath '%RELEASE_DIR%\%SETUP_NAME%' -Algorithm SHA256).Hash.ToLowerInvariant()"`) do set "SETUP_SHA=%%H"

if not defined SETUP_SHA (
    echo.
    echo %C_ERR%[LOI] Khong tinh duoc SHA256 cua file Setup.%C_RESET%
    pause
    exit /b 1
)

set "CHECK_SHA=%SETUP_SHA%"
powershell -NoProfile -Command "if ($env:CHECK_SHA -match '^[0-9a-fA-F]{64}$') { exit 0 } else { exit 1 }"
if errorlevel 1 (
    echo.
    echo %C_ERR%[LOI] SHA256 khong hop le: %SETUP_SHA%%C_RESET%
    pause
    exit /b 1
)

echo.
echo %C_STEP%============================================================%C_RESET%
echo %C_STEP% [3/3] DONG BO version.json + versions.json%C_RESET%
echo %C_STEP%============================================================%C_RESET%

powershell -NoProfile -ExecutionPolicy Bypass -File "%HELPER%\SYNC_VERSION.ps1" -Root "%CD%" -SetupPath "%RELEASE_DIR%\%SETUP_NAME%"
if errorlevel 1 (
    echo.
    echo %C_ERR%[LOI] Khong dong bo duoc version.json / versions.json.%C_RESET%
    pause
    exit /b 1
)

if not exist "version.json" (
    echo.
    echo %C_ERR%[LOI] version.json khong ton tai sau khi dong bo.%C_RESET%
    pause
    exit /b 1
)
if not exist "versions.json" (
    echo.
    echo %C_ERR%[LOI] versions.json khong ton tai sau khi dong bo.%C_RESET%
    pause
    exit /b 1
)

copy /y "version.json" "%RELEASE_DIR%\version.json" >nul
copy /y "versions.json" "%RELEASE_DIR%\versions.json" >nul
copy /y "VERSION.txt" "%RELEASE_DIR%\VERSION.txt" >nul

echo %C_OK%[OK] Da dong bo manifest%C_RESET%
echo %C_DIM%     version       =%C_RESET% %C_INFO%%NEW_VERSION%%C_RESET%
echo %C_DIM%     setupUrl      =%C_RESET% %C_INFO%%SETUP_URL%%C_RESET%
echo %C_DIM%     sha256        =%C_RESET% %C_INFO%%SETUP_SHA%%C_RESET%
echo %C_DIM%     version.json  =%C_RESET% %C_INFO%%CD%\version.json%C_RESET%
echo %C_DIM%     versions.json =%C_RESET% %C_INFO%%CD%\versions.json%C_RESET%
echo.

echo %C_OK%============================================================%C_RESET%
echo %C_OK%   HOAN TAT BAN CAP NHAT V%NEW_VERSION%%C_RESET%
echo %C_OK%============================================================%C_RESET%
echo.
echo %C_INFO%Thu muc ban cap nhat:%C_RESET%
echo %C_ACCENT%%RELEASE_DIR%%C_RESET%
echo.
echo %C_INFO%FILE UPLOAD LEN GITHUB RELEASE:%C_RESET%
echo %C_ACCENT%%RELEASE_DIR%\%SETUP_NAME%%C_RESET%
echo.
echo %C_INFO%Tag GitHub:%C_RESET%  %C_OK%v%NEW_VERSION%%C_RESET%
echo %C_INFO%Release:%C_RESET%     Tool TikTok V%NEW_VERSION%
echo %C_INFO%SHA256:%C_RESET%      %SETUP_SHA%
echo.
echo %C_INFO%File ZIP may khach:%C_RESET%
echo %C_ACCENT%%RELEASE_DIR%\%CLIENT_ZIP_NAME%%C_RESET%
echo.
echo %C_INFO%Manifest da tu dong cap nhat:%C_RESET%
echo %C_ACCENT%%CD%\version.json%C_RESET%
echo %C_ACCENT%%CD%\versions.json%C_RESET%
echo %C_OK%============================================================%C_RESET%
echo.
pause
exit /b 0

:missinghelper
echo.
echo %C_ERR%============================================================%C_RESET%
echo %C_ERR% [LOI] THIEU FILE TRONG THU MUC _BAT_PHU%C_RESET%
echo %C_ERR% Hay kiem tra lai cac file BAT phu.%C_RESET%
echo %C_ERR%============================================================%C_RESET%
pause
exit /b 1
