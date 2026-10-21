import AppKit
import UserNotifications

enum Palette {
    static let blue = NSColor(srgbRed: 77/255, green: 124/255, blue: 254/255, alpha: 1)
    static let purple = NSColor(srgbRed: 122/255, green: 92/255, blue: 255/255, alpha: 1)
    static let card = NSColor(srgbRed: 30/255, green: 32/255, blue: 40/255, alpha: 0.96)
    static let text = NSColor(srgbRed: 232/255, green: 232/255, blue: 240/255, alpha: 1)
    static let muted = NSColor(srgbRed: 154/255, green: 154/255, blue: 176/255, alpha: 1)
    static let accent = NSColor(srgbRed: 122/255, green: 155/255, blue: 255/255, alpha: 1)
}
final class QuotaBall: NSView {
    var value = "—" { didSet { needsDisplay = true } }
    var subtitle = "剩余额度" { didSet { needsDisplay = true } }
    var onClick: (() -> Void)?
    var onMenu: ((NSEvent) -> Void)?
    private var startMouse = NSPoint.zero
    private var startOrigin = NSPoint.zero
    private var moved = false
    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.saveGraphicsState()
        let shape = NSBezierPath(ovalIn: NSRect(x: 4, y: 4, width: 72, height: 72))
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = 8
        shadow.shadowOffset = NSSize(width: 0, height: -2)
        shadow.set()
        Palette.blue.setFill()
        shape.fill()
        NSGraphicsContext.restoreGraphicsState()
        NSGradient(starting: Palette.blue, ending: Palette.purple)?.draw(in: shape, angle: -45)
        let centered = NSMutableParagraphStyle()
        centered.alignment = .center
        let number: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 15, weight: .bold), .foregroundColor: NSColor.white, .paragraphStyle: centered]
        let caption: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 10), .foregroundColor: NSColor.white.withAlphaComponent(0.85), .paragraphStyle: centered]
        (value as NSString).draw(in: NSRect(x: 8, y: 36, width: 64, height: 20), withAttributes: number)
        (subtitle as NSString).draw(in: NSRect(x: 8, y: 23, width: 64, height: 14), withAttributes: caption)
    }
    override func hitTest(_ point: NSPoint) -> NSView? {
        return hypot(point.x - 40, point.y - 40) <= 36 ? self : nil
    }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .openHand) }
    override func mouseDown(with event: NSEvent) {
        if event.modifierFlags.contains(.control) { onMenu?(event); return }
        startMouse = NSEvent.mouseLocation
        startOrigin = window?.frame.origin ?? .zero
        moved = false
    }
    override func mouseDragged(with event: NSEvent) {
        let delta = NSSize(width: NSEvent.mouseLocation.x - startMouse.x, height: NSEvent.mouseLocation.y - startMouse.y)
        if abs(delta.width) + abs(delta.height) > 4 { moved = true }
        if moved { window?.setFrameOrigin(NSPoint(x: startOrigin.x + delta.width, y: startOrigin.y + delta.height)) }
    }
    override func mouseUp(with event: NSEvent) {
        if moved {
            if let window = window, let screen = window.screen {
                let area = screen.visibleFrame
                let origin = NSPoint(x: min(max(window.frame.minX, area.minX), area.maxX - 80), y: min(max(window.frame.minY, area.minY), area.maxY - 80))
                window.setFrameOrigin(origin)
            }
            window?.saveFrame(usingName: "QuotaBall")
        } else { onClick?() }
    }
    override func rightMouseDown(with event: NSEvent) { onMenu?(event) }
}
final class DetailWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}
final class DockPanel: NSPanel {
    override var canBecomeKey: Bool { false }
}
final class Delegate: NSObject, NSApplicationDelegate {
    var item: NSStatusItem!
    var panel: DockPanel!
    var window: NSWindow!
    var detail: NSTextView!
    var ball: QuotaBall!
    var quota: [String: Any] = [:]
    var taskData: [String: Any] = [:]
    var busyQuota = false, busyTasks = false
    var nextTasks = Date.distantPast
    var failed = false, failures = 0
    var nextQuota = Date.distantPast
    var alerted = Set<String>()
    var timer: Timer?
    var interval: Double { let v = UserDefaults.standard.double(forKey: "interval"); return v > 0 ? v : 300 }
    var threshold: Double { let v = UserDefaults.standard.double(forKey: "threshold"); return v > 0 ? v : 20 }
    func applicationDidFinishLaunching(_ notification: Notification) {
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "local.codex.quota.monitor")
        if others.count > 1 && !CommandLine.arguments.contains("--preview") { NSApp.terminate(nil); return }
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.title = "CQ —"
        makeMenu()
        let screen = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        panel = DockPanel(contentRect: NSRect(x: screen.maxX - 96, y: screen.midY, width: 80, height: 80), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "Codex 用量"
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.level = .floating
        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        _ = panel.setFrameUsingName("QuotaBall")
        panel.setContentSize(NSSize(width: 80, height: 80))
        ball = QuotaBall(frame: NSRect(x: 0, y: 0, width: 80, height: 80))
        ball.onClick = { [weak self] in
            guard let self = self else { return }
            if self.window.isVisible { self.window.orderOut(nil) } else { self.showDetails() }
        }
        ball.onMenu = { [weak self] event in
            guard let self = self, let menu = self.item.menu else { return }
            NSMenu.popUpContextMenu(menu, with: event, for: self.ball)
        }
        panel.contentView = ball
        panel.orderFrontRegardless()
        window = DetailWindow(contentRect: NSRect(x: 0, y: 0, width: 420, height: 560), styleMask: [.borderless], backing: .buffered, defer: false)
        window.title = "Codex 额度与每轮用量"
        window.isReleasedWhenClosed = false
        window.backgroundColor = .clear
        window.isOpaque = false
        window.hasShadow = true
        window.level = .floating
        window.isMovableByWindowBackground = true
        let card = NSView(frame: NSRect(x: 0, y: 0, width: 420, height: 560))
        card.wantsLayer = true
        card.layer?.cornerRadius = 16
        card.layer?.backgroundColor = Palette.card.cgColor
        card.layer?.masksToBounds = true
        window.contentView = card
        let title = NSTextField(labelWithString: "Codex 用量统计")
        title.frame = NSRect(x: 16, y: 520, width: 260, height: 24)
        title.font = .systemFont(ofSize: 16, weight: .semibold)
        title.textColor = Palette.text
        card.addSubview(title)
        let close = NSButton(title: "×", target: self, action: #selector(hideDetails))
        close.frame = NSRect(x: 380, y: 520, width: 24, height: 24)
        close.isBordered = false
        close.font = .systemFont(ofSize: 22)
        close.contentTintColor = Palette.muted
        card.addSubview(close)
        let refreshButton = NSButton(title: "刷新", target: self, action: #selector(refresh))
        refreshButton.frame = NSRect(x: 324, y: 520, width: 50, height: 24)
        refreshButton.isBordered = false
        refreshButton.contentTintColor = Palette.accent
        card.addSubview(refreshButton)
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 12, width: 420, height: 498))
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        scroll.scrollerStyle = .overlay
        detail = NSTextView(frame: scroll.bounds)
        detail.isEditable = false
        detail.isSelectable = true
        detail.drawsBackground = false
        detail.textColor = Palette.text
        detail.insertionPointColor = Palette.accent
        detail.textContainerInset = NSSize(width: 16, height: 8)
        detail.autoresizingMask = [.width]
        detail.isVerticallyResizable = true
        detail.isHorizontallyResizable = false
        detail.textContainer?.widthTracksTextView = true
        scroll.documentView = detail
        card.addSubview(scroll)
        if let index = CommandLine.arguments.firstIndex(of: "--preview"), CommandLine.arguments.count > index + 1 {
            let previewWindows: [[String: Any]] = [["key": "primary", "remaining": 92, "minutes": 300], ["key": "secondary", "remaining": 98, "minutes": 10080]]
            let previewBucket: [String: Any] = ["id": "codex", "name": "Codex", "windows": previewWindows]
            quota = ["buckets": [previewBucket]]
            taskData = ["cache": ["percent": 96.8, "included": 173, "missing": 27, "zero": 0]]
            render()
            let folder = URL(fileURLWithPath: CommandLine.arguments[index + 1])
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            for (view, name) in [(ball! as NSView, "ball"), (card, "panel")] {
                if let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) {
                    view.cacheDisplay(in: view.bounds, to: rep)
                    try? rep.representation(using: .png, properties: [:])?.write(to: folder.appendingPathComponent(name + ".png"))
                }
            }
            NSApp.terminate(nil)
            return
        }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
        tick()
        timer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in self?.tick() }
    }
    func makeMenu() {
        let menu = NSMenu()
        menu.addItem(withTitle: "查看额度与任务用量", action: #selector(showDetails), keyEquivalent: "")
        menu.addItem(withTitle: "立即刷新", action: #selector(refresh), keyEquivalent: "r")
        menu.addItem(withTitle: "显示／隐藏悬浮窗", action: #selector(toggle), keyEquivalent: "")
        let refreshMenu = NSMenu()
        for mins in [1, 5, 10, 15] {
            let entry = NSMenuItem(title: "\(mins) 分钟", action: #selector(setInterval(_:)), keyEquivalent: "")
            entry.tag = mins
            entry.state = interval == Double(mins * 60) ? .on : .off
            refreshMenu.addItem(entry)
        }
        let settings = NSMenuItem(title: "刷新频率", action: nil, keyEquivalent: "")
        settings.submenu = refreshMenu
        menu.addItem(settings)
        let thresholds = NSMenu()
        for percent in [10, 20, 30, 50] {
            let entry = NSMenuItem(title: "\(percent)%", action: #selector(setThreshold(_:)), keyEquivalent: "")
            entry.tag = percent
            entry.state = threshold == Double(percent) ? .on : .off
            thresholds.addItem(entry)
        }
        let level = NSMenuItem(title: "低额度提醒阈值", action: nil, keyEquivalent: "")
        level.submenu = thresholds
        menu.addItem(level)
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出监控", action: #selector(quit), keyEquivalent: "q")
        for entry in menu.items { entry.target = self }
        for entry in refreshMenu.items + thresholds.items { entry.target = self }
        item.menu = menu
    }
    @objc func setInterval(_ sender: NSMenuItem) { UserDefaults.standard.set(sender.tag * 60, forKey: "interval"); makeMenu(); refresh() }
    @objc func setThreshold(_ sender: NSMenuItem) { UserDefaults.standard.set(sender.tag, forKey: "threshold"); alerted.removeAll(); makeMenu(); render() }
    @objc func hideDetails() { window.orderOut(nil) }
    @objc func showDetails() {
        render()
        if let screen = panel.screen ?? NSScreen.main {
            let area = screen.visibleFrame
            let x = panel.frame.minX >= area.midX ? panel.frame.minX - 428 : panel.frame.maxX + 8
            let y = min(max(panel.frame.maxY - 560, area.minY), area.maxY - 560)
            window.setFrameOrigin(NSPoint(x: min(max(x, area.minX), area.maxX - 420), y: y))
        }
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
    @objc func toggle() { if panel.isVisible { panel.orderOut(nil) } else { panel.orderFrontRegardless() } }
    @objc func quit() { NSApp.terminate(nil) }
    @objc func refresh() { nextQuota = .distantPast; tick() }
    func tick() {
        if !busyQuota && Date() >= nextQuota {
            busyQuota = true
            run("quota") { data in
                self.busyQuota = false
                self.failed = data["error"] != nil
                if self.failed {
                    self.failures += 1
                    self.quota["error"] = data["error"]
                    self.nextQuota = Date().addingTimeInterval(min(900, 60 * pow(2, Double(min(4, self.failures - 1)))))
                } else {
                    self.failures = 0
                    self.quota = data
                    self.nextQuota = Date().addingTimeInterval(self.interval)
                }
                self.render()
            }
        }
        if !busyTasks && Date() >= nextTasks {
            nextTasks = Date().addingTimeInterval(30)
            busyTasks = true
            run("tasks") { data in self.busyTasks = false; self.taskData = data; self.render() }
        }
        render()
    }
    func run(_ command: String, completion: @escaping ([String: Any]) -> Void) {
        DispatchQueue.global(qos: .utility).async {
            var data: [String: Any] = [:]
            let process = Process()
            let output = Pipe()
            let resources = Bundle.main.resourceURL!
            let paths = [ProcessInfo.processInfo.environment["QUOTA_PYTHON"] ?? "", "/opt/homebrew/bin/python3", "/usr/local/bin/python3", "/usr/bin/python3"]
            guard let python = paths.first(where: { !$0.isEmpty && FileManager.default.isExecutableFile(atPath: $0) }) else {
                DispatchQueue.main.async { completion(["error": "请先安装 Python 3.8 或更新版本。专业统计脚本需 Python 3。"])}
                return
            }
            process.executableURL = URL(fileURLWithPath: python)
            process.arguments = [resources.appendingPathComponent("monitor.py").path, command]
            process.standardOutput = output
            process.standardError = FileHandle.nullDevice
            do {
                try process.run()
                let raw = output.fileHandleForReading.readDataToEndOfFile()
                process.waitUntilExit()
                data = (try? JSONSerialization.jsonObject(with: raw)) as? [String: Any] ?? ["error": "统计脚本未返回有效数据，请检查 Python 版本。"]
            } catch { data = ["error": "无法运行统计脚本，请检查 Python 3 安装。"] }
            DispatchQueue.main.async { completion(data) }
        }
    }
    func numeric(_ value: Any?) -> Double? {
        guard !(value is NSNull), let n = value as? NSNumber else { return nil }
        return n.doubleValue
    }
    func fmt(_ value: Any?) -> String { guard let n = numeric(value) else { return "未知" }; return String(format: "%.0f", n) }
    func dateText(_ epoch: Double) -> String { let f = DateFormatter(); f.dateStyle = .short; f.timeStyle = .medium; return f.string(from: Date(timeIntervalSince1970: epoch)) }
    func render() {
        let buckets = quota["buckets"] as? [[String: Any]] ?? []
        let main = buckets.first(where: { ($0["id"] as? String) == "codex" }) ?? buckets.first
        let mainWindows = main?["windows"] as? [[String: Any]] ?? []
        let minimum = mainWindows.compactMap { numeric($0["remaining"]) }.min()
        let percent = minimum.map { String(format: "%.1f%%", $0) } ?? "未知"
        item.button?.title = failed ? "CQ !" : "CQ \(minimum.map { String(format: "%.0f%%", floor($0)) } ?? "—")"
        ball.value = minimum.map { String(format: "%.0f%%", $0) } ?? "—"
        ball.subtitle = failed ? "待更新" : "剩余额度"
        ball.toolTip = "Codex 剩余 \(percent)；点击查看详情，拖动移动，右键设置。"
        var text = "Codex 账户额度\n\n"
        if let error = quota["error"] as? String { text += "上次结果 · 待更新\n\(error)\n\n" }
        if let fetched = numeric(quota["fetchedAt"]) { text += "最近成功同步：\(dateText(fetched))\n\n" }
        if buckets.isEmpty { text += "暂无额度数据，请确认 Codex 已使用 ChatGPT 账户登录。\n" }
        for bucket in buckets {
            text += "\(bucket["name"] as? String ?? "Codex")\n"
            for w in bucket["windows"] as? [[String: Any]] ?? [] {
                let remaining = numeric(w["remaining"])
                text += "  \(fmt(w["minutes"])) 分钟窗口 · 剩余 \(remaining.map { String(format: "%.1f%%", $0) } ?? "未知")\n"
                if let reset = numeric(w["reset"]) {
                    let seconds = max(0, reset - Date().timeIntervalSince1970)
                    text += "  重置 \(dateText(reset)) · \(seconds == 0 ? "等待服务确认" : String(format: "%.0f 分钟后", ceil(seconds / 60)))\n"
                }
                if !failed, let remain = remaining {
                    let key = "\(bucket["id"] ?? ""):\(w["key"] ?? ""):\(fmt(w["reset"]))"
                    if remain <= threshold && !alerted.contains(key) {
                        alerted.insert(key)
                        let content = UNMutableNotificationContent()
                        content.title = "Codex 额度偏低"
                        content.body = "\(bucket["name"] ?? "Codex") 剩余 \(String(format: "%.1f%%", remain))"
                        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: key, content: content, trigger: nil))
                    } else if remain > threshold { alerted.remove(key) }
                }
            }
            text += "\n"
        }
        let cache = taskData["cache"] as? [String: Any] ?? [:]
        let hit = numeric(cache["percent"]).map { String(format: "%.1f%%", $0) } ?? "未知"
        text += "近期每轮任务\n最近 7 天内变化的本机日志 · 最多 200 轮\n\n"
        text += "加权缓存命中率：\(hit) · 有效 \(fmt(cache["included"])) 轮\n"
        text += "缺失／无效 \(fmt(cache["missing"])) 轮 · 零输入 \(fmt(cache["zero"])) 轮\n\n"
        if let error = taskData["error"] as? String { text += "\(error)\n" }
        for row in taskData["records"] as? [[String: Any]] ?? [] {
            let u = row["usage"] as? [String: Any] ?? [:]
            let input = numeric(u["input_tokens"]), cached = numeric(u["cached_input_tokens"])
            let rate: String
            if let i = input, let c = cached, i > 0, c <= i { rate = String(format: "%.1f%%", c / i * 100) } else { rate = "未知" }
            text += "\(row["parent"] as? String == "" ? "主任务" : "子任务") \(String((row["thread"] as? String ?? "").prefix(8))) · \(row["model"] as? String ?? "")\n"
            text += "  \(row["completed"] ?? "") · 总 Token \(fmt(u["total_tokens"])) · 缓存 \(rate)\n"
            text += "  输入 \(fmt(u["input_tokens"]))（缓存 \(fmt(u["cached_input_tokens"]))）\n"
            text += "  输出 \(fmt(u["output_tokens"]))（推理 \(fmt(u["reasoning_output_tokens"]))）\n\n"
        }
        text += "缓存已包含在输入中；推理已包含在输出中。未知字段不估算为零。\nToken 不能换算为账户剩余额度百分比或费用。\n"
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineSpacing = 3
        let styled = NSMutableAttributedString(string: text, attributes: [.font: NSFont.systemFont(ofSize: 13), .foregroundColor: Palette.text, .paragraphStyle: paragraph])
        let full = text as NSString
        for heading in ["Codex 账户额度", "近期每轮任务"] {
            let range = full.range(of: heading)
            if range.location != NSNotFound { styled.addAttributes([.font: NSFont.systemFont(ofSize: 16, weight: .semibold), .foregroundColor: Palette.accent], range: range) }
        }
        if let regex = try? NSRegularExpression(pattern: "剩余 [0-9.]+%|加权缓存命中率：[0-9.]+%") {
            for match in regex.matches(in: text, range: NSRange(location: 0, length: full.length)) {
                styled.addAttributes([.font: NSFont.monospacedDigitSystemFont(ofSize: 18, weight: .bold), .foregroundColor: Palette.accent], range: match.range)
            }
        }
        if let mutedLines = try? NSRegularExpression(pattern: "(?m)^.*(?:最近成功同步|最近 7 天内变化|缺失／无效|缓存已包含|Token 不能|重置 ).*$") {
            for match in mutedLines.matches(in: text, range: NSRange(location: 0, length: full.length)) {
                styled.addAttributes([.font: NSFont.systemFont(ofSize: 12), .foregroundColor: Palette.muted], range: match.range)
            }
        }
        let oldOffset = detail.enclosingScrollView?.contentView.bounds.origin ?? .zero
        detail.textStorage?.setAttributedString(styled)
        detail.enclosingScrollView?.contentView.scroll(to: oldOffset)
    }
}
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = Delegate()
app.delegate = delegate
app.run()
