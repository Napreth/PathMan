Unicode true
ManifestDPIAware true
ManifestSupportedOS all
!include MUI2.nsh
!include LogicLib.nsh
!include FileFunc.nsh
!include x64.nsh
!include WordFunc.nsh
!define MUI_ICON "${__FILEDIR__}\..\src\Pathman.Gui\Assets\PathMan.ico"
!define MUI_UNICON "${MUI_ICON}"
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
VIProductVersion "${VERSION_NUMERIC}.0"
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
Var ApplicationsApproved
Var ApplicationsDialog
Var ApplicationsList
Var ApplicationsAutomatic
Var ApplicationsManual
Var ApplicationsStatus
Var WaitingApplications
Var UpdateMode

!define MUI_CUSTOMFUNCTION_ABORT RestoreBackground
!define MUI_CUSTOMFUNCTION_UNABORT un.RestoreBackground
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
Page custom ApplicationsPage ApplicationsLeave
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
UninstPage custom un.ApplicationsPage un.ApplicationsLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
SetFont /LANG=1033 "Segoe UI" 9
SetFont /LANG=2052 "Microsoft YaHei UI" 9

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
LangString DowngradeBlocked ${LANG_ENGLISH} "A newer version of PathMan ($1) is installed. Uninstall it before installing ${VERSION}. Your user data will be preserved."
LangString DowngradeBlocked ${LANG_SIMPCHINESE} "已安装更高版本的 PathMan（$1）。请先卸载，再安装 ${VERSION}。用户数据会保留。"
LangString UnownedDirectory ${LANG_ENGLISH} "This folder contains an unregistered PathMan installation. Choose another folder or uninstall the previous copy first."
LangString UnownedDirectory ${LANG_SIMPCHINESE} "此目录包含未登记的 PathMan 程序。请选择其他目录，或先卸载原有副本。"

LangString ApplicationsTitle ${LANG_ENGLISH} "Close running applications"
LangString ApplicationsTitle ${LANG_SIMPCHINESE} "关闭正在运行的程序"
LangString ApplicationsSubtitle ${LANG_ENGLISH} "PathMan needs to close before setup can continue."
LangString ApplicationsSubtitle ${LANG_SIMPCHINESE} "请先关闭 PathMan，再继续安装。"
LangString ApplicationsDescription ${LANG_ENGLISH} "The following applications need to close before setup can continue. Save your work first. Background monitoring will resume after installation if it was running."
LangString ApplicationsDescription ${LANG_SIMPCHINESE} "以下程序需要关闭后才能继续安装，请先保存工作。此前运行的后台程序将在安装完成后重新启动。"
LangString UnApplicationsDescription ${LANG_ENGLISH} "The following applications need to close before uninstall can continue. Save your work first."
LangString UnApplicationsDescription ${LANG_SIMPCHINESE} "以下程序需要关闭后才能继续卸载，请先保存工作。"
LangString ApplicationsAutomatic ${LANG_ENGLISH} "&Automatically close the applications"
LangString ApplicationsAutomatic ${LANG_SIMPCHINESE} "自动关闭这些程序(&A)"
LangString ApplicationsManual ${LANG_ENGLISH} "I will close the applications &manually"
LangString ApplicationsManual ${LANG_SIMPCHINESE} "我将手动关闭这些程序(&M)"
LangString ApplicationsManualWaiting ${LANG_ENGLISH} "Waiting for you to close the applications..."
LangString ApplicationsManualWaiting ${LANG_SIMPCHINESE} "正在等待你关闭这些程序……"
LangString ApplicationsScanError ${LANG_ENGLISH} "Unable to refresh the application list. Close PathMan manually, then click Continue to retry."
LangString ApplicationsScanError ${LANG_SIMPCHINESE} "无法刷新程序列表。请手动关闭 PathMan 后单击“继续”重试。"
LangString ApplicationsContinue ${LANG_ENGLISH} "&Continue"
LangString ApplicationsContinue ${LANG_SIMPCHINESE} "继续(&C)"
LangString ApplicationsWaiting ${LANG_ENGLISH} "Waiting for command-line tasks to finish..."
LangString ApplicationsWaiting ${LANG_SIMPCHINESE} "正在等待命令行任务结束……"
LangString ApplicationsError ${LANG_ENGLISH} "Unable to close PathMan (Windows error $0). Close it manually, then click Continue to retry."
LangString ApplicationsError ${LANG_SIMPCHINESE} "无法关闭 PathMan（Windows 错误 $0）。请手动关闭后单击“继续”重试。"

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
  StrCpy $ApplicationsApproved 0
  StrCpy $RestartBackground 0
  StrCpy $UpdateMode 0
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
      ReadINIStr $ApplicationsApproved "$WorkerConfig" "Setup" "ApplicationsApproved"
      ReadINIStr $UpdateMode "$WorkerConfig" "Setup" "UpdateMode"
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
  WriteINIStr "$WorkerConfig" "Setup" "ApplicationsApproved" "$ApplicationsApproved"
  WriteINIStr "$WorkerConfig" "Setup" "UpdateMode" "$UpdateMode"
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
  ClearErrors
  ${GetOptions} $Parameters "/Update" $1
  ${IfNot} ${Errors}
    ; The application requests unattended maintenance only in its registered folder.
    ${If} $0 != "$INSTDIR"
    ${OrIf} $0 == ""
      SetErrorLevel 87
      Quit
    ${EndIf}
    StrCpy $UpdateMode 1
    StrCpy $ApplicationsApproved 1
  ${EndIf}
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
  Call CheckInstalledVersion
