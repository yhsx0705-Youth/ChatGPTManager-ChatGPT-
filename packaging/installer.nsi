!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"

Var InstallPathField
Var InstallPathValue
Var ShortcutCheckbox
Var CreateShortcuts
Var PathPreview

Name "ChatGPT Manager"
OutFile "..\..\ChatGPTManagerSetup.exe"
VIProductVersion "1.8.1.0"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright (c) ChatGPT Manager"
VIAddVersionKey /LANG=1033 "ProductName" "ChatGPT Manager"
VIAddVersionKey /LANG=1033 "FileDescription" "ChatGPT Manager installer"
VIAddVersionKey /LANG=1033 "FileVersion" "1.8.1.0"
VIAddVersionKey /LANG=1033 "ProductVersion" "1.8.1"
InstallDir "C:\ChatGPT Manager"
RequestExecutionLevel admin
SetCompressor /SOLID lzma

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\APP\ChatGPTManager.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ChatGPT Manager"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\APP\安装说明.md"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the installation guide"

Page custom InstallOptionsPageCreate InstallOptionsPageLeave "Setup Options"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Function InstallOptionsPageCreate
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 12u "Installation folder (ASCII characters only):"
  Pop $1
  ${NSD_CreateText} 0 15u 76% 14u "$INSTDIR"
  Pop $InstallPathField
  ${NSD_CreateButton} 78% 15u 22% 14u "Browse..."
  Pop $2
  ${NSD_OnClick} $2 InstallBrowseClick
  ${NSD_OnChange} $InstallPathField InstallPathChanged

  ${NSD_CreateCheckbox} 0 39u 100% 14u "Create Desktop and Start Menu shortcuts"
  Pop $ShortcutCheckbox
  ${NSD_SetState} $ShortcutCheckbox ${BST_CHECKED}

  ${NSD_CreateLabel} 0 62u 100% 12u "The following folders will be created or updated:"
  Pop $1
  ${NSD_CreateLabel} 12u 78u 100% 45u ""
  Pop $PathPreview
  Call UpdatePathPreview

  ${NSD_CreateLabel} 0 127u 100% 22u "Uninstall is blocked while a saved Transit key is in use."
  Pop $1
  nsDialogs::Show
FunctionEnd

Function InstallBrowseClick
  Pop $3
  ${NSD_GetText} $InstallPathField $0
  nsDialogs::SelectFolderDialog "Select installation folder" "$0"
  Pop $0
  StrCmp $0 "" browse_done
  ${NSD_SetText} $InstallPathField "$0"
browse_done:
FunctionEnd

Function InstallPathChanged
  Pop $3
  Call UpdatePathPreview
FunctionEnd

Function UpdatePathPreview
  ${NSD_GetText} $InstallPathField $0
  StrCmp $0 "" 0 +2
    StrCpy $0 "(select a folder)"
  ${NSD_SetText} $PathPreview "$0\APP$\r$\n$0\Official$\r$\n$0\Transit"
FunctionEnd

Function InstallOptionsPageLeave
  ${NSD_GetText} $InstallPathField $0
  StrCmp $0 "" install_path_default
  Goto install_path_selected
install_path_default:
  StrCpy $0 "$INSTDIR"
  ${NSD_SetText} $InstallPathField "$0"
install_path_selected:
  StrCpy $InstallPathValue "$0"
  StrCpy $INSTDIR "$InstallPathValue"
  ${NSD_GetState} $ShortcutCheckbox $1
  ${If} $1 == ${BST_CHECKED}
    StrCpy $CreateShortcuts "1"
  ${Else}
    StrCpy $CreateShortcuts "0"
  ${EndIf}
  Return

FunctionEnd

Section "Install"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=UninstallRestoreCheck.exe "..\Uninstall.exe"

  ExecWait '"$PLUGINSDIR\UninstallRestoreCheck.exe" --check-codex --silent' $0
  ${If} $0 != 0
    IfSilent install_abort install_abort_with_message
