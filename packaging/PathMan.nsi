Unicode true
ManifestDPIAware true
ManifestSupportedOS all
!include MUI2.nsh
!include LogicLib.nsh
!include FileFunc.nsh
!include x64.nsh
!include WordFunc.nsh
!define APPKEY "Software\PathMan"
!define UNINSTALLKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan"

!define MULTIUSER_EXECUTIONLEVEL Highest
!define MULTIUSER_MUI
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
; Keep one unelevated standard wizard. Machine-wide writes use a silent,
; authorized elevated child; language and scope changes never replace the UI.
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

Var StartMenuFolder
Var Parameters
Var ScopeArgument
Var Elevated
Var Machine
Var Helper
Var UserLocalData
Var OriginalDesktop
Var OldDirectory
Var CommandDirectory
Var SetupMutex
Var RestartBackground
Var Worker
Var WorkerConfig

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
!define MUI_PAGE_CUSTOMFUNCTION_SHOW ReadyShow
!insertmacro MUI_PAGE_STARTMENU Application $StartMenuFolder
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "$(DesktopShortcut)"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_RUN_FUNCTION CreateDesktopShortcut
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "$(OpenPathMan)"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION LaunchPathMan
!define MUI_PAGE_CUSTOMFUNCTION_SHOW FinishShow
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
LangString InvalidDirectory ${LANG_ENGLISH} "Choose a valid installation folder. The LocalAppData volume must support hard links."
LangString InvalidDirectory ${LANG_SIMPCHINESE} "请选择有效的安装目录。LocalAppData 所在卷必须支持硬链接。"
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
LangString AlreadyInstalling ${LANG_ENGLISH} "Another PathMan setup is running. Close it and try again."
LangString AlreadyInstalling ${LANG_SIMPCHINESE} "另一个 PathMan 安装程序正在运行。请关闭后重试。"
LangString CloseApplications ${LANG_ENGLISH} "PathMan or its background/alias commands are running. Save your work, then click OK to close PathMan and continue. Running alias commands must be closed manually."
LangString CloseApplications ${LANG_SIMPCHINESE} "PathMan、后台程序或别名命令正在运行。请保存工作，单击“确定”关闭 PathMan 后继续。正在运行的别名命令需手动关闭。"
LangString ApplicationsStillRunning ${LANG_ENGLISH} "PathMan is still running. Finish or cancel any unsaved edits, close running CLI/alias commands, then click OK to retry. No files have been replaced."
LangString ApplicationsStillRunning ${LANG_SIMPCHINESE} "PathMan 仍在运行。请处理未保存的更改，关闭 CLI/别名命令，然后单击“确定”重试。程序文件尚未替换。"
LangString DowngradeBlocked ${LANG_ENGLISH} "A newer version of PathMan ($1) is installed. Uninstall it before installing ${VERSION}. Your user data will be preserved."
LangString DowngradeBlocked ${LANG_SIMPCHINESE} "已安装更高版本的 PathMan（$1）。请先卸载，再安装 ${VERSION}。用户数据会保留。"
LangString UpgradeInstall ${LANG_ENGLISH} "Upgrade PathMan from $1 to ${VERSION}? Settings, groups, aliases and the installation folder will be preserved."
LangString UpgradeInstall ${LANG_SIMPCHINESE} "将 PathMan 从 $1 升级到 ${VERSION}？设置、分组、别名及安装目录会保留。"
LangString OverwriteInstall ${LANG_ENGLISH} "PathMan ${VERSION} is already installed. Reinstall it using this package (${ARCH}, ${PACKAGE_TYPE})? User data will be preserved."
LangString OverwriteInstall ${LANG_SIMPCHINESE} "已安装 PathMan ${VERSION}。是否用此安装包（${ARCH}，${PACKAGE_TYPE}）覆盖安装？用户数据会保留。"
LangString UnownedDirectory ${LANG_ENGLISH} "This folder contains an unregistered PathMan installation. Choose another folder or uninstall the previous copy first."
LangString UnownedDirectory ${LANG_SIMPCHINESE} "此目录包含未登记的 PathMan 程序。请选择其他目录，或先卸载原有副本。"

