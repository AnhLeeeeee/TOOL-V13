@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

echo ============================================================
echo   CREATE SOURCE ZIP - SAFE V4
echo ============================================================
echo.

set "ROOT=%CD%"

REM ===== 1. CHECK REQUIRED SOURCE FOLDERS =====
set "MISSING=0"
call :REQ "ToolTikTokManagerV13"
call :REQ "ToolTikTokWorkerV13"
call :REQ "V115Core"
call :REQ "ManagerShared"
call :REQ "CommentVisibilityMonitor"

if "!MISSING!"=="1" (
    echo.
    echo [DUNG] Thieu source quan trong. Khong tao ZIP.
    pause
    exit /b 2
)

for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%I"
set "TMP=%TEMP%\ToolTikTok_SourceLite_!RANDOM!_!RANDOM!"
set "OUT=%ROOT%\ToolTikTok_SOURCE_LITE_!STAMP!.zip"

if exist "!TMP!" rmdir /s /q "!TMP!" >nul 2>&1
mkdir "!TMP!" >nul 2>&1

echo.
echo [1/3] Gom source can thiet...

REM ===== 2. COPY REQUIRED SOURCE, EXCLUDING BUILD OUTPUT =====
call :COPYDIR "ToolTikTokManagerV13"
if errorlevel 1 goto :fail
call :COPYDIR "ToolTikTokWorkerV13"
if errorlevel 1 goto :fail
call :COPYDIR "V115Core"
if errorlevel 1 goto :fail
call :COPYDIR "ManagerShared"
if errorlevel 1 goto :fail
call :COPYDIR "CommentVisibilityMonitor"
if errorlevel 1 goto :fail

REM Optional source/support folders
call :COPYDIR_OPTIONAL "_BAT_PHU"
call :COPYDIR_OPTIONAL "_PATCH_PAYLOAD"
call :COPYDIR_OPTIONAL "_version"
call :COPYDIR_OPTIONAL "defaults"
call :COPYDIR_OPTIONAL "QITool_Admin_Server"

REM Copy root files automatically, but never nest old ZIP archives.
for %%F in (*) do (
    if /I not "%%~xF"==".zip" (
        copy /y "%%F" "!TMP!\%%~nxF" >nul 2>&1
    )
)

echo [2/3] Nen ZIP...
if exist "!OUT!" del /q "!OUT!" >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Compress-Archive -Path '!TMP!\*' -DestinationPath '!OUT!' -CompressionLevel Optimal -Force"
if errorlevel 1 goto :fail

echo [3/3] Kiem tra ZIP...

REM ROOT is explicitly exported by SET above, so PowerShell can safely use $env:ROOT.
set "ZIP_VERIFY=!OUT!"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem; $z=[IO.Compression.ZipFile]::OpenRead($env:ZIP_VERIFY); try { $n=@($z.Entries | ForEach-Object {$_.FullName.Replace('\','/')}); $req=@('ToolTikTokManagerV13/','ToolTikTokWorkerV13/','V115Core/','ManagerShared/','CommentVisibilityMonitor/'); $must=@('ToolTikTokV13.sln','BUILD_RUN_V13.bat','ToolTikTok_V13_5.iss','version.json','versions.json'); $m=@(); foreach($r in $req){ if(-not ($n | Where-Object { $_ -like ($r+'*') } | Select-Object -First 1)){ $m += $r } }; foreach($f in $must){ if((Test-Path -LiteralPath (Join-Path $env:ROOT $f)) -and -not ($n -contains $f)){ $m += $f } }; if($m.Count -gt 0){ Write-Host '[LOI] ZIP THIEU:'; $m | ForEach-Object { Write-Host ('  - '+$_) }; exit 3 }; Write-Host '[OK] ZIP da co du source va file goc quan trong.' } finally { $z.Dispose() }"

if errorlevel 1 goto :badzip

for %%A in ("!OUT!") do set "SIZE=%%~zA"

echo.
powershell -NoProfile -Command "$n=[double]$env:SIZE; if($n -ge 1GB){'Dung luong ZIP: {0:N2} GB' -f ($n/1GB)} elseif($n -ge 1MB){'Dung luong ZIP: {0:N2} MB' -f ($n/1MB)} else {'Dung luong ZIP: {0:N2} KB' -f ($n/1KB)}"

rmdir /s /q "!TMP!" >nul 2>&1

echo.
echo ============================================================
echo XONG - ZIP DA DUOC KIEM TRA
echo !OUT!
echo ============================================================
echo.
explorer.exe /select,"!OUT!"
pause
exit /b 0

:REQ
if exist "%~1\" (
    echo [OK]    %~1
) else (
    echo [THIEU] %~1
    set "MISSING=1"
)
exit /b 0

:COPYDIR
robocopy "%~1" "!TMP!\%~1" /E /R:0 /W:0 ^
  /XD bin obj .vs TestResults ^
  /XF *.pdb *.cache *.user *.suo >nul
set "RC=!ERRORLEVEL!"
if !RC! GEQ 8 exit /b 1
exit /b 0

:COPYDIR_OPTIONAL
if not exist "%~1\" exit /b 0
call :COPYDIR "%~1"
exit /b 0

:badzip
echo.
echo [DUNG] ZIP tao ra bi thieu file. Da xoa ZIP loi.
if exist "!OUT!" del /q "!OUT!" >nul 2>&1
if exist "!TMP!" rmdir /s /q "!TMP!" >nul 2>&1
pause
exit /b 3

:fail
echo.
echo [LOI] Khong tao duoc ZIP.
if exist "!OUT!" del /q "!OUT!" >nul 2>&1
if exist "!TMP!" rmdir /s /q "!TMP!" >nul 2>&1
pause
exit /b 1
