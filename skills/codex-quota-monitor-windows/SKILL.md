---
name: codex-quota-monitor-windows
description: 在 Windows 启动或使用 CodexQuotaMonitor，查看 Codex 共享额度、重置时间、每轮任务 Token、缓存命中率和贴边悬浮窗。
---

# Codex 用量统计（Windows）

本包包含基于用户提供的 CodexQuotaMonitor Windows 1.0.0 修改的 1.1.0 蓝紫悬浮球程序、源码和测试，原文件位于 `assets/CodexQuotaMonitor/`。只在 Windows 使用此版本；macOS 应使用 codex-quota-monitor-mac。

- 启动监控：运行 `assets/CodexQuotaMonitor/CodexQuotaMonitor.exe` 或 `Start.cmd`。路径有空格时正确引用；PowerShell 可用 `Start-Process -FilePath <完整exe路径>`。
- 环境：Windows 10/11、.NET Framework 4.8，本机 Codex 已用 ChatGPT 账户登录。程序自动发现 Windows Codex CLI。
- 具体操作、统计口径、通知和诊断参数请读取 [原版说明](assets/CodexQuotaMonitor/README.md)。
- 默认每 5 分钟查额度，每 5 秒检查本机轮次记录；托盘右键可退出。不得将关闭面板误认为退出。
- 只读查询额度，不创建模型任务、不消费重置券，不索要或复制凭据。缺失字段显示未知；缓存命中包含在输入内，推理包含在输出内。Token 不换算为任务独占订阅额度或金额。
- 不自动设置开机启动。只有用户要求时创建桌面快捷方式。

外观与 Mac 版同步：72px 圆球，#4D7CFE → #7A5CFF 渐变；深色详情与任务窗口。单击展开／收起，右键设置；保留 Windows 拖动贴边及原有任务提醒。程序在 Mac 上交叉编译，Windows GUI 及系统通知未在本机实测。
