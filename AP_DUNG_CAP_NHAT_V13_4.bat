@echo off
setlocal
cd /d "%~dp0"

echo ==============================================
echo  HOAN TAT CAP NHAT SOURCE V13.3 -^> V13.4
echo ==============================================
echo.
echo Dang xoa cac file OCR/image legacy khong con dung...
if exist "V115Core\Services\TesseractOcr.cs" del /f /q "V115Core\Services\TesseractOcr.cs"
if exist "V115Core\Services\ImageMatcher.cs" del /f /q "V115Core\Services\ImageMatcher.cs"

rem Chi la tai lieu cu, xoa neu con de thu muc gon hon.
if exist "CHANGES_V13_3.txt" del /f /q "CHANGES_V13_3.txt"
if exist "CHANGES_V13_3_BUILD_FIX.txt" del /f /q "CHANGES_V13_3_BUILD_FIX.txt"

echo.
echo Da cap nhat source len V13.4 XPath Only / VM Optimized.
echo Khong xoa TikTokProfiles, profiles, profiles.json hay du lieu Chrome.
echo.
echo Hay chay BUILD_V13.bat de build lai.
echo.
pause
endlocal
