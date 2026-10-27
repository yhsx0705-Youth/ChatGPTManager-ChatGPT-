$ErrorActionPreference = 'Stop'
$appExe = Join-Path (Split-Path -Parent $PSScriptRoot) 'ChatGPTManager.exe'
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) { throw '找不到 APP。' }
$desktopFolder = [Environment]::GetFolderPath('DesktopDirectory')
if (-not (Test-Path -LiteralPath $desktopFolder -PathType Container)) { throw '找不到桌面文件夹。' }
$shortcutPath = Join-Path $desktopFolder 'ChatGPT Manager.lnk'
$shortcutShell = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $shortcutPath) {
    $existingShortcut = $shortcutShell.CreateShortcut($shortcutPath)
    if ($existingShortcut.TargetPath -ne $appExe) {
        $shortcutPath = Join-Path $desktopFolder 'ChatGPT Manager 配置切换.lnk'
        if (Test-Path -LiteralPath $shortcutPath) { throw '桌面已有其他同名快捷方式，未覆盖。' }
    }
}
$shortcut = $shortcutShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $appExe
$shortcut.WorkingDirectory = [System.IO.Path]::GetDirectoryName($appExe)
$shortcut.IconLocation = $appExe + ',0'
$shortcut.Description = 'ChatGPT 官方 / 中转配置管理'
$shortcut.WindowStyle = 1
$shortcut.Save()
$verifiedShortcut = $shortcutShell.CreateShortcut($shortcutPath)
if ($verifiedShortcut.TargetPath -ne $appExe) { throw '快捷方式验证失败。' }
[pscustomobject]@{ Shortcut=$shortcutPath; Target=$verifiedShortcut.TargetPath; WorkingDirectory=$verifiedShortcut.WorkingDirectory } | ConvertTo-Json
