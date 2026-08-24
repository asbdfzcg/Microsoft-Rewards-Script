# setup-task.ps1 - 创建/覆盖 MicrosoftRewardsScript 计划任务（需管理员权限）
# 触发器：每天 7:00 + 用户登录时；使用 wt.exe(Windows Terminal) 启动，窗口模式由 automation-settings.json 决定

$ErrorActionPreference = 'Stop'
$AutorunDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RunScript  = Join-Path $AutorunDir 'run-rewards.ps1'
$TaskName   = 'MicrosoftRewardsScript'

if (-not (Test-Path $RunScript)) { throw "找不到运行脚本: $RunScript" }

# 不再使用 powershell.exe 直接启动：其 conhost 窗口空白且无法实时显示输出。
# 改为 wt.exe(Windows Terminal) 启动：实时显示输出，窗口状态由 run-rewards.ps1
# 按 automation-settings.json 的 windowMode 控制（silent=隐藏 / minimized=最小化 / normal=正常显示）。
$windowMode = 'silent'   # 默认静默，与原有行为一致
$settingsFile = Join-Path $AutorunDir 'automation-settings.json'
if (Test-Path $settingsFile) {
    try {
        $s = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($s.windowMode) { $windowMode = [string]$s.windowMode }
    } catch {}
}

# 用 Windows Terminal (wt.exe) 启动：新版终端显示实时输出，窗口状态由 run-rewards.ps1 按
# automation-settings.json 的 windowMode 控制（silent=隐藏 / minimized=最小化 / normal=正常显示）。
# 参数与 run-manual.bat 一致（-w 0 强制新建独立窗口，nt = new-tab），便于精确控制本任务的窗口状态。
$execPath = 'wt.exe'
$execArgs = "-w 0 nt powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$RunScript`""

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
