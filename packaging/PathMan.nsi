Unicode true
ManifestDPIAware true
ManifestSupportedOS all
!include MUI2.nsh
!include LogicLib.nsh
!include FileFunc.nsh
!include x64.nsh
!define APPKEY "Software\PathMan"
!define UNINSTALLKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan"

!define MULTIUSER_EXECUTIONLEVEL Highest
!define MULTIUSER_MUI
!define MULTIUSER_INSTALLMODE_COMMANDLINE
!define MULTIUSER_INSTALLMODE_DEFAULT_CURRENTUSER
!define MULTIUSER_INSTALLMODE_DEFAULT_REGISTRY_KEY "${APPKEY}"
!define MULTIUSER_INSTALLMODE_DEFAULT_REGISTRY_VALUENAME "InstallDirectory"
!define MULTIUSER_INSTALLMODE_INSTDIR "PathMan"
!define MULTIUSER_INSTALLMODE_INSTDIR_REGISTRY_KEY "${APPKEY}"
!define MULTIUSER_INSTALLMODE_INSTDIR_REGISTRY_VALUENAME "InstallDirectory"
!define MULTIUSER_INSTALLMODEPAGE_SHOWUSERNAME
!if "${ARCH}" != "x86"
  !define MULTIUSER_USE_PROGRAMFILES64
!endif
!include MultiUser.nsh
; Start unelevated. Only the AllUsers choice relaunches with runas. Keep the
; framework's MultiUser page and account-name control rather than drawing a page.
RequestExecutionLevel user

Name "PathMan"
OutFile "${OUTPUT_FILE}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "PathMan"
VIAddVersionKey /LANG=1033 "FileDescription" "PathMan Setup (${ARCH}, ${PACKAGE_TYPE})"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "PathMan contributors"
SetCompressor /SOLID zlib
ShowInstDetails nevershow
ShowUninstDetails nevershow
!define MUI_ABORTWARNING
!define MUI_LANGDLL_ALWAYSSHOW
!define MUI_LANGDLL_WINDOWTITLE "PathMan"

Var StartMenuFolder
Var Parameters
Var ScopeArgument
Var Elevated
Var Machine
Var Helper
Var UserLocalData
Var OldDirectory
Var CommandDirectory

!define MULTIUSER_PAGE_CUSTOMFUNCTION_PRE ScopePre
!define MULTIUSER_PAGE_CUSTOMFUNCTION_SHOW ScopeShow
!define MULTIUSER_PAGE_CUSTOMFUNCTION_LEAVE ScopeLeave
!insertmacro MULTIUSER_PAGE_INSTALLMODE
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE DirectoryLeave
!insertmacro MUI_PAGE_DIRECTORY
!define MUI_STARTMENUPAGE_DEFAULTFOLDER "PathMan"
!define MUI_STARTMENUPAGE_REGISTRY_ROOT SHCTX
!define MUI_STARTMENUPAGE_REGISTRY_KEY "${APPKEY}"
!define MUI_STARTMENUPAGE_REGISTRY_VALUENAME "StartMenuFolder"
!insertmacro MUI_PAGE_STARTMENU Application $StartMenuFolder
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "$(DesktopShortcut)"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_RUN_FUNCTION CreateDesktopShortcut
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "$(OpenPathMan)"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION LaunchPathMan
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

