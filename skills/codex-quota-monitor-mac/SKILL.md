---
name: codex-quota-monitor-mac
description: 在 macOS 查询 Codex 账户剩余额度、重置时间、每轮 Token 和缓存命中率，或启动 Codex 用量统计菜单栏及悬浮窗监控。
---

# Codex 用量统计（Mac）

所有相对路径以本 skill 目录为基准。支持 macOS 12+、Python 3.8+；附带本机 Intel 编译的 App，Apple Silicon 可用 Rosetta 或通过 `bash scripts/build.sh` 原生重编译（需 Xcode Command Line Tools）。

## 单次查询

- 账户额度：`python3 scripts/monitor.py quota`。
- 每轮用量和缓存命中率：`python3 scripts/monitor.py tasks`。
- 两者一起：`python3 scripts/monitor.py snapshot`。
- 查询本地任务日志时遵循 `CODEX_HOME`，否则用 `~/.codex`。`--home PATH` 仅覆盖日志目录。
- 优先复用可用的 Codex 内置 `get_usage_limits` 工具完成简单额度查询；工具不可用或需独立运行时用脚本。

## 持续监控

用户要求监控、悬浮窗或打开统计工具时运行 `open CodexQuotaMonitor.app`。菜单栏 `CQ` 提供详情、刷新、悬浮窗显隐、刷新频率、低额度阈值和退出；桌面为 72 像素蓝紫渐变圆形悬浮球（#4D7CFE → #7A5CFF），显示剩余百分比；单击展开／收起深色详情卡片，拖动移动并保存位置，右键打开设置菜单。详情配色为 #1E2028 背景、#E8E8F0 文字、#7A9BFF 强调色，沿用用户指定的 API 用量统计应用风格。账户额度默认每 5 分钟刷新，任务日志每 30 秒检查。仅启动当前用户会话，不自动配置开机启动或定时任务。

若 App 缺失或架构不兼容，运行 `bash scripts/build.sh` 后重试；未安装编译工具时说明依赖，不自动安装工具链。Windows 原程序的自动贴边、悬停收缩、每轮完成弹窗和持久历史未移植；Mac 使用可拖动圆形悬浮球、深色详情卡片、菜单栏、低额度系统通知及即时日志统计。

## 数据口径与边界

- 查询只发送 `initialize`、`initialized`、`account/rateLimits/read`，复用 Codex 登录；不读取认证文件，不创建模型任务，不消费重置券、不购买额度。
- 优先多额度池映射，兼容旧版单池；剩余比例为 `100-usedPercent`，限制在 0–100。没有字段就是未知，不能当作零。周期采用接口真实分钟数，重置时间用本机时区。
- 失败保留上次额度并标明待更新；倒计时归零不自行恢复满额。失败按 1、2、4、8、15 分钟重试。低额度通知需要 macOS 通知授权，同一窗口同一重置周期去重。
- 每轮统计扫描最近 7 天内修改的 sessions / archived_sessions 日志，按 thread+turn 去重，最多 200 条完成轮次。只采用明确属于当前线程的 `turn_token_usage`，不把一次响应或会话累计冒充本轮消耗。旧日志没有独立轮次字段时显示未知。
- 父子任务独立；过滤复制的继承历史。缓存命中已计入输入、推理已计入输出。汇总按有效输入量加权；无效和零输入单独排除。它不是全账户所有设备的统计，也不能换算成订阅金额或任务独占额度。
- 不持久化聊天正文、凭据或账户额度；App 设置和浮窗位置保存在 macOS 用户偏好 `local.codex.quota.monitor`。
- 如未找到 Codex，可用 `CODEX_BINARY` 指定本机 CLI；App 通过 `QUOTA_PYTHON` 或常见安装路径寻找 Python。API Key 登录可能没有订阅额度；不要索要用户 token。

实现依据：[官方 App Server 文档](https://learn.chatgpt.com/docs/app-server)。原项目与移植差异见 [迁移说明](MIGRATION.md)。
