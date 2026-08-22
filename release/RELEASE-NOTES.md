# Microsoft Rewards Script v4.3.0

## 更新内容

- **同步上游 4.3.0 登录与活动修复**：
  - 登录流程重写：修复邮箱确认页、无密码（passwordless）登录、代理相关登录问题。
  - 视觉搜索（Visual Search）激活修复，并支持「促销类」视觉搜索。
  - 修复 Turbopack 解析并恢复领取奖励积分（bonus points）。
  - 支持 Bing 子域名，并新增「恢复 Edge 浏览」能力。
  - 改进账号延迟（account delay）的日志与解析。
- **实验性功能**：新增实验性的 Edge 浏览器内浏览（Edge Browsing）活动。
- **管理程序（RewardsManager）与 autorun 工具**（本 fork 特性，延续自 4.2.3）：
  - 主界面状态条显示「今日获得」与「当前积分」，下拉框显示完整邮箱。
  - 静默窗口模式与 Windows 通知。
  - 更新日志链接可点击（点击即在默认浏览器打开）。
  - 适配 4.3.0 新的运行日志格式（`[ACCOUNT-END]` / `[RUN-END]`），积分状态栏与运行成功判定同步更新。

> 修改 `.env` 或 `config.json` 后需重新构建，可在管理程序中重跑「安装依赖并构建」。

**Full Changelog**: https://github.com/asbdfzcg/Microsoft-Rewards-Script/compare/v4.2.3...v4.3.0