LangString DesktopShortcut ${LANG_ENGLISH} "Create a &desktop shortcut"
LangString DesktopShortcut ${LANG_SIMPCHINESE} "创建桌面快捷方式(&D)"
LangString OpenPathMan ${LANG_ENGLISH} "&Open PathMan"
LangString OpenPathMan ${LANG_SIMPCHINESE} "打开 PathMan(&O)"
LangString InvalidDirectory ${LANG_ENGLISH} "Choose a valid installation folder on the same volume as LocalAppData. The volume must support hard links."
LangString InvalidDirectory ${LANG_SIMPCHINESE} "请选择与 LocalAppData 位于同一卷的有效安装目录。该卷必须支持硬链接。"
LangString RuntimeRequired ${LANG_ENGLISH} "This package needs the ${ARCH} .NET 10 Desktop Runtime. Install it first, or use the -SelfContained installer."
LangString RuntimeRequired ${LANG_SIMPCHINESE} "此安装包需要 ${ARCH} 架构的 .NET 10 Desktop Runtime。请先安装运行时，或使用 -SelfContained 安装包。"
LangString WrongArchitecture ${LANG_ENGLISH} "This installer is for ${ARCH}. Choose a package that matches your Windows architecture."
LangString WrongArchitecture ${LANG_SIMPCHINESE} "此安装包适用于 ${ARCH}。请选择与 Windows 架构匹配的安装包。"
LangString ExistingOtherScope ${LANG_ENGLISH} "PathMan is already installed in another scope. Uninstall it before changing installation scope."
LangString ExistingOtherScope ${LANG_SIMPCHINESE} "已存在另一安装范围的 PathMan。请先卸载，再更改安装范围。"
LangString ExistingDirectory ${LANG_ENGLISH} "Reinstall in the existing folder, or uninstall PathMan before choosing another folder."
LangString ExistingDirectory ${LANG_SIMPCHINESE} "请沿用已有安装目录。如需更换目录，请先卸载 PathMan。"
LangString LegacyInstaller ${LANG_ENGLISH} "Uninstall the previous MSI version of PathMan before installing this version. Your alias data will be preserved."
LangString LegacyInstaller ${LANG_SIMPCHINESE} "请先卸载旧的 MSI 版 PathMan，再安装此版本。别名数据会保留。"
LangString MaintenanceFailed ${LANG_ENGLISH} "PathMan setup could not complete a maintenance operation (Windows error $0). Close PathMan and try again."
LangString MaintenanceFailed ${LANG_SIMPCHINESE} "安装程序未能完成维护操作（Windows 错误 $0）。请关闭 PathMan 后重试。"

Function GetScope
  StrCpy $Machine 0
  ${If} $MultiUser.InstallMode == "AllUsers"
    StrCpy $Machine 1
  ${EndIf}
FunctionEnd

Function ElevateForAllUsers
  ${If} $Elevated == 0
    ClearErrors
    ExecShell "runas" "$EXEPATH" '/AllUsers /LANG=$LANGUAGE $Parameters'
    ${If} ${Errors}
      Abort
    ${EndIf}
    Quit
  ${EndIf}
FunctionEnd

Function CheckLegacyRegistration
  ReadRegStr $0 HKCU "${APPKEY}" "InstallDirectory"
  ReadRegStr $1 HKCU "${APPKEY}" "InstallerKind"
  ${If} $0 != ""
  ${AndIf} $1 != "NSIS"
    MessageBox MB_OK|MB_ICONSTOP "$(LegacyInstaller)" /SD IDOK
    SetErrorLevel 1638
    Quit
  ${EndIf}
  ReadRegStr $0 HKLM "${APPKEY}" "InstallDirectory"
  ReadRegStr $1 HKLM "${APPKEY}" "InstallerKind"
  ${If} $0 != ""
  ${AndIf} $1 != "NSIS"
    MessageBox MB_OK|MB_ICONSTOP "$(LegacyInstaller)" /SD IDOK
    SetErrorLevel 1638
    Quit
  ${EndIf}
FunctionEnd

Function .onInit
  ; One registry view for installer identity across all payload architectures.
  SetRegView 32
  StrCpy $CommandDirectory $INSTDIR
  StrCpy $UserLocalData "$LOCALAPPDATA"
  ${GetParameters} $Parameters
  StrCpy $ScopeArgument ""
  ClearErrors
  ${GetOptions} $Parameters "/LANG=" $0
  ${IfNot} ${Errors}
    StrCpy $LANGUAGE $0
  ${Else}
    !insertmacro MUI_LANGDLL_DISPLAY
  ${EndIf}
  ${If} ${RunningX64}
    SetRegView 64
    Call CheckLegacyRegistration
  ${EndIf}
  SetRegView 32
  Call CheckLegacyRegistration
!if "${ARCH}" == "x64"
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "$(WrongArchitecture)" /SD IDOK
    SetErrorLevel 1633
    Quit
  ${EndIf}
!else if "${ARCH}" == "arm64"
  ${IfNot} ${IsNativeARM64}
    MessageBox MB_OK|MB_ICONSTOP "$(WrongArchitecture)" /SD IDOK
    SetErrorLevel 1633
    Quit
  ${EndIf}
