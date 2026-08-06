# setup-task.ps1 - 创建/覆盖 MicrosoftRewardsScript 计划任务（需管理员权限）
# 触发器：每天 7:00 + 用户登录时；使用 Windows Terminal 启动并自动最小化

$ErrorActionPreference = 'Stop'
$AutorunDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RunScript  = Join-Path $AutorunDir 'run-rewards.ps1'
$TaskName   = 'MicrosoftRewardsScript'

if (-not (Test-Path $RunScript)) { throw "找不到运行脚本: $RunScript" }

# 查找 wt.exe（Windows Terminal）
$wtPath = (Get-Command wt.exe -ErrorAction SilentlyContinue).Source
if (-not $wtPath) {
    $wtPath = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wt.exe'
}
$useWt = Test-Path $wtPath

if ($useWt) {
    $execPath = $wtPath
    $execArgs = "--title `"Microsoft Rewards Script`" -- powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File `"$RunScript`""
} else {
    Write-Host "未找到 Windows Terminal，回退到 PowerShell 窗口" -ForegroundColor Yellow
    $execPath = 'powershell.exe'
    $execArgs = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File `"$RunScript`""
}

# 触发器 1：每天 7:00
$triggerDaily = New-ScheduledTaskTrigger -Daily -At '07:00'
# 触发器 2：用户登录时（覆盖关机/睡眠后登录场景）
$triggerLogon = New-ScheduledTaskTrigger -AtLogOn

$action = New-ScheduledTaskAction -Execute $execPath -Argument $execArgs -WorkingDirectory $AutorunDir

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -WakeToRun `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 10) `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -MultipleInstances IgnoreNew

# 若已存在旧任务（可能指向旧路径），先注销再重建
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "发现已存在的计划任务，执行覆盖重建..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger @($triggerDaily, $triggerLogon) `
    -Settings $settings `
    -RunLevel Highest `
    -Description "每天 7:00 自动运行 Microsoft Rewards Script（登录时补跑），失败每 10 分钟重试最多 3 次" | Out-Null

Write-Host ""
Write-Host "计划任务创建成功！" -ForegroundColor Green
Write-Host "  任务名称: $TaskName"
Write-Host "  触发器:   每天 07:00 + 用户登录时"
Write-Host "  执行程序: $execPath"
Write-Host "  运行脚本: $RunScript"
Write-Host ""
Write-Host "手动测试命令: Start-ScheduledTask -TaskName '$TaskName'"
