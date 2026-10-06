@echo off
setlocal EnableExtensions
cd /d "%~dp0"

title BUILD + RUN ALL - V13 + COMMENT CHECK

echo ================================================
echo   BUILD + RUN ALL - COMMENT CHECK + TOOL V13
echo ================================================
echo.

rem Tim file build Comment Check. Uu tien ten chuan, sau do cac ten co suffix.
set "COMMENT_BAT="
if exist ".\BUILD_RUN_COMMENT_CHECK.bat" set "COMMENT_BAT=.\BUILD_RUN_COMMENT_CHECK.bat"
if not defined COMMENT_BAT if exist ".\BUILD_RUN_COMMENT_CHECK(2).bat" set "COMMENT_BAT=.\BUILD_RUN_COMMENT_CHECK(2).bat"
if not defined COMMENT_BAT if exist ".\BUILD_RUN_COMMENT_CHECK(1).bat" set "COMMENT_BAT=.\BUILD_RUN_COMMENT_CHECK(1).bat"

rem Tim file build V13. Uu tien ten chuan, sau do cac ten co suffix.
set "V13_BAT="
if exist ".\BUILD_RUN_V13.bat" set "V13_BAT=.\BUILD_RUN_V13.bat"
if not defined V13_BAT if exist ".\BUILD_RUN_V13(1).bat" set "V13_BAT=.\BUILD_RUN_V13(1).bat"
if not defined V13_BAT if exist ".\BUILD_RUN_V13(2).bat" set "V13_BAT=.\BUILD_RUN_V13(2).bat"

if not defined COMMENT_BAT (
    echo [LOI] Khong tim thay BUILD_RUN_COMMENT_CHECK.bat
    echo Hay dat file nay cung thu muc voi 2 file BUILD_RUN.
    echo.
    pause
    exit /b 1
)

if not defined V13_BAT (
    echo [LOI] Khong tim thay BUILD_RUN_V13.bat
    echo Hay dat file nay cung thu muc voi 2 file BUILD_RUN.
    echo.
    pause
    exit /b 1
)

echo [1/2] BUILD + RUN COMMENT CHECK
echo File: %COMMENT_BAT%
echo ------------------------------------------------
call "%COMMENT_BAT%"
set "RC_COMMENT=%ERRORLEVEL%"
if not "%RC_COMMENT%"=="0" goto :comment_fail

echo.
echo [OK] COMMENT CHECK thanh cong.
echo.

echo [2/2] BUILD + RUN TOOL V13
echo File: %V13_BAT%
echo ------------------------------------------------
call "%V13_BAT%"
set "RC_V13=%ERRORLEVEL%"
if not "%RC_V13%"=="0" goto :v13_fail

echo.
echo ================================================
echo   BUILD ALL THANH CONG
echo   - Comment Check: OK
echo   - Tool V13:      OK
echo ================================================
echo.
echo Nhan phim bat ky de dong cua so nay.
pause >NUL
exit /b 0

:comment_fail
echo.
echo ================================================
echo BUILD ALL THAT BAI
echo COMMENT CHECK bi loi. ErrorLevel=%RC_COMMENT%
echo V13 chua duoc build.
echo ================================================
pause
exit /b %RC_COMMENT%

:v13_fail
echo.
echo ================================================
echo BUILD ALL THAT BAI
echo COMMENT CHECK: OK
echo TOOL V13 bi loi. ErrorLevel=%RC_V13%
echo ================================================
pause
exit /b %RC_V13%
