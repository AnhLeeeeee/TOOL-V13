@echo off
setlocal EnableExtensions
set "NOPAUSE=%~1"
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "ROOT=%%~fI"
cd /d "%ROOT%"

set "OUT=%CD%\publish_v13_5_vm"
set "ZIP=%CD%\ToolTikTok_V13.5_VM_CLIENT_WIN_X64.zip"
set "TMPZIP=%TEMP%\ToolTikTok_V13.5_VM_CLIENT_WIN_X64_%RANDOM%_%RANDOM%.zip"

if not exist "VERSION.txt" goto :versionfail
set /p APP_VERSION=<"VERSION.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%SYNC_VERSION.ps1" -Root "%ROOT%"
if errorlevel 1 goto :versionfail

if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%OUT%" (
    echo.
    echo ========================================
    echo LOI: KHONG THE XOA THU MUC BUILD CU
    echo %OUT%
    echo Hay dong Tool/Chrome/Explorer dang mo thu muc nay roi thu lai.
    echo ========================================
    if /I not "%NOPAUSE%"=="--no-pause" pause
    exit /b 1
)

if exist "%ZIP%" (
    del /f /q "%ZIP%" >nul 2>&1
    if exist "%ZIP%" (
        echo.
        echo ========================================
        echo LOI: FILE ZIP CU DANG BI KHOA
        echo %ZIP%
        echo Hay dong Explorer/phan mem dang mo file ZIP roi thu lai.
        echo ========================================
        if /I not "%NOPAUSE%"=="--no-pause" pause
        exit /b 1
    )
)

if exist "%TMPZIP%" del /f /q "%TMPZIP%" >nul 2>&1
mkdir "%OUT%"
if errorlevel 1 goto :fail

echo ========================================
echo TAO BAN MAY AO TOOL TIKTOK V%APP_VERSION%
echo XPath-only - KHONG CAN TESSERACT
echo ========================================
echo.

echo [1/3] Publish Worker self-contained win-x64...
dotnet publish ".\ToolTikTokWorkerV13\ToolTikTokWorkerV13.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false -o "%OUT%"
if errorlevel 1 goto :fail

echo.
echo [2/3] Publish Manager self-contained win-x64...
dotnet publish ".\ToolTikTokManagerV13\ToolTikTokManagerV13.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false -o "%OUT%"
if errorlevel 1 goto :fail

for /r "%OUT%" %%F in (*.pdb) do del /q "%%F" >nul 2>&1

> "%OUT%\CHAY_TOOL_V13_5.bat" echo @echo off
>>"%OUT%\CHAY_TOOL_V13_5.bat" echo cd /d "%%~dp0"
>>"%OUT%\CHAY_TOOL_V13_5.bat" echo start "" ".\ToolTikTokManagerV13.exe"

> "%OUT%\README_MAY_AO.txt" echo TOOL TIKTOK V%APP_VERSION% VM CLIENT
>>"%OUT%\README_MAY_AO.txt" echo - Khong can cai .NET 8.
>>"%OUT%\README_MAY_AO.txt" echo - Khong can cai Tesseract/OCR.
>>"%OUT%\README_MAY_AO.txt" echo - Can Google Chrome.
>>"%OUT%\README_MAY_AO.txt" echo - Chay CHAY_TOOL_V13_5.bat hoac ToolTikTokManagerV13.exe.
>>"%OUT%\README_MAY_AO.txt" echo - Profile Chrome: TikTokProfiles\ten_profile\chrome_profile

mkdir "%OUT%\TikTokProfiles" >nul 2>&1
mkdir "%OUT%\profiles" >nul 2>&1

echo.
echo [3/3] Nen ZIP may ao...
echo Dang nen bang tar.exe de tranh loi Compress-Archive...

where tar.exe >nul 2>&1
if errorlevel 1 (
    echo.
    echo LOI: Khong tim thay tar.exe tren Windows.
    goto :fail
)

rem Nen vao TEMP truoc de tranh file ZIP dich bi Explorer/Defender giu.
tar.exe -a -c -f "%TMPZIP%" -C "%OUT%" .
if errorlevel 1 goto :zipfail

if not exist "%TMPZIP%" goto :zipfail
for %%Z in ("%TMPZIP%") do if %%~zZ LEQ 0 goto :zipfail

move /y "%TMPZIP%" "%ZIP%" >nul
if errorlevel 1 goto :zipmovefail

if not exist "%ZIP%" goto :zipmovefail
for %%Z in ("%ZIP%") do if %%~zZ LEQ 0 goto :zipmovefail

echo.
echo ========================================
echo HOAN TAT
echo %ZIP%
echo ========================================
echo.
if /I not "%NOPAUSE%"=="--no-pause" pause
exit /b 0

:versionfail
echo.
echo ========================================
echo LOI: VERSION KHONG HOP LE HOAC KHONG DONG BO DUOC
echo Kiem tra VERSION.txt va SYNC_VERSION.ps1
echo ========================================
if /I not "%NOPAUSE%"=="--no-pause" pause
exit /b 1

:zipfail
if exist "%TMPZIP%" del /f /q "%TMPZIP%" >nul 2>&1
echo.
echo ========================================
echo LOI: NEN ZIP THAT BAI
echo Khong tao duoc file ZIP bang tar.exe.
echo ========================================
goto :failpause

:zipmovefail
echo.
echo ========================================
echo LOI: KHONG THE GHI FILE ZIP DICH
echo Co the Explorer/Defender dang giu file:
echo %ZIP%
echo File ZIP tam neu con se nam tai:
echo %TMPZIP%
echo ========================================
goto :failpause

:fail
echo.
echo ========================================
echo TAO BAN MAY AO THAT BAI
echo ========================================
echo Kiem tra phan loi phia tren.

:failpause
if /I not "%NOPAUSE%"=="--no-pause" pause
exit /b 1
