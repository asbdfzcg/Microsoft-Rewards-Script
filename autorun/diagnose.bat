@echo off
REM diagnose.bat - 双击运行诊断（申请管理员权限以读取计划任务历史）
cd /d "%~dp0"
powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0diagnose.ps1\"'"
