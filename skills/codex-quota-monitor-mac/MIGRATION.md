# Mac 迁移说明

来源：用户提供的 CodexQuotaMonitor Windows 1.0.0，原项目 https://github.com/loverelic0-wq/codex-quota-monitor 。原始文件和说明完整保留在独立 Windows 包；本 Mac 版重新实现为 Swift/AppKit 界面及 Python 标准库只读采集器。

## 使用

解压后双击 CodexQuotaMonitor.app。菜单栏 CQ 可查看详情、手动刷新、调整频率与通知阈值、隐藏浮窗和退出。依赖 Python 3.8+ 和已登录的 Codex。当前附带 Intel App；在 Apple Silicon 上可使用 Rosetta，或安装 Xcode Command Line Tools 后运行 `bash scripts/build.sh` 重编译。App 为本地临时签名，未进行 Apple 公证。系统拒绝运行时可在“系统设置 → 隐私与安全性”允许打开，不需要关闭 Gatekeeper。

单次命令：`python3 scripts/monitor.py snapshot`。

## 已实现

多额度池、真实窗口周期、剩余比例、本地重置时间与倒计时；菜单栏百分比；可拖动置顶浮窗；完整文本详情；低额度通知及去重；失败保留旧结果并退避重试；每轮 Token、独立父子任务及缓存加权汇总；复制历史过滤；未知值保留。

## 和 Windows 的差异

Mac 浮窗改为 72 像素蓝紫渐变圆球（#4D7CFE → #7A5CFF），可拖动并保存位置，点击切换深色详情卡片，右键打开菜单。卡片采用 #1E2028 背景、#E8E8F0 文字、#9A9AB0 次要文字和 #7A9BFF 强调色，与参考 API 用量统计桌面应用一致。没有 Windows 的自动贴边和悬停收缩效果。没有每轮任务完成通知及跨运行持久化任务历史。为避免频繁扫描大日志目录，任务用量每 30 秒更新，扫描最近 7 天内发生修改的日志并取最近 200 个完成轮次；不复制聊天正文或保存任务标题。原版的重置券展示、细粒度通知开关及诊断截图参数未移植。

设置保存在用户偏好 local.codex.quota.monitor；退出后可删除 App 和 skill 目录卸载。没有增加登录启动项、后台服务或网络监听端口。

## 测试

`python3 -m unittest discover -s tests -v` 检查多池与未知额度、独立轮次、父子复制历史、加权缓存、重复轮次及无效数据。

`bash scripts/build.sh` 编译本机架构 App。账户查询依赖安装版本支持 App Server 额度接口及登录可用性；不支持时显示错误，不读取凭据绕过。
