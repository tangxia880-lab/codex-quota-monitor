#!/bin/bash
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
app="$root/CodexQuotaMonitor.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
CLANG_MODULE_CACHE_PATH="${TMPDIR:-/tmp}/quota-clang-cache" swiftc -module-cache-path "${TMPDIR:-/tmp}/quota-swift-cache" "$root/native/Monitor.swift" -o "$app/Contents/MacOS/CodexQuotaMonitor" -framework AppKit -framework UserNotifications
cp "$root/scripts/monitor.py" "$app/Contents/Resources/monitor.py"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>CodexQuotaMonitor</string>
<key>CFBundleIdentifier</key><string>local.codex.quota.monitor</string>
<key>CFBundleName</key><string>Codex 用量统计</string>
<key>CFBundleVersion</key><string>1.0.0</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>LSUIElement</key><true/>
<key>NSHighResolutionCapable</key><true/>
<key>LSMinimumSystemVersion</key><string>12.0</string>
</dict></plist>
PLIST
codesign --force --deep --sign - "$app"
printf 'Built %s\n' "$app"