FunctionEnd

Function CheckInstalledVersion
  ${If} $UpdateMode == 1
    Return
  ${EndIf}
  ReadRegStr $OldDirectory SHCTX "${APPKEY}" "InstallDirectory"
  ${If} $OldDirectory == ""
    Return
  ${EndIf}
  ReadRegStr $1 SHCTX "${UNINSTALLKEY}" "DisplayVersion"
  ${If} $1 == ""
    StrCpy $1 "0.0.0"
  ${EndIf}
  System::Call '$Helper::CompareVersions(w r1, w "${VERSION}") i.r0'
  ${If} $0 == 1
    MessageBox MB_OK|MB_ICONSTOP "$(DowngradeBlocked)" /SD IDOK
    SetErrorLevel 1638
    Abort
  ${EndIf}
FunctionEnd

!macro EnsureApplicationsClosed Prefix
Function ${Prefix}ApplicationsPage
  StrCpy $ApplicationsApproved 0
  StrCpy $WaitingApplications 0
  System::Call '$Helper::RunningApplications(w "$INSTDIR") i.r0'
  ${If} $0 == 0
    Abort
  ${EndIf}
  System::Call '$Helper::BackgroundRunning(w "$INSTDIR") i.r0'
  ${If} $0 == 1
    StrCpy $RestartBackground 1
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "$(ApplicationsTitle)" "$(ApplicationsSubtitle)"
  nsDialogs::Create 1018
  Pop $ApplicationsDialog
  ${If} $ApplicationsDialog == error
    Abort
  ${EndIf}
  System::Call 'user32::GetSystemMetrics(i 49) i.r1'
  nsDialogs::CreateControl STATIC ${DEFAULT_STYLES}|${SS_ICON}|${SS_REALSIZECONTROL} 0 0 0 $1 $1 ""
  Pop $0
  System::Call 'user32::LoadImageW(p 0, p 32513, i 1, i r1, i r1, i 0x8000) p.r1'
  SendMessage $0 ${STM_SETICON} $1 0
!if "${Prefix}" == "un."
  ${NSD_CreateLabel} 20u 0 280u 32u "$(UnApplicationsDescription)"
