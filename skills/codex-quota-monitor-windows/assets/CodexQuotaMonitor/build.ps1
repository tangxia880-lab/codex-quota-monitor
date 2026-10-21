param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler not found.' }
$target = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $PSScriptRoot 'CodexQuotaMonitor.exe' } else { [IO.Path]::GetFullPath($OutputPath) }
$targetFolder = Split-Path -Parent $target
if (-not (Test-Path -LiteralPath $targetFolder)) { New-Item -ItemType Directory -Path $targetFolder | Out-Null }
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$target" @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output "Built: $target"
