@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================================
echo   TAO SETUP CHUAN - TOOL TIKTOK V13.5
echo ============================================================
echo.

if not exist "publish_v13_5_vm\ToolTikTokManagerV13.exe" (
    echo [LOI] Khong tim thay:
    echo publish_v13_5_vm\ToolTikTokManagerV13.exe
    echo.
    echo Hay chay TAO_BAN_CAI_V13_5.bat truoc.
    echo.
    pause
    exit /b 1
)

if not exist "ToolTikTok_V13_5.iss" (
    echo [LOI] Khong tim thay ToolTikTok_V13_5.iss
    echo.
    echo Dat file .iss cung thu muc voi file BAT nay.
    echo.
    pause
    exit /b 1
)

set "ISCC="

rem 1) Thu tim qua PATH
for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do (
    if not defined ISCC set "ISCC=%%I"
)

rem 2) Cac duong dan thong dung
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%LocalAppData%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 7\ISCC.exe"

rem 3) Tim nhanh trong Program Files / LocalAppData
if not defined ISCC (
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command ^
      "$roots=@($env:ProgramFiles,${env:ProgramFiles(x86)},$env:LOCALAPPDATA); foreach($r in $roots){ if($r -and (Test-Path $r)){ $f=Get-ChildItem -Path $r -Filter ISCC.exe -File -Recurse -ErrorAction SilentlyContinue | Where-Object {$_.FullName -match 'Inno Setup'} | Select-Object -First 1; if($f){$f.FullName; break} } }"`) do (
        if not defined ISCC set "ISCC=%%I"
    )
)

if not defined ISCC (
    echo [LOI] Inno Setup da cai nhung khong tim thay ISCC.exe.
    echo.
    echo Hay mo Inno Setup, vao Help ^> About neu can,
    echo hoac gui anh thu muc cai dat cho ChatGPT.
    echo.
    pause
    exit /b 1
)

echo [OK] Tim thay Inno Setup:
echo "%ISCC%"
echo.

if exist "SETUP_OUTPUT\ToolTikTok_V13.5_Setup.exe" (
    del /f /q "SETUP_OUTPUT\ToolTikTok_V13.5_Setup.exe" >nul 2>&1
)

echo Dang tao bo cai...
echo.

"%ISCC%" "ToolTikTok_V13_5.iss"

if errorlevel 1 (
    echo.
    echo ============================================================
    echo [LOI] Inno Setup compile that bai.
    echo Gui anh toan bo cua so CMD nay cho ChatGPT.
    echo ============================================================
    echo.
    pause
    exit /b 1
)

if not exist "SETUP_OUTPUT\ToolTikTok_V13.5_Setup.exe" (
    echo.
    echo [LOI] Compile xong nhung khong thay file Setup dau ra.
    echo.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo [OK] DA TAO XONG:
echo SETUP_OUTPUT\ToolTikTok_V13.5_Setup.exe
echo.
echo Chi can gui DUY NHAT file EXE nay sang may khac.
echo ============================================================
echo.
pause
