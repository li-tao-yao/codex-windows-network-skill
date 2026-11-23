<#
.SYNOPSIS
    使用 Windows 内置 .NET Framework 4.x 编译器 (csc.exe) 从源码构建辅助工具。
.DESCRIPTION
    将 scripts\Check-WindowsProxy.cs 和 scripts\Repair-CodexReconnect.cs 编译为同目录的 .exe 文件，
    无需安装 Visual Studio 或额外 .NET SDK。
#>
[CmdletBinding()]
param(
    [string]$OutDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$candidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) {
    throw '未找到 .NET Framework 4.x 编译器 (csc.exe)。请在 Windows 桌面环境运行，或直接从 GitHub Releases 下载预编译 .exe。'
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

$targets = @(
    @{
        Source = Join-Path $PSScriptRoot 'Check-WindowsProxy.cs'
        Output = Join-Path $OutDir 'Check-WindowsProxy.exe'
        Refs   = @()
    },
    @{
        Source = Join-Path $PSScriptRoot 'Repair-CodexReconnect.cs'
        Output = Join-Path $OutDir 'Repair-CodexReconnect.exe'
        Refs   = @('System.Web.Extensions.dll')
    }
)

Write-Host "使用编译器: $csc"
foreach ($t in $targets) {
    if (-not (Test-Path -LiteralPath $t.Source)) {
        throw "缺少源码文件: $($t.Source)"
    }
    $argsList = @('/nologo', '/optimize+', '/target:exe', '/utf8output')
    foreach ($r in $t.Refs) {
        $argsList += "/r:$r"
    }
    $argsList += "/out:$($t.Output)"
    $argsList += $t.Source

    & $csc @argsList
    if ($LASTEXITCODE -ne 0) {
        throw "编译失败 (退出码 $LASTEXITCODE): $($t.Source)"
    }
    $hash = (Get-FileHash -LiteralPath $t.Output -Algorithm SHA256).Hash
    Write-Host "已生成: $($t.Output)"
    Write-Host "SHA256: $hash"
}