Function GetScope
  StrCpy $Machine 0
  ${If} $MultiUser.InstallMode == "AllUsers"
    StrCpy $Machine 1
  ${EndIf}
FunctionEnd

!macro SetupInitialization Prefix
Function ${Prefix}InitializeSetup
  SetRegView 32
  ${GetParameters} $Parameters
  System::Call 'kernel32::GetUserDefaultUILanguage() i.r0'
  StrCpy $LANGUAGE ${LANG_ENGLISH}
  ${If} $0 == ${LANG_SIMPCHINESE}
    StrCpy $LANGUAGE ${LANG_SIMPCHINESE}
  ${EndIf}
  ClearErrors
  ${GetOptions} $Parameters "/LANG=" $0
  ${IfNot} ${Errors}
    StrCpy $LANGUAGE $0
  ${EndIf}
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=PathMan.SetupActions.dll "${HELPER_DLL}"
  StrCpy $Helper "$PLUGINSDIR\PathMan.SetupActions.dll"
  System::Call 'shell32::IsUserAnAdmin() i.r0'
  StrCpy $Elevated $0
  StrCpy $Worker 0
  ClearErrors
  ${GetOptions} $Parameters "/SetupWorker=" $0
  ${IfNot} ${Errors}
    System::Call '$Helper::JoinSetupWorker(w r0) p.r1'
    StrCpy $SetupMutex $1
    ${If} $SetupMutex != 0
    ${AndIf} $Elevated != 0
      IfSilent +2 0
        Quit
      StrCpy $Worker 1
      ${GetOptions} $Parameters "/SetupConfig=" $WorkerConfig
      ReadINIStr $LANGUAGE "$WorkerConfig" "Setup" "Language"
      ReadINIStr $CommandDirectory "$WorkerConfig" "Setup" "Directory"
      ReadINIStr $StartMenuFolder "$WorkerConfig" "Setup" "StartMenuFolder"
    ${Else}
      SetErrorLevel 1618
      Quit
    ${EndIf}
  ${Else}
    System::Call '$Helper::BeginSetup() p.r0'
    StrCpy $SetupMutex $0
    ${If} $SetupMutex == 0
      MessageBox MB_OK|MB_ICONSTOP "$(AlreadyInstalling)" /SD IDOK
      SetErrorLevel 1618
      Quit
    ${EndIf}
  ${EndIf}
FunctionEnd
Function ${Prefix}WriteWorkerConfiguration
  StrCpy $WorkerConfig "$PLUGINSDIR\worker.ini"
  WriteINIStr "$WorkerConfig" "Setup" "Language" "$LANGUAGE"
  WriteINIStr "$WorkerConfig" "Setup" "Directory" "$INSTDIR"
  WriteINIStr "$WorkerConfig" "Setup" "StartMenuFolder" "$StartMenuFolder"
FunctionEnd
!macroend
!insertmacro SetupInitialization ""
!insertmacro SetupInitialization "un."

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
  StrCpy $OriginalDesktop "$DESKTOP"
  Call InitializeSetup
  StrCpy $ScopeArgument ""
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
  ReadRegStr $0 HKLM "${APPKEY}" "InstallDirectory"
  ${If} $ScopeArgument == ""
  ${AndIf} $0 != ""
    StrCpy $ScopeArgument "/AllUsers"
  ${EndIf}
  !insertmacro MULTIUSER_INIT
  ; MultiUser's stock page otherwise hides AllUsers for a standard account.
  ; The choice is available to everyone; ScopeLeave obtains real UAC privileges.
  StrCpy $MultiUser.Privileges "Admin"
  ${If} $ScopeArgument == "/AllUsers"
    Call MultiUser.InstallMode.AllUsers
  ${ElseIf} $ScopeArgument == "/CurrentUser"
    Call MultiUser.InstallMode.CurrentUser
  ${EndIf}
  ${If} $CommandDirectory != ""
    StrCpy $INSTDIR $CommandDirectory
  ${EndIf}
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
  GetDlgItem $1 $HWNDPARENT 1
  SendMessage $1 0x160C 0 0 ; No elevation until the Install button.
