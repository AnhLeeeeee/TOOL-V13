@echo off
cd /d "%~dp0"
if exist "CAP_NHAT_LIVE_SWITCH_NO_RELOAD.txt" del /q "CAP_NHAT_LIVE_SWITCH_NO_RELOAD.txt" >nul 2>&1
echo Da ap dung source khoi phuc flow ArrowDown ^> cho 2s ^> F5.
echo Hay dong Manager/Worker roi chay BUILD_V13.bat.
pause
