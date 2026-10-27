$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found.' }

$appRoot = Split-Path -Parent $PSScriptRoot
$testExe = Join-Path $PSScriptRoot 'UninstallRestoreTests.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ "/out:$testExe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll `
    (Join-Path $appRoot 'src\Core.cs') `
    (Join-Path $appRoot 'packaging\RestoreOfficialService.cs') `
    (Join-Path $PSScriptRoot 'UninstallRestoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }

& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Uninstall restore tests failed.' }
