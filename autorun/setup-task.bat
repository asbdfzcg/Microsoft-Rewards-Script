@echo off
REM setup-task.bat - 双击以管理员身份创建计划任务
cd /d "%~dp0"
powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0setup-task.ps1\"' -Wait"
pause