!endif
  ClearErrors
  ${GetOptions} $Parameters "/AllUsers" $0
  ${IfNot} ${Errors}
    StrCpy $ScopeArgument "/AllUsers"
  ${Else}
    ClearErrors
    ${GetOptions} $Parameters "/CurrentUser" $0
    ${IfNot} ${Errors}
      StrCpy $ScopeArgument "/CurrentUser"
    ${EndIf}
  ${EndIf}
  System::Call 'shell32::IsUserAnAdmin() i.r0'
  StrCpy $Elevated $0
  ReadRegStr $0 HKLM "${APPKEY}" "InstallDirectory"
  ${If} $ScopeArgument == ""
  ${AndIf} $0 != ""
    StrCpy $ScopeArgument "/AllUsers"
  ${EndIf}
  ${If} $ScopeArgument == "/AllUsers"
    Call ElevateForAllUsers
  ${EndIf}
  !insertmacro MULTIUSER_INIT
  ; MultiUser's stock page otherwise hides AllUsers for a standard account.
  ; The choice is available to everyone; ScopeLeave obtains real UAC privileges.
  StrCpy $MultiUser.Privileges "Admin"
  ${If} $ScopeArgument == "/AllUsers"
    Call MultiUser.InstallMode.AllUsers
  ${EndIf}
  ${If} $CommandDirectory != ""
    StrCpy $INSTDIR $CommandDirectory
  ${EndIf}
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=PathMan.SetupActions.dll "${HELPER_DLL}"
  StrCpy $Helper "$PLUGINSDIR\PathMan.SetupActions.dll"
  ReadRegStr $0 SHCTX "${APPKEY}" "InstallDirectory"
  ${If} $0 != ""
    ReadRegStr $1 SHCTX "${APPKEY}" "InstallerKind"
    ${If} $1 != "NSIS"
      MessageBox MB_OK|MB_ICONSTOP "$(LegacyInstaller)" /SD IDOK
      SetErrorLevel 1638
      Quit
    ${EndIf}
  ${EndIf}
!if "${PACKAGE_TYPE}" == "FrameworkDependent"
  System::Call '$Helper::HasDesktopRuntime(w "${ARCH}") i.r0'
  ${If} $0 == 0
    MessageBox MB_OK|MB_ICONSTOP "$(RuntimeRequired)" /SD IDOK
    SetErrorLevel 1603
    Quit
  ${EndIf}
!endif
FunctionEnd

Function ScopePre
  ${If} $ScopeArgument != ""
    Abort
  ${EndIf}
FunctionEnd

Function UpdateScopeShield
  SendMessage $MultiUser.InstallModePage.AllUsers ${BM_GETCHECK} 0 0 $0
  GetDlgItem $1 $HWNDPARENT 1
  SendMessage $1 0x160C 0 $0 ; BCM_SETSHIELD
FunctionEnd

Function ScopeShow
  ${NSD_OnClick} $MultiUser.InstallModePage.AllUsers ScopeRadioClicked
  ${NSD_OnClick} $MultiUser.InstallModePage.CurrentUser ScopeRadioClicked
  Call UpdateScopeShield
FunctionEnd

Function ScopeRadioClicked
  Pop $0 ; nsDialogs passes the clicked control on the stack.
  Call UpdateScopeShield
FunctionEnd

Function ScopeLeave
  ${If} $MultiUser.InstallMode == "AllUsers"
    Call ElevateForAllUsers
  ${EndIf}
  ${If} $CommandDirectory != ""
    StrCpy $INSTDIR $CommandDirectory
  ${EndIf}
  Call UpdateScopeShield
FunctionEnd

Function DirectoryLeave
  Call GetScope
  ${If} $Machine == 1
    ReadRegStr $0 HKCU "${APPKEY}" "InstallDirectory"
  ${Else}
    ReadRegStr $0 HKLM "${APPKEY}" "InstallDirectory"
  ${EndIf}
  ${If} $0 != ""
    MessageBox MB_OK|MB_ICONSTOP "$(ExistingOtherScope)" /SD IDOK
    SetErrorLevel 1638
    Abort
  ${EndIf}
  ; An upgrade keeps its directory so old PATH entries/shortcuts stay owned.
  ReadRegStr $OldDirectory SHCTX "${APPKEY}" "InstallDirectory"
  ${If} $OldDirectory != ""
  ${AndIf} $OldDirectory != $INSTDIR
    StrCpy $INSTDIR $OldDirectory
    MessageBox MB_OK|MB_ICONSTOP "$(ExistingDirectory)" /SD IDOK
    SetErrorLevel 87
    Abort
  ${EndIf}
  System::Call '$Helper::ValidateDirectory(w "$INSTDIR", w "$UserLocalData") i.r0'
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "$(InvalidDirectory)" /SD IDOK
    SetErrorLevel 87
    Abort
  ${EndIf}
