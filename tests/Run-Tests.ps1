[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$src = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts'
$compiler = @('Framework64','Framework') | ForEach-Object {
    Join-Path $env:SystemRoot "Microsoft.NET\$_\v4.0.30319\csc.exe"
} | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'Windows .NET Framework compiler required.' }
$scratch = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('network-skill-tests-' + [guid]::NewGuid().ToString('N'))))
try {
    $exe = Join-Path $scratch.FullName 'Regression.exe'
    & $compiler /nologo /codepage:65001 /optimize+ /target:exe /main:Regression /r:System.Web.Extensions.dll "/out:$exe" (Join-Path $src 'Repair-CodexReconnect.cs') (Join-Path $src 'Check-WindowsProxy.cs') (Join-Path $PSScriptRoot 'Regression.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $exe $scratch.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Regression test failed.' }
} finally {
    if ($scratch.Parent.FullName.TrimEnd('\') -eq [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -and $scratch.Name -match '^network-skill-tests-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $scratch.FullName -Recurse -Force
    }
}
