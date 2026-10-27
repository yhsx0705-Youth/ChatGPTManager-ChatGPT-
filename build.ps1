$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到系统 .NET Framework C# 编译器。' }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$output = Join-Path $PSScriptRoot 'ChatGPTManager.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output `
    "/out:$output" "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'assets\app.ico')" `
    "/resource:$(Join-Path $PSScriptRoot 'assets\floating-ball-mark.png'),ChatGPTManager.FloatingBallMark.png" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output "已生成：$output"