FunctionEnd

Section "PathMan" Main
  SectionIn RO
  Call DirectoryLeave
  System::Call '$Helper::StopApplications(w "$INSTDIR") i.r0'
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "$(MaintenanceFailed)" /SD IDOK
    SetErrorLevel 1603
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*"
  SetOutPath "$INSTDIR\internal"
  File "${HELPER_DLL}"
  WriteUninstaller "$INSTDIR\internal\Uninstall.exe"
  WriteINIStr "$INSTDIR\internal\install.ini" "PathMan" "Scope" "$MultiUser.InstallMode"
  WriteRegStr SHCTX "${APPKEY}" "InstallDirectory" "$INSTDIR"
  WriteRegStr SHCTX "${APPKEY}" "InstallerKind" "NSIS"
  WriteRegStr SHCTX "${APPKEY}" "PackageType" "${PACKAGE_TYPE}"
  WriteRegStr SHCTX "${APPKEY}" "Architecture" "${ARCH}"
  WriteRegDWORD SHCTX "${APPKEY}" "Language" $LANGUAGE
  WriteRegStr SHCTX "${UNINSTALLKEY}" "DisplayName" "PathMan"
  WriteRegStr SHCTX "${UNINSTALLKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr SHCTX "${UNINSTALLKEY}" "Publisher" "PathMan"
  WriteRegStr SHCTX "${UNINSTALLKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr SHCTX "${UNINSTALLKEY}" "DisplayIcon" "$INSTDIR\PathMan.exe"
  WriteRegStr SHCTX "${UNINSTALLKEY}" "UninstallString" '$\"$INSTDIR\internal\Uninstall.exe$\" /$MultiUser.InstallMode'
  WriteRegStr SHCTX "${UNINSTALLKEY}" "QuietUninstallString" '$\"$INSTDIR\internal\Uninstall.exe$\" /$MultiUser.InstallMode /S'
  WriteRegDWORD SHCTX "${UNINSTALLKEY}" "NoModify" 1
  WriteRegDWORD SHCTX "${UNINSTALLKEY}" "NoRepair" 1
  ReadRegStr $2 SHCTX "${APPKEY}" "StartMenuFolder"
  ${If} $2 != ""
  ${AndIf} $2 != $StartMenuFolder
    Delete "$SMPROGRAMS\$2\PathMan.lnk"
    Delete "$SMPROGRAMS\$2\Uninstall PathMan.lnk"
    RMDir "$SMPROGRAMS\$2"
  ${EndIf}
  !insertmacro MUI_STARTMENU_WRITE_BEGIN Application
    CreateDirectory "$SMPROGRAMS\$StartMenuFolder"
    CreateShortcut "$SMPROGRAMS\$StartMenuFolder\PathMan.lnk" "$INSTDIR\PathMan.exe"
    CreateShortcut "$SMPROGRAMS\$StartMenuFolder\Uninstall PathMan.lnk" "$INSTDIR\internal\Uninstall.exe" "/$MultiUser.InstallMode"
  !insertmacro MUI_STARTMENU_WRITE_END
  System::Call '$Helper::ConfigureInstall(w "$INSTDIR", i $Machine) i.r0'
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "$(MaintenanceFailed)" /SD IDOK
    SetErrorLevel 1603
    Abort
  ${EndIf}
  System::Call '$Helper::RefreshShims(w "$INSTDIR", w "$UserLocalData") i.r0'
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "$(MaintenanceFailed)" /SD IDOK
  ${EndIf}
  SetErrorLevel 0
SectionEnd

Function CreateDesktopShortcut
  CreateShortcut "$DESKTOP\PathMan.lnk" "$INSTDIR\PathMan.exe"
FunctionEnd
Function LaunchPathMan
  ; Explorer launches the desktop app at the desktop user's integrity level.
  ExecShell "open" "$WINDIR\explorer.exe" '$\"$INSTDIR\PathMan.exe$\"'
FunctionEnd

