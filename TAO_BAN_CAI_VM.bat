@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "OUT=%CD%\publish_v13_4_vm"
set "ZIP=%CD%\ToolTikTok_V13.4.1_VM_CLIENT_WIN_X64.zip"

if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%ZIP%" del /q "%ZIP%"
mkdir "%OUT%"

echo ========================================
echo TAO BAN MAY AO TOOL TIKTOK V13.4.1
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

> "%OUT%\CHAY_TOOL_V13_4.bat" echo @echo off
>>"%OUT%\CHAY_TOOL_V13_4.bat" echo cd /d "%%~dp0"
>>"%OUT%\CHAY_TOOL_V13_4.bat" echo start "" ".\ToolTikTokManagerV13.exe"

> "%OUT%\README_MAY_AO.txt" echo TOOL TIKTOK V13.4.1 VM CLIENT
>>"%OUT%\README_MAY_AO.txt" echo - Khong can cai .NET 8.
>>"%OUT%\README_MAY_AO.txt" echo - Khong can cai Tesseract/OCR.
>>"%OUT%\README_MAY_AO.txt" echo - Can Google Chrome.
>>"%OUT%\README_MAY_AO.txt" echo - Chay CHAY_TOOL_V13_4.bat hoac ToolTikTokManagerV13.exe.
>>"%OUT%\README_MAY_AO.txt" echo - Profile Chrome: TikTokProfiles\ten_profile\chrome_profile

mkdir "%OUT%\TikTokProfiles" >nul 2>&1
mkdir "%OUT%\profiles" >nul 2>&1

echo.
echo [3/3] Nen ZIP may ao...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -CompressionLevel Optimal -Force"
if errorlevel 1 goto :fail

echo.
echo ========================================
echo HOAN TAT
echo %ZIP%
echo ========================================
echo.
pause
exit /b 0

:fail
echo.
echo ========================================
echo TAO BAN MAY AO THAT BAI
echo ========================================
echo Kiem tra phan loi phia tren.
pause
exit /b 1