install_abort_with_message:
    MessageBox MB_ICONSTOP "ChatGPT or ChatGPT Manager is running. Installation was blocked. Close it completely and try again."
install_abort:
    Abort
  ${EndIf}

  ExecWait '"$PLUGINSDIR\UninstallRestoreCheck.exe" --validate-install-path "$InstallPathValue"' $0
  ${If} $0 != 0
    IfSilent install_path_abort install_path_abort_with_message
install_path_abort_with_message:
    MessageBox MB_ICONSTOP "The selected path is invalid: $InstallPathValue. Choose a non-root installation folder using English / ASCII characters, for example E:\ChatGPT Manager."
install_path_abort:
    Abort
  ${EndIf}

  CreateDirectory "$InstallPathValue"
  IfFileExists "$InstallPathValue\." install_directory_ready install_directory_failed
install_directory_failed:
  IfSilent install_directory_abort install_directory_abort_with_message
install_directory_abort_with_message:
  MessageBox MB_ICONSTOP "Windows could not create or access the selected folder: $InstallPathValue. Check that the drive is available and that you have permission and free space."
install_directory_abort:
  Abort
install_directory_ready:

  SetShellVarContext all
  SetOutPath "$INSTDIR\APP"
  Delete "$INSTDIR\APP\CodexSwitch.exe"
  File "..\ChatGPTManager.exe"
  File "..\Uninstall.exe"
  File /oname=安装说明.md "安装说明.md"
  File /oname=使用说明.md "..\使用说明.md"

  SetOutPath "$INSTDIR\Official"
  File /oname=README.txt "Official-README.txt"
  CreateDirectory "$INSTDIR\Official\SafetyBackup"

  SetOutPath "$INSTDIR\Transit"
  File /oname=README.txt "Transit-README.txt"
  IfFileExists "$INSTDIR\Transit\config.toml" transit_config_exists
    File /oname=config.toml "Transit-config.toml"
transit_config_exists:
  IfFileExists "$INSTDIR\Transit\auth.json" transit_auth_exists
    File /oname=auth.json "Transit-auth.template.json"
transit_auth_exists:

  ReadEnvStr $0 "USERDOMAIN"
  ReadEnvStr $1 "USERNAME"
  ExecWait '"$SYSDIR\icacls.exe" "$INSTDIR" /grant "$0\$1:(OI)(CI)M" /C' $2
  ${If} $2 != 0
    MessageBox MB_ICONSTOP "Windows could not grant the required access to the installation folder. Installation was stopped."
    Abort
  ${EndIf}

  StrCmp $CreateShortcuts "1" create_shortcuts
  Delete "$DESKTOP\Codex Switch.lnk"
  Delete "$SMPROGRAMS\Codex Switch\Codex Switch.lnk"
  RMDir "$SMPROGRAMS\Codex Switch"
  Delete "$DESKTOP\ChatGPT Manager.lnk"
  Delete "$SMPROGRAMS\ChatGPT Manager\ChatGPT Manager.lnk"
  RMDir "$SMPROGRAMS\ChatGPT Manager"
  Goto shortcuts_done
create_shortcuts:
  Delete "$DESKTOP\Codex Switch.lnk"
  Delete "$SMPROGRAMS\Codex Switch\Codex Switch.lnk"
  RMDir "$SMPROGRAMS\Codex Switch"
  CreateDirectory "$SMPROGRAMS\ChatGPT Manager"
  CreateShortcut "$SMPROGRAMS\ChatGPT Manager\ChatGPT Manager.lnk" "$INSTDIR\APP\ChatGPTManager.exe"
  CreateShortcut "$DESKTOP\ChatGPT Manager.lnk" "$INSTDIR\APP\ChatGPTManager.exe"
shortcuts_done:

  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexSwitch"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ChatGPTManager" "DisplayName" "ChatGPT Manager"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ChatGPTManager" "UninstallString" '"$INSTDIR\APP\Uninstall.exe"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ChatGPTManager" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ChatGPTManager" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ChatGPTManager" "NoRepair" 1
SectionEnd