Function un.onInit
  SetRegView 32
  ; NSIS executes a temporary copy of the uninstaller. Its initial INSTDIR
  ; identifies the original uninstaller directory; EXEDIR points at TEMP.
  ${GetParent} "$INSTDIR" $INSTDIR
  StrCpy $OldDirectory $INSTDIR
  ReadINIStr $ScopeArgument "$INSTDIR\internal\install.ini" "PathMan" "Scope"
  ReadRegStr $0 HKLM "${APPKEY}" "InstallDirectory"
  ${If} $0 == $INSTDIR
    StrCpy $ScopeArgument "AllUsers"
  ${EndIf}
  System::Call 'shell32::IsUserAnAdmin() i.r0'
  ${If} $ScopeArgument == "AllUsers"
  ${AndIf} $0 == 0
    ${GetParameters} $Parameters
    ExecShell "runas" "$INSTDIR\internal\Uninstall.exe" '/AllUsers $Parameters'
    Quit
  ${EndIf}
  !insertmacro MULTIUSER_UNINIT
  ${If} $ScopeArgument == "AllUsers"
    Call un.MultiUser.InstallMode.AllUsers
  ${Else}
    Call un.MultiUser.InstallMode.CurrentUser
  ${EndIf}
  StrCpy $INSTDIR $OldDirectory
  StrCpy $Machine 0
  ${If} $MultiUser.InstallMode == "AllUsers"
    StrCpy $Machine 1
  ${EndIf}
  ReadRegDWORD $LANGUAGE SHCTX "${APPKEY}" "Language"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=PathMan.SetupActions.dll "${HELPER_DLL}"
  StrCpy $Helper "$PLUGINSDIR\PathMan.SetupActions.dll"
FunctionEnd

!macro DeleteInstalledFile RelativePath
  IfFileExists "$INSTDIR\${RelativePath}" 0 +2
    Delete "$INSTDIR\${RelativePath}"
!macroend

Section "Uninstall"
  System::Call '$Helper::StopApplications(w "$INSTDIR") i.r0'
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "$(MaintenanceFailed)" /SD IDOK
    SetErrorLevel 1603
    Abort
  ${EndIf}
  System::Call '$Helper::CleanupInstall(w "$INSTDIR", i $Machine, w "$DESKTOP") i.r0'
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "$(MaintenanceFailed)" /SD IDOK
    SetErrorLevel 1603
    Abort
  ${EndIf}
  !insertmacro MUI_STARTMENU_GETFOLDER Application $StartMenuFolder
  Delete "$SMPROGRAMS\$StartMenuFolder\PathMan.lnk"
  Delete "$SMPROGRAMS\$StartMenuFolder\Uninstall PathMan.lnk"
  RMDir "$SMPROGRAMS\$StartMenuFolder"
  ClearErrors
  !insertmacro DeleteInstalledFile "PathMan.exe"
  !insertmacro DeleteInstalledFile "LICENSE"
  !insertmacro DeleteInstalledFile "bin\pathman.exe"
  !insertmacro DeleteInstalledFile "internal\PathMan.Service.exe"
  !insertmacro DeleteInstalledFile "internal\PathMan.Shim.exe"
  !insertmacro DeleteInstalledFile "internal\PathMan.SetupActions.dll"
  !insertmacro DeleteInstalledFile "internal\install.ini"
  !insertmacro DeleteInstalledFile "internal\Uninstall.exe"
  ; Missing files may set NSIS's error flag. Check the owned files themselves
  ; before removing the installation record, so an incomplete install can be
  ; uninstalled while a genuinely locked application remains recoverable.
  IfFileExists "$INSTDIR\PathMan.exe" UninstallFilesLocked
  IfFileExists "$INSTDIR\bin\pathman.exe" UninstallFilesLocked
  IfFileExists "$INSTDIR\internal\PathMan.Service.exe" UninstallFilesLocked
  IfFileExists "$INSTDIR\internal\PathMan.Shim.exe" UninstallFilesLocked
  IfFileExists "$INSTDIR\internal\PathMan.SetupActions.dll" UninstallFilesLocked
  Goto UninstallFilesRemoved
  UninstallFilesLocked:
    StrCpy $0 5
    MessageBox MB_OK|MB_ICONSTOP "$(MaintenanceFailed)" /SD IDOK
    SetErrorLevel 1603
    Abort
  UninstallFilesRemoved:
  DeleteRegKey SHCTX "${UNINSTALLKEY}"
  DeleteRegKey SHCTX "${APPKEY}"
  RMDir "$INSTDIR\bin"
  RMDir "$INSTDIR\internal"
  RMDir "$INSTDIR"
SectionEnd
