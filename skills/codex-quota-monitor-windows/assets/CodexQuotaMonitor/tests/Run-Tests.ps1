$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
function Invoke-TestCase([string]$name, [string[]]$sources, [string]$entryPoint = '') {
    $testExecutable = Join-Path $env:TEMP ('CodexQuotaMonitor-Tests-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        $arguments = @('/nologo', '/target:exe', ("/out:" + $testExecutable), '/r:System.Web.Extensions.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
        if ($entryPoint) { $arguments += '/main:' + $entryPoint }
        & $compiler @arguments @sources
        if ($LASTEXITCODE -ne 0) { throw ($name + ' compilation failed.') }
        & $testExecutable
        if ($LASTEXITCODE -ne 0) { throw ($name + ' failed.') }
    } finally {
        if (Test-Path -LiteralPath $testExecutable) { Remove-Item -LiteralPath $testExecutable }
    }
}
Invoke-TestCase 'Quota model' @((Join-Path $appRoot 'src\UsageModel.cs'), (Join-Path $PSScriptRoot 'ModelTests.cs'))
Invoke-TestCase 'Task parser' @((Join-Path $appRoot 'src\TaskUsageParser.cs'), (Join-Path $PSScriptRoot 'TaskUsageTests.cs'))
Invoke-TestCase 'Cache usage statistics' @((Join-Path $appRoot 'src\TaskUsageParser.cs'), (Join-Path $appRoot 'src\CacheUsageStatistics.cs'), (Join-Path $PSScriptRoot 'CacheUsageTests.cs'))
Invoke-TestCase 'Task monitor' @((Join-Path $appRoot 'src\TaskUsageParser.cs'), (Join-Path $appRoot 'src\TaskUsageMonitor.cs'), (Join-Path $PSScriptRoot 'TaskMonitorTests.cs'))
$uiSources = @(Get-ChildItem -LiteralPath (Join-Path $appRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$uiSources += Join-Path $PSScriptRoot 'UiLifecycleTests.cs'
Invoke-TestCase 'Offline interface' $uiSources 'UiLifecycleTests'
Invoke-TestCase 'Edge docking and colors' @((Join-Path $appRoot 'src\EdgeDock.cs'), (Join-Path $PSScriptRoot 'EdgeDockTests.cs'))
$floatingSources = @(Get-ChildItem -LiteralPath (Join-Path $appRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$floatingSources += Join-Path $PSScriptRoot 'FloatingQuotaTests.cs'
Invoke-TestCase 'Floating window' $floatingSources 'FloatingQuotaTests'