FunctionEnd

Function FinishShow
  Call UpdateScopeShield
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
  ${If} $CommandDirectory != ""
    StrCpy $INSTDIR $CommandDirectory
  ${EndIf}
  Call UpdateScopeShield
FunctionEnd

Function ReadyShow
  Call GetScope
  GetDlgItem $1 $HWNDPARENT 1
  ${If} $Machine == 1
  ${AndIf} $Elevated == 0
    SendMessage $1 0x160C 0 1
  ${Else}
    SendMessage $1 0x160C 0 0
  ${EndIf}
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
  ${If} $OldDirectory == ""
    ${If} ${FileExists} "$INSTDIR\PathMan.exe"
      MessageBox MB_OK|MB_ICONSTOP "$(UnownedDirectory)" /SD IDOK
      SetErrorLevel 1638
      Abort
    ${EndIf}
  ${EndIf}
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

Function ConfirmMaintenance
  ReadRegStr $OldDirectory SHCTX "${APPKEY}" "InstallDirectory"
  ${If} $OldDirectory == ""
    Return
  ${EndIf}
  ReadRegStr $1 SHCTX "${UNINSTALLKEY}" "DisplayVersion"
  ${If} $1 == ""
    StrCpy $1 "0.0.0"
  ${EndIf}
  ${VersionCompare} "$1" "${VERSION}" $0
  ${If} $0 == 1
    MessageBox MB_OK|MB_ICONSTOP "$(DowngradeBlocked)" /SD IDOK
    SetErrorLevel 1638
    Abort
  ${ElseIf} $0 == 0
    MessageBox MB_OKCANCEL|MB_ICONQUESTION "$(OverwriteInstall)" /SD IDOK IDOK MaintenanceConfirmed
  ${Else}
    MessageBox MB_OKCANCEL|MB_ICONQUESTION "$(UpgradeInstall)" /SD IDOK IDOK MaintenanceConfirmed
  ${EndIf}
  SetErrorLevel 1602
  Abort
  MaintenanceConfirmed:
FunctionEnd

!macro EnsureApplicationsClosed Prefix
Function ${Prefix}EnsureApplicationsClosed
  System::Call '$Helper::RunningApplications(w "$INSTDIR") i.r0'
  ${If} $0 == 0
    Return
  ${EndIf}
  MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "$(CloseApplications)" /SD IDCANCEL IDOK CloseApplicationsTry
  SetErrorLevel 1602
  Abort
  CloseApplicationsTry:
  System::Call '$Helper::StopApplications(w "$INSTDIR") i.r0'
  ${If} $0 == 0
    Return
  ${EndIf}
  MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "$(ApplicationsStillRunning)" /SD IDCANCEL IDOK CloseApplicationsTry
  SetErrorLevel 1602
  Abort
FunctionEnd
Function ${Prefix}RestoreBackground
  ${If} $RestartBackground == 1
    ; Explorer uses the desktop user's token even when setup was elevated.
    ExecShell "open" "$WINDIR\explorer.exe" '$\"$INSTDIR\internal\PathMan.Service.exe$\"'
    StrCpy $RestartBackground 0
  ${EndIf}
FunctionEnd
!macroend
!insertmacro EnsureApplicationsClosed ""
!insertmacro EnsureApplicationsClosed "un."

Function .onInstFailed
  Call RestoreBackground
FunctionEnd
Function un.onUninstFailed
  Call un.RestoreBackground
FunctionEnd

