@echo off
REM run-manual.bat - 手动运行 Microsoft Rewards Script（跳过时间/已运行检查）
cd /d "%~dp0"
where wt.exe >nul 2>nul
if %errorlevel%==0 (
    start "" wt.exe -- powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-rewards.ps1" -Force
) else (
    start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-rewards.ps1" -Force
)
