@echo off
REM run-manual.bat - 手动运行 Microsoft Rewards Script（跳过时间/已运行检查）
REM 使用 wt.exe（Windows Terminal）运行，窗口可见，便于查看实时输出。
cd /d "%~dp0"
start "" wt.exe -w 0 nt powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-rewards.ps1" -Force
