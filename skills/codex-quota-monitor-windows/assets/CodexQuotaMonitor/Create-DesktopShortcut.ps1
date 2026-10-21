$ErrorActionPreference = 'Stop'
$monitorTarget = Join-Path $PSScriptRoot 'CodexQuotaMonitor.exe'
if (-not (Test-Path -LiteralPath $monitorTarget)) {
    & (Join-Path $PSScriptRoot 'build.ps1')
}
if (-not (Test-Path -LiteralPath $monitorTarget)) { throw 'Monitor executable not found.' }
$desktopFolder = [Environment]::GetFolderPath('DesktopDirectory')
if ([string]::IsNullOrWhiteSpace($desktopFolder)) { throw 'Desktop folder not found.' }
$shortcutPath = Join-Path $desktopFolder 'Codex Quota Monitor.lnk'
$shellLink = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $shortcutPath) {
    $oldLink = $shellLink.CreateShortcut($shortcutPath)
    if ($oldLink.TargetPath -ne $monitorTarget) {
        $shortcutPath = Join-Path $desktopFolder ('Codex Quota Monitor-' + [Guid]::NewGuid().ToString('N').Substring(0, 6) + '.lnk')
    }
}
$link = $shellLink.CreateShortcut($shortcutPath)
$link.TargetPath = $monitorTarget
$link.WorkingDirectory = $PSScriptRoot
$link.Description = 'Codex quota, task token usage and cache hit rate'
$link.WindowStyle = 7
$link.Save()
Write-Output "Created: $shortcutPath"