Section "PathMan" Main
  SectionIn RO
  Call DirectoryLeave
  ${If} $Worker == 0
    Call ConfirmMaintenance
    System::Call '$Helper::BackgroundRunning(w "$INSTDIR") i.r0'
    StrCpy $RestartBackground $0
  ${EndIf}
  Call EnsureApplicationsClosed
  ${If} $Machine == 1
  ${AndIf} $Elevated == 0
    Call WriteWorkerConfiguration
    System::Call '$Helper::RunElevatedSetup(p $HWNDPARENT, w "$EXEPATH", w "$WorkerConfig") i.r0'
    ${If} $0 != 0
      SetErrorLevel $0
      ${If} $0 != 1602
        MessageBox MB_OK|MB_ICONSTOP "$(MaintenanceFailed)" /SD IDOK
      ${EndIf}
      Abort
    ${EndIf}
    Goto InstallUserFinalization
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
  InstallUserFinalization:
  ${If} $Worker == 0
    System::Call '$Helper::RefreshShims(w "$INSTDIR", w "$UserLocalData") i.r0'
    ${If} $0 != 0
      MessageBox MB_OK|MB_ICONEXCLAMATION "$(MaintenanceFailed)" /SD IDOK
    ${EndIf}
    Call RestoreBackground
  ${EndIf}
  SetErrorLevel 0
SectionEnd

Function CreateDesktopShortcut
  CreateShortcut "$OriginalDesktop\PathMan.lnk" "$INSTDIR\PathMan.exe"
FunctionEnd
Function LaunchPathMan
  ; Explorer launches the desktop app at the desktop user's integrity level.
  ExecShell "open" "$WINDIR\explorer.exe" '$\"$INSTDIR\PathMan.exe$\"'
FunctionEnd

Function un.onInit
  SetRegView 32
  StrCpy $OriginalDesktop "$DESKTOP"
  Call un.InitializeSetup
  ; NSIS executes a temporary copy of the uninstaller. Its initial INSTDIR
  ; identifies the original uninstaller directory; EXEDIR points at TEMP.
  ${GetParent} "$INSTDIR" $INSTDIR
  StrCpy $OldDirectory $INSTDIR
  ReadINIStr $ScopeArgument "$INSTDIR\internal\install.ini" "PathMan" "Scope"
  ReadRegStr $0 HKLM "${APPKEY}" "InstallDirectory"
  ${If} $0 == $INSTDIR
    StrCpy $ScopeArgument "AllUsers"
  ${EndIf}
  !insertmacro MULTIUSER_UNINIT
  StrCpy $MultiUser.Privileges "Admin"
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
FunctionEnd

!macro DeleteInstalledFile RelativePath
  IfFileExists "$INSTDIR\${RelativePath}" 0 +2
    Delete "$INSTDIR\${RelativePath}"
!macroend

Section "Uninstall"
  ${If} $Worker == 0
    System::Call '$Helper::BackgroundRunning(w "$INSTDIR") i.r0'
    StrCpy $RestartBackground $0
  ${EndIf}
  Call un.EnsureApplicationsClosed
  ${If} $Machine == 1
  ${AndIf} $Elevated == 0
    Call un.WriteWorkerConfiguration
    System::Call '$Helper::RunElevatedSetup(p $HWNDPARENT, w "$INSTDIR\internal\Uninstall.exe", w "$WorkerConfig") i.r0'
    ${If} $0 != 0
      SetErrorLevel $0
      ${If} $0 != 1602
        MessageBox MB_OK|MB_ICONSTOP "$(MaintenanceFailed)" /SD IDOK
      ${EndIf}
      Abort
    ${EndIf}
    System::Call '$Helper::CleanupUserInstall(w "$INSTDIR", w "$OriginalDesktop") i.r0'
    SetErrorLevel 0
    Return
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
  ${If} $Worker == 0
    System::Call '$Helper::CleanupUserInstall(w "$INSTDIR", w "$OriginalDesktop") i.r0'
  ${EndIf}
  RMDir "$INSTDIR\bin"
  RMDir "$INSTDIR\internal"
  RMDir "$INSTDIR"
SectionEnd