!else
  ${NSD_CreateLabel} 20u 0 280u 32u "$(ApplicationsDescription)"
!endif
  Pop $0
  ${NSD_CreateListBox} 20u 35u 280u 64u ""
  Pop $ApplicationsList
  System::Call '$Helper::UpdateApplicationsList(w "$INSTDIR", p $ApplicationsList) i.r0'
  ${NSD_CreateFirstRadioButton} 20u 105u 280u 10u "$(ApplicationsAutomatic)"
  Pop $ApplicationsAutomatic
  ${NSD_Check} $ApplicationsAutomatic
  ${NSD_CreateAdditionalRadioButton} 20u 118u 280u 10u "$(ApplicationsManual)"
  Pop $ApplicationsManual
  ${NSD_CreateLabel} 20u 132u 280u 10u ""
  Pop $ApplicationsStatus
  GetDlgItem $1 $HWNDPARENT 1
  EnableWindow $1 1
  SendMessage $1 ${WM_SETTEXT} 0 "STR:$(ApplicationsContinue)"
  ${If} $Machine == 1
  ${AndIf} $Elevated == 0
    SendMessage $1 0x160C 0 1
  ${EndIf}
  GetFunctionAddress $0 ${Prefix}ApplicationsPoll
  nsDialogs::CreateTimer $0 500
  nsDialogs::Show
  GetFunctionAddress $0 ${Prefix}ApplicationsPoll
  nsDialogs::KillTimer $0
FunctionEnd
Function ${Prefix}ApplicationsPoll
  System::Call '$Helper::UpdateApplicationsList(w "$INSTDIR", p $ApplicationsList) i.r0'
  ${If} $0 < 0
    ${NSD_SetText} $ApplicationsStatus "$(ApplicationsScanError)"
    Return
  ${EndIf}
  ${If} $WaitingApplications == 1
    ${If} $0 == 0
      StrCpy $WaitingApplications 0
      GetDlgItem $1 $HWNDPARENT 1
      EnableWindow $1 1
      System::Call 'user32::PostMessageW(p r1, i ${BM_CLICK}, p 0, p 0)'
    ${EndIf}
  ${EndIf}
FunctionEnd
Function ${Prefix}ApplicationsLeave
  ${NSD_GetState} $ApplicationsManual $0
  ${If} $0 == ${BST_CHECKED}
    System::Call '$Helper::RunningApplications(w "$INSTDIR") i.r0'
    ${If} $0 < 0
      ${NSD_SetText} $ApplicationsStatus "$(ApplicationsScanError)"
      Abort
    ${ElseIf} $0 != 0
      StrCpy $WaitingApplications 1
      EnableWindow $ApplicationsAutomatic 0
      EnableWindow $ApplicationsManual 0
      ${NSD_SetText} $ApplicationsStatus "$(ApplicationsManualWaiting)"
      GetDlgItem $1 $HWNDPARENT 1
      EnableWindow $1 0
      Abort
    ${EndIf}
    Return
  ${EndIf}
  StrCpy $ApplicationsApproved 1
  ${If} $Machine == 1
  ${AndIf} $Elevated == 0
    Return ; The authorized silent worker closes elevated processes.
  ${EndIf}
  System::Call '$Helper::StopApplications(w "$INSTDIR") i.r0'
  ${If} $0 == 170
    StrCpy $WaitingApplications 1
    EnableWindow $ApplicationsAutomatic 0
    EnableWindow $ApplicationsManual 0
    ${NSD_SetText} $ApplicationsStatus "$(ApplicationsWaiting)"
    GetDlgItem $1 $HWNDPARENT 1
    EnableWindow $1 0
    Abort
  ${ElseIf} $0 != 0
    ${NSD_SetText} $ApplicationsStatus "$(ApplicationsError)"
    Abort
  ${EndIf}
