@echo off
REM run-manual.bat - 手动运行 Microsoft Rewards Script（跳过时间/已运行检查）
REM 不使用 wt.exe（Windows Terminal 会在桌面左下角留下可见的 PseudoConsoleWindow 灰色方块），
REM 改用 powershell.exe 直接运行；窗口可见，便于查看实时输出。
cd /d "%~dp0"
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Normal -File "%~dp0run-rewards.ps1" -Force
