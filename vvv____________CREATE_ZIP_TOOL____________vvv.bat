@echo off
setlocal EnableExtensions
cd /d "%~dp0"

for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%I"
set "OUT=%CD%\ToolTikTok_SOURCE_LITE_%STAMP%.zip"

echo ==========================================
echo   NEN SOURCE LITE - CHI LAY SOURCE CAN THIET
echo ==========================================
echo.

REM Allow-list: chi nen cac thu muc source duoi day.
REM Cac thu muc output nhu publish/dist/RELEASE_OUTPUT/SETUP_OUTPUT
REM se KHONG bao gio bi dua vao ZIP vi khong nam trong danh sach.

tar.exe -a -c -f "%OUT%" ^
  --exclude="*/bin/*" ^
  --exclude="*/obj/*" ^
  --exclude="*/.vs/*" ^
  --exclude="*/TestResults/*" ^
  --exclude="*.pdb" ^
  --exclude="*.cache" ^
  --exclude="*.user" ^
  --exclude="*.suo" ^
  "_BAT_PHU" ^
  "_PATCH_PAYLOAD" ^
  "_version" ^
  "defaults" ^
  "ManagerShared" ^
  "QITool_Admin_Server" ^
  "ToolTikTokManagerV13" ^
  "ToolTikTokWorkerV13" ^
  "V115Core" ^
  ".gitignore" ^
  "BUILD_RUN_V13.bat" ^
  "device_access_policy.json" ^
  "Directory.Build.props" ^
  "SERVER_VERSION_POLICY_CONTRACT.md" ^
  "TAO_CAP_NHAT_V13_UI.bat" ^
  "ToolTikTok_V13_5.iss" ^
  "ToolTikTokV13.sln" ^
  "version.json" ^
  "versions.json"

if errorlevel 1 (
    echo.
    echo [LOI] Khong tao duoc ZIP. Kiem tra xem cac thu muc source co bi doi ten khong.
    if exist "%OUT%" del /q "%OUT%" >nul 2>&1
    pause
    exit /b 1
)

for %%A in ("%OUT%") do set "SIZE=%%~zA"
powershell -NoProfile -Command "$n=%SIZE%; if($n -ge 1GB){'{0:N2} GB' -f ($n/1GB)} elseif($n -ge 1MB){'{0:N2} MB' -f ($n/1MB)} else {'{0:N2} KB' -f ($n/1KB)}"

echo.
echo XONG:
echo %OUT%
echo.
explorer.exe /select,"%OUT%"
pause
