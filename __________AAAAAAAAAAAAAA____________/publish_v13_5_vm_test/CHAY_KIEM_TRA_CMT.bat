@echo off
cd /d "%~dp0CommentCheck"
if not exist ".\CommentVisibilityMonitor.exe" (
  echo KHONG TIM THAY CommentVisibilityMonitor.exe
  pause
  exit /b 1
)
start "" ".\CommentVisibilityMonitor.exe"
