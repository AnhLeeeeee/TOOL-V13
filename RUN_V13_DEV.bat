@echo off
setlocal
cd /d "%~dp0"
call BUILD_V13.bat --no-pause
if errorlevel 1 (
  echo Build V13 that bai. Nhan phim bat ky de dong.
  pause >nul
  exit /b 1
)
start "" ".\dist_v13\ToolTikTokManagerV13.exe"
