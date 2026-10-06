@echo off
setlocal EnableExtensions
set "NOPAUSE=%~1"
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "ROOT=%%~fI"
cd /d "%ROOT%"

set "OUT=%CD%\publish_v13_5_vm"
set "ZIP=%CD%\ToolTikTok_V13.5_VM_CLIENT_WIN_X64.zip"
set "TMPZIP=%TEMP%\ToolTikTok_V13.5_VM_CLIENT_WIN_X64_%RANDOM%_%RANDOM%.zip"

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%ENSURE_VERSION_LAYOUT.ps1" -Root "%ROOT%"
if errorlevel 1 goto :versionfail
if not exist "_version\VERSION.txt" goto :versionfail
set /p APP_VERSION=<"_version\VERSION.txt"
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

echo [1/4] Publish Worker self-contained win-x64...
dotnet publish ".\ToolTikTokWorkerV13\ToolTikTokWorkerV13.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false -o "%OUT%"
if errorlevel 1 goto :fail

echo.
echo [2/4] Publish Manager self-contained win-x64...
dotnet publish ".\ToolTikTokManagerV13\ToolTikTokManagerV13.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false -o "%OUT%"
if errorlevel 1 goto :fail

echo.
echo [2B/4] Tao BUILD ID + SHA256 Manager cho Shadow Mode...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%SYNC_VERSION.ps1" -Root "%ROOT%" -ManagerExePath "%OUT%\ToolTikTokManagerV13.exe" -GenerateBuildIdentity
if errorlevel 1 goto :buildidentityfail

if not exist "%OUT%\build_identity.json" goto :buildidentityfail

echo.
echo [3/4] Publish Comment Visibility Monitor self-contained win-x64...
if not exist ".\CommentVisibilityMonitor\CommentVisibilityMonitor.csproj" (
    echo.
    echo ========================================
    echo LOI: KHONG TIM THAY COMMENT MONITOR SOURCE
    echo .\CommentVisibilityMonitor\CommentVisibilityMonitor.csproj
    echo ========================================
    goto :fail
)
dotnet publish ".\CommentVisibilityMonitor\CommentVisibilityMonitor.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false -o "%OUT%\CommentCheck"
if errorlevel 1 goto :fail

if not exist "%OUT%\CommentCheck\CommentVisibilityMonitor.exe" (
    echo.
    echo ========================================
    echo LOI: COMMENT MONITOR BUILD XONG NHUNG KHONG CO EXE
    echo %OUT%\CommentCheck\CommentVisibilityMonitor.exe
    echo ========================================
    goto :fail
)

for /r "%OUT%" %%F in (*.pdb) do del /q "%%F" >nul 2>&1

> "%OUT%\CHAY_TOOL_V13_5.bat" echo @echo off
>>"%OUT%\CHAY_TOOL_V13_5.bat" echo cd /d "%%~dp0"
>>"%OUT%\CHAY_TOOL_V13_5.bat" echo start "" ".\ToolTikTokManagerV13.exe"

> "%OUT%\CHAY_KIEM_TRA_CMT.bat" echo @echo off
>>"%OUT%\CHAY_KIEM_TRA_CMT.bat" echo cd /d "%%~dp0CommentCheck"
>>"%OUT%\CHAY_KIEM_TRA_CMT.bat" echo if not exist ".\CommentVisibilityMonitor.exe" ^(
>>"%OUT%\CHAY_KIEM_TRA_CMT.bat" echo   echo KHONG TIM THAY CommentVisibilityMonitor.exe
>>"%OUT%\CHAY_KIEM_TRA_CMT.bat" echo   pause
>>"%OUT%\CHAY_KIEM_TRA_CMT.bat" echo   exit /b 1
>>"%OUT%\CHAY_KIEM_TRA_CMT.bat" echo ^)
>>"%OUT%\CHAY_KIEM_TRA_CMT.bat" echo start "" ".\CommentVisibilityMonitor.exe"

> "%OUT%\README_MAY_AO.txt" echo TOOL TIKTOK V%APP_VERSION% VM CLIENT
>>"%OUT%\README_MAY_AO.txt" echo - Khong can cai .NET 8.
>>"%OUT%\README_MAY_AO.txt" echo - Khong can cai Tesseract/OCR.
>>"%OUT%\README_MAY_AO.txt" echo - Can Google Chrome.
>>"%OUT%\README_MAY_AO.txt" echo - Chay CHAY_TOOL_V13_5.bat hoac ToolTikTokManagerV13.exe.
>>"%OUT%\README_MAY_AO.txt" echo - Kiem tra CMT: chay CHAY_KIEM_TRA_CMT.bat.
>>"%OUT%\README_MAY_AO.txt" echo - Profile Chrome: TikTokProfiles\ten_profile\chrome_profile

mkdir "%OUT%\TikTokProfiles" >nul 2>&1
mkdir "%OUT%\profiles" >nul 2>&1

echo.
echo [4/4] Nen ZIP may ao...
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

:buildidentityfail
echo.
echo ========================================
echo LOI: KHONG TAO/DOI CHIEU DUOC BUILD IDENTITY
echo Kiem tra ToolTikTokManagerV13.exe va SYNC_VERSION.ps1
echo ========================================
if /I not "%NOPAUSE%"=="--no-pause" pause
exit /b 1

:versionfail
echo.
echo ========================================
echo LOI: VERSION KHONG HOP LE HOAC KHONG DONG BO DUOC
echo Kiem tra _version\VERSION.txt va SYNC_VERSION.ps1
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
