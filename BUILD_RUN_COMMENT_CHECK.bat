@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ========================================
echo BUILD + RUN COMMENT VISIBILITY MONITOR
echo ========================================
echo.

REM Neu monitor dang chay, dung ban cu truoc de tranh lock file khi build.
tasklist /FI "IMAGENAME eq CommentVisibilityMonitor.exe" 2>NUL | find /I "CommentVisibilityMonitor.exe" >NUL
if not errorlevel 1 (
    echo Dang dung CommentVisibilityMonitor dang chay...
    taskkill /IM "CommentVisibilityMonitor.exe" /F >NUL 2>&1
    timeout /t 1 /nobreak >NUL
)

echo [1/2] Dang build...
dotnet build ".\CommentVisibilityMonitor\CommentVisibilityMonitor.csproj" -c Release
if errorlevel 1 goto :fail

if not exist ".\dist_comment_check\CommentVisibilityMonitor.exe" (
    echo.
    echo [LOI] Build xong nhung khong tim thay:
    echo %CD%\dist_comment_check\CommentVisibilityMonitor.exe
    pause
    exit /b 1
)

echo.
echo [2/2] Dang mo Comment Visibility Monitor...
start "" ".\dist_comment_check\CommentVisibilityMonitor.exe"

echo.
echo BUILD + RUN OK
timeout /t 2 /nobreak >NUL
exit /b 0

:fail
echo.
echo BUILD FAILED
pause
exit /b 1
