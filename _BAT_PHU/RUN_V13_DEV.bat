@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "ROOT=%%~fI"
cd /d "%ROOT%"

call "%SCRIPT_DIR%BUILD_V13.bat" --no-pause
if errorlevel 1 (
  echo Build V13 that bai. Nhan phim bat ky de dong.
  pause >nul
  exit /b 1
)
start "" "%ROOT%\dist_v13\ToolTikTokManagerV13.exe"