FunctionEnd
Function ${Prefix}EnsureApplicationsClosed
  System::Call '$Helper::RunningApplications(w "$INSTDIR") i.r0'
  ${If} $0 == 0
    Return
  ${EndIf}
  ; Silent installs require explicit authorization from the visible wizard.
  ${If} $ApplicationsApproved != 1
    SetErrorLevel 1602
    Abort
  ${EndIf}
  StrCpy $2 0
  ${Do}
    System::Call '$Helper::StopApplications(w "$INSTDIR") i.r0'
    ${If} $0 == 0
      Return
    ${EndIf}
    ${If} $0 != 170
      ${ExitDo}
    ${EndIf}
    Sleep 100
    IntOp $2 $2 + 1
  ${LoopWhile} $2 < 300
  SetErrorLevel 1603
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
  ${If} $UpdateMode == 1
  ${AndIf} $Worker == 0
    Call LaunchPathMan
  ${EndIf}
FunctionEnd
Function .onInstSuccess
  ${If} $UpdateMode == 1
  ${AndIf} $Worker == 0
    Call LaunchPathMan
  ${EndIf}
FunctionEnd
Function un.onUninstFailed
  Call un.RestoreBackground
FunctionEnd

Section "PathMan" Main
  SectionIn RO
  Call DirectoryLeave
  ${If} $Worker == 0
    Call CheckInstalledVersion
    System::Call '$Helper::BackgroundRunning(w "$INSTDIR") i.r0'
    ${If} $0 == 1
      StrCpy $RestartBackground 1
    ${EndIf}
  ${EndIf}
  ${If} $Machine != 1
  ${OrIf} $Elevated != 0
    Call EnsureApplicationsClosed
  ${EndIf}
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
  File /r /x PathMan.Shim.exe "${PUBLISH_DIR}\*"
  SetOutPath "$INSTDIR\internal"
  File /oname=PathMan.Shim.new.exe "${PUBLISH_DIR}\internal\PathMan.Shim.exe"
  System::Call '$Helper::ReplaceShim(w "$INSTDIR\internal\PathMan.Shim.new.exe", w "$INSTDIR\internal\PathMan.Shim.exe") i.r0'
  ${If} $0 != 0
    Delete "$INSTDIR\internal\PathMan.Shim.new.exe"
    MessageBox MB_OK|MB_ICONSTOP "$(MaintenanceFailed)" /SD IDOK
    SetErrorLevel 1603
    Abort
  ${EndIf}
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
    CreateShortcut "$SMPROGRAMS\$StartMenuFolder\PathMan.lnk" "$INSTDIR\PathMan.exe" "" "$INSTDIR\PathMan.exe" 0
    CreateShortcut "$SMPROGRAMS\$StartMenuFolder\Uninstall PathMan.lnk" "$INSTDIR\internal\Uninstall.exe" "/$MultiUser.InstallMode" "$INSTDIR\internal\Uninstall.exe" 0
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
  CreateShortcut "$OriginalDesktop\PathMan.lnk" "$INSTDIR\PathMan.exe" "" "$INSTDIR\PathMan.exe" 0
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
    ${If} $0 == 1
      StrCpy $RestartBackground 1
    ${EndIf}
  ${EndIf}
  ${If} $Machine != 1
  ${OrIf} $Elevated != 0
    Call un.EnsureApplicationsClosed
  ${EndIf}
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
  !insertmacro DeleteInstalledFile "THIRD-PARTY-NOTICES.txt"
  !insertmacro DeleteInstalledFile "licenses\dotnet-THIRD-PARTY-NOTICES.txt"
  !insertmacro DeleteInstalledFile "bin\pathman.exe"
  !insertmacro DeleteInstalledFile "internal\PathMan.Service.exe"
  System::Call '$Helper::RemoveShim(w "$INSTDIR") i.r0'
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
  RMDir "$INSTDIR\licenses"
  RMDir "$INSTDIR"
SectionEnd
