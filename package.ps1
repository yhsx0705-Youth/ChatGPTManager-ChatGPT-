$ErrorActionPreference = 'Stop'

$makensis = Get-Command makensis.exe -ErrorAction SilentlyContinue
if ($makensis) {
    $compiler = $makensis.Source
} else {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'NSIS\makensis.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    if (-not $candidates) { throw 'NSIS 3 was not found. Install NSIS and run package.ps1 again.' }
    $compiler = $candidates[0]
}

$frameworkCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $frameworkCompiler)) {
    $frameworkCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $frameworkCompiler)) { throw 'The .NET Framework C# compiler was not found.' }

$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' |
    Sort-Object Name | Select-Object -ExpandProperty FullName)
$appExe = Join-Path $PSScriptRoot 'ChatGPTManager.exe'
$manifest = Join-Path $PSScriptRoot 'app.manifest'
$icon = Join-Path $PSScriptRoot 'assets\app.ico'
$authTemplatePath = Join-Path $PSScriptRoot 'packaging\Transit-auth.template.json'
$authTemplate = Get-Content -LiteralPath $authTemplatePath -Raw | ConvertFrom-Json
$authProperties = @($authTemplate.PSObject.Properties)
if ($authProperties.Count -ne 1 -or $authProperties[0].Name -ne 'OPENAI_API_KEY' -or -not ($authProperties[0].Value -is [string]) -or $authProperties[0].Value.Length -ne 0) {
    throw 'The packaged Transit auth template must contain only a blank API key.'
}
$programVersion = [regex]::Match(
    (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\Program.cs') -Raw),
    'AssemblyVersion\("(\d+\.\d+\.\d+\.\d+)"\)').Groups[1].Value
$installerVersion = [regex]::Match(
    (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging\installer.nsi') -Raw),
    'VIProductVersion "([^"]+)"').Groups[1].Value
if (-not $programVersion -or $programVersion -ne $installerVersion) {
    throw "Application and installer versions do not match: $programVersion / $installerVersion"
}
& $frameworkCompiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output `
    "/out:$appExe" "/win32manifest:$manifest" "/win32icon:$icon" `
    "/resource:$(Join-Path $PSScriptRoot 'assets\floating-ball-mark.png'),ChatGPTManager.FloatingBallMark.png" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }

$uninstallerExe = Join-Path $PSScriptRoot 'Uninstall.exe'
$uninstallerSources = @(
    (Join-Path $PSScriptRoot 'src\Core.cs'),
    (Join-Path $PSScriptRoot 'packaging\RestoreOfficialService.cs'),
    (Join-Path $PSScriptRoot 'packaging\UninstallRestore.cs')
)
& $frameworkCompiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output `
    "/out:$uninstallerExe" "/win32manifest:$manifest" "/win32icon:$icon" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $uninstallerSources
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller compilation failed.' }

Push-Location $PSScriptRoot
try {
    & $compiler /V3 /INPUTCHARSET UTF8 'packaging\installer.nsi'
    if ($LASTEXITCODE -ne 0) { throw 'Installer generation failed.' }
} finally {
    Pop-Location
}

$setupPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'ChatGPTManagerSetup.exe'
if (-not (Test-Path -LiteralPath $setupPath)) { throw "Installer was not created: $setupPath" }
Write-Output "Created: $setupPath"
