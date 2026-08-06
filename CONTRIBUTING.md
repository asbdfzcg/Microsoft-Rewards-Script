# 贡献指南

感谢你对本项目的关注！

## 项目定位

本仓库是 [TheNetsky/Microsoft-Rewards-Script](https://github.com/TheNetsky/Microsoft-Rewards-Script) 的维护分支（`v4`），主要面向中文 Windows 用户，额外提供 **RewardsManager 图形化管理程序** 与 **autorun 一键运行工具**。

- 若修改涉及核心积分任务逻辑（Bing / Microsoft Rewards 自动化），建议先向上游提交 PR。
- 若修改针对 RewardsManager GUI、autorun 脚本、中文本地化、发布打包流程，欢迎直接在本仓库提交 PR。

## 提交前请确认

1. 在本地测试通过：RewardsManager 能正常启动、配置、构建和运行。
2. 提交信息使用中文或英文，简要说明改动内容。
3. 不要提交 `node_modules/`、`dist/`、`sessions/` 等构建产物（已加入 `.gitignore`）。

## 报告问题

请使用 GitHub Issues，并尽量提供：
- 操作系统版本
- Node.js 版本
- 复现步骤
- 相关错误截图或日志
