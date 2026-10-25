[CmdletBinding()]
param([string]$ResultPath='C:\PathManInteractionTest\cleanup-results.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine') { throw 'Run only in the disposable Windows VM, then restore its checkpoint.' }
$app='C:\PathManTestApp\PathMan.exe'
if($PrepareFixture) {
    Get-Process PathMan -ErrorAction SilentlyContinue | Where-Object Path -eq $app | Stop-Process -Force
    $machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true)
    try {
        $machine.SetValue('Path','%SystemRoot%\System32;%SystemRoot%;%SystemRoot%\System32\Wbem;%SystemRoot%\System32\WindowsPowerShell\v1.0\;%SystemRoot%\System32\OpenSSH;C:\Cleanup\App;C:\cleanup\app\;%DEVELOP_PATH%\;%EXTRA_PATH%;%SDK_PATH%;%JAVA_HOME%\bin;C:\Cleanup\Shared;C:\Windows\VendorPath',[Microsoft.Win32.RegistryValueKind]::ExpandString)
        $machine.SetValue('DEVELOP_PATH','C:\Cleanup\Dev;C:\Cleanup\Shared;C:\Cleanup\Shared\')
        $machine.SetValue('EXTRA_PATH','C:\Cleanup\Shared;D:\Extra')
        $machine.SetValue('JAVA_HOME','C:\Java')
        $machine.SetValue('SDK_HOME','C:\SDK')
        $machine.SetValue('SDK_PATH','%SDK_HOME%\tools',[Microsoft.Win32.RegistryValueKind]::ExpandString)
        $machine.SetValue('SOFTWARE_PATH','C:\Occupied')
        $machine.DeleteValue('SOFTWARE1_PATH',$false)
    } finally { $machine.Dispose() }
    $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
    try {
        $user.SetValue('Path','C:\Cleanup\User;C:\CLEANUP\USER\;%USER_TOOLS_PATH%;C:\Cleanup\UserTools',[Microsoft.Win32.RegistryValueKind]::ExpandString)
        $user.SetValue('USER_TOOLS_PATH','C:\Cleanup\UserTools;D:\UserTwo')
        $user.SetValue('PATHMAN_LIST','one;;two;  spaced  ;')
        $user.SetValue('PATHMAN_EDIT_PATH','C:\first;%USER_TOOLS_PATH%\;%USER_TOOLS_PATH%\bin;%USER_TOOLS_PATH%\lib\%USERPROFILE%',[Microsoft.Win32.RegistryValueKind]::ExpandString)
        $user.SetValue('PATHMAN_SCALAR_ORIGIN','one')
        $user.SetValue('PATHEXT','.EXE')
    } finally { $user.Dispose() }
    $data=Join-Path $env:LOCALAPPDATA 'PathMan'; New-Item -ItemType Directory -Force $data | Out-Null
    @{groups=@(@{id='paths';name='路径变量';rule='paths'},@{id='system';name='系统变量';rule='builtin';collapsed=$true},@{id='other';name='其他变量';rule='fallback'},@{id='work';name='工作';scope='User';rule='manual'});assignments=@{'User:PATHMAN_LIST'='work';'User:PATHMAN_EDIT_PATH'='work';'User:PATHMAN_SCALAR_ORIGIN'='work'};revision=[guid]::NewGuid().ToString('N')} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $data 'environment.json') -Encoding UTF8
    return
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CleanupInput {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
}
'@
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$message) { if(!$value) { throw $message }; $checks.Add($message); $message | Set-Content ($ResultPath+'.progress') }
function WaitFor([scriptblock]$condition,[string]$message) { for($i=0;$i -lt 1200;$i++) { if(& $condition) { return }; Start-Sleep -Milliseconds 100 }; throw $message }
function Find($root,[string]$name,$type=$null) {
    $c=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
    if($type) { $c=[Windows.Automation.AndCondition]::new($c,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$type)) }
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants,$c)
}
function Id([string]$id) { $window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)) }
function Node([string]$name) { Find (Id 'UserVariablesTree') $name ([Windows.Automation.ControlType]::TreeItem) }
function Rows { (Id 'ValuesList').FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)) }
function SelectNode($node) { $node.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 200 }
function Invoke($element) { $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 250 }
function Input { Find $window '行内编辑' ([Windows.Automation.ControlType]::Edit) }
function SetInput([string]$text) { WaitFor { $null -ne (Input) } 'Inline input missing'; (Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text); (Input).SetFocus() }
function Key([byte]$key) { [CleanupInput]::keybd_event($key,0,0,[UIntPtr]::Zero); [CleanupInput]::keybd_event($key,0,2,[UIntPtr]::Zero); Start-Sleep -Milliseconds 200 }
function DoubleClick($element) {
    $r=$element.Current.BoundingRectangle; [void][CleanupInput]::SetCursorPos([int]($r.Left+8),[int]($r.Top+$r.Height/2))
    for($i=0;$i -lt 2;$i++) { [CleanupInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [CleanupInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 90 }; Start-Sleep -Milliseconds 200
}
function UserValue([string]$name) { $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment'); try { $key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } finally { $key.Dispose() } }
function MachineValue([string]$name) { $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment'); try { $key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } finally { $key.Dispose() } }
function Toggle($element) { $element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 300 }
try {
    Start-Process $app
    $window=$null
    WaitFor { $script:window=Find ([Windows.Automation.AutomationElement]::RootElement) 'PathMan · 以非管理员身份运行' ([Windows.Automation.ControlType]::Window); $null -ne $window } 'Window did not open'
    [void][CleanupInput]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle)
    WaitFor { $null -ne (Node '工作') } 'Variables did not load'
    Assert ($null -eq (Node '系统变量')) 'User scope has no built-in system category'
    Assert ($null -ne (Find (Id 'SystemVariablesTree') '系统变量' ([Windows.Automation.ControlType]::TreeItem))) 'Machine scope retains its built-in system category'
    SelectNode (Find (Id 'SystemVariablesTree') 'SDK_PATH' ([Windows.Automation.ControlType]::TreeItem))
    Assert ((Rows).Count -eq 1 -and $null -ne (Find (Rows)[0] '转到引用变量' ([Windows.Automation.ControlType]::Button))) 'Single-entry path variables also show a list row with reference navigation'
    Invoke (Find (Rows)[0] '转到引用变量' ([Windows.Automation.ControlType]::Button))
    Assert ((Id 'VariableTitle').Current.Name -eq 'SDK_HOME') 'A single-entry path reference jumps to its variable'
    SelectNode (Node 'PATHEXT')
    DoubleClick (Find (Rows)[0] '.EXE' ([Windows.Automation.ControlType]::Text)); SetInput ''; Key 13
    Assert ((Rows).Count -eq 0) 'Clearing the only list item leaves an empty list'
    Invoke (Id 'SaveButton'); WaitFor { (UserValue 'PATHEXT') -eq '' -and !(Id 'SaveButton').Current.IsEnabled } 'Empty list was not saved'
    Assert ((Rows).Count -eq 0) 'Saving an empty list does not recreate a blank row'
    (Node '工作').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 200
    SelectNode (Node 'PATHMAN_LIST')
    Assert ((Rows).Count -eq 5) 'Existing list values retain empty entries'
    Assert ($null -eq (Find (Id 'ValuesList') '编辑' ([Windows.Automation.ControlType]::Button)) -and $null -eq (Find (Id 'ValuesList') '删除' ([Windows.Automation.ControlType]::Button))) 'List rows contain neither edit nor delete buttons'
    Assert ($null -eq (Id 'EditTextButton')) 'The text editing dialog button is removed'
    $copy=Id 'CopyValueButton'
    SelectNode (Rows)[0]; Invoke (Id 'AddEntryButton')
    WaitFor { $null -ne (Input) } 'New row did not activate'
    Assert ((Rows).Count -eq 6 -and (Input).Current.BoundingRectangle.Top -ge (Rows)[5].Current.BoundingRectangle.Top) 'New creates an active empty row at the bottom'
    $copy.SetFocus(); Start-Sleep -Milliseconds 300
    Assert ((Rows).Count -eq 5 -and !(Id 'SaveButton').Current.IsEnabled) 'Leaving an empty draft removes it without creating an unsaved change'
    Invoke (Id 'AddEntryButton'); SetInput 'new item'; $copy.SetFocus(); Start-Sleep -Milliseconds 300
    Assert ((Rows).Count -eq 6 -and (UserValue 'PATHMAN_LIST') -eq 'one;;two;  spaced  ;') 'Filled drafts are retained and staged until Save'
    Invoke (Id 'SaveButton'); WaitFor { (UserValue 'PATHMAN_LIST') -eq 'one;;two;  spaced  ;;new item' } 'New row was not saved'
    DoubleClick (Find (Rows)[0] 'one' ([Windows.Automation.ControlType]::Text)); SetInput ''; Key 13
    Assert ((Rows).Count -eq 5) 'Committing an emptied existing row removes the row'
    Invoke (Id 'SaveButton'); WaitFor { (UserValue 'PATHMAN_LIST') -eq ';two;  spaced  ;;new item' } 'Empty row deletion was not saved'
    Invoke (Id 'AddEntryButton'); SetInput 'cancelled'; Key 27
    Assert ((Rows).Count -eq 5 -and !(Id 'SaveButton').Current.IsEnabled) 'Escape cancels a new draft without retaining a blank row'
    Invoke (Id 'ToggleModeButton')
    Assert ((Id 'ToggleModeButton').Current.Name -eq '多行模式' -and !(Id 'SaveButton').Current.IsEnabled) 'Switching to text mode preserves the value without marking it dirty'
    (Id 'ScalarValueBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('x;;y;')
    Invoke (Id 'ToggleModeButton'); Assert ((Rows).Count -eq 4) 'Switching back to multiline mode preserves semicolon empty items'
    SelectNode (Rows)[2]; Invoke (Id 'DeleteEntryButton'); Invoke (Id 'SaveButton')
    WaitFor { (UserValue 'PATHMAN_LIST') -eq 'x;;' } 'Side delete button did not remove only the selected row'
    SelectNode (Node 'PATHMAN_EDIT_PATH')
    $jump=Find (Rows)[1] '转到引用变量' ([Windows.Automation.ControlType]::Button); Assert ($null -ne $jump) 'Pure references have a jump button'
    Invoke $jump; Assert ((Id 'VariableTitle').Current.Name -eq 'USER_TOOLS_PATH') 'Reference jumps select the saved variable in its correct scope'
    SelectNode (Node 'PATHMAN_EDIT_PATH'); Invoke (Find (Rows)[2] '转到引用变量' ([Windows.Automation.ControlType]::Button))
    Assert ((Id 'VariableTitle').Current.Name -eq 'USER_TOOLS_PATH') 'Embedded path references also support jumping'
    SelectNode (Node 'PATHMAN_EDIT_PATH'); Invoke (Find (Rows)[3] '转到引用变量' ([Windows.Automation.ControlType]::Button))
    $menuTarget=$null
    WaitFor { $script:menuTarget=Find ([Windows.Automation.AutomationElement]::RootElement) '用户 · USER_TOOLS_PATH' ([Windows.Automation.ControlType]::MenuItem); $null -ne $menuTarget } 'Multiple-reference menu did not open'
    Invoke $menuTarget
    Assert ((Id 'VariableTitle').Current.Name -eq 'USER_TOOLS_PATH') 'Multiple references use a target menu and preserve underscores in variable names'
    $originalMachine=MachineValue 'Path'; $originalUser=UserValue 'Path'; $originalDevelop=MachineValue 'DEVELOP_PATH'
    Invoke (Id 'CleanupButton')
    Assert ((MachineValue 'Path') -eq $originalMachine -and (UserValue 'Path') -eq $originalUser) 'Cleanup preview does not modify registry values'
    Assert ($null -eq (Id 'CleanupWarningsSurface') -and (Id 'ApplyCleanupButton').Current.IsEnabled) 'Cleanup has an explicit Apply action and no separate health-warning panel'
    'WAITING_FOR_CLEANUP_UAC' | Set-Content ($ResultPath+'.progress')
    Invoke (Id 'ApplyCleanupButton')
    WaitFor { (MachineValue 'Path') -like '*%SOFTWARE1_PATH%' } 'Cleanup did not finish (confirm the UAC prompt in the VM)'
    WaitFor { (Id 'CleanupStatus').Current.Name -like '清理完成*' } 'Cleanup UI did not finish'
    Assert ((MachineValue 'SOFTWARE1_PATH') -eq 'C:\Cleanup\App;%JAVA_HOME%\bin;C:\Windows\VendorPath') 'Machine cleanup moves only software entries and embedded references into the next free software variable'
    Assert ((UserValue 'Path') -eq 'C:\Cleanup\User;%USER_TOOLS_PATH%') 'User cleanup only removes duplicates and paths covered by references'
    Assert ((MachineValue 'DEVELOP_PATH') -eq $originalDevelop) 'Referenced variable content is left intact'
    Assert (!(Id 'ApplyCleanupButton').Current.IsEnabled) 'Cleanup remains idempotent after applying'
    Assert (@(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'PathMan\backups') -Filter 'path-cleanup-*.json').Count -gt 0) 'Cleanup saves a raw-value backup before applying'
    SelectNode (Find (Id 'Navigation') '健康检测' ([Windows.Automation.ControlType]::ListItem))
    Invoke (Find $window '系统 · DEVELOP_PATH' ([Windows.Automation.ControlType]::Hyperlink))
    Assert ((Id 'VariableTitle').Current.Name -eq 'DEVELOP_PATH') 'Health overview links to the affected variable after cleanup'
    SelectNode (Find (Id 'Navigation') '程序别名' ([Windows.Automation.ControlType]::ListItem))
    Toggle (Id 'EnableAliases')
    $shims=Join-Path $env:LOCALAPPDATA 'PathMan\shims'
    WaitFor { (UserValue 'Path').EndsWith($shims) } 'Alias switch did not append the shim directory'
    Assert (@((UserValue 'Path').Split(';') | Where-Object { $_ -eq $shims }).Count -eq 1) 'Enabling aliases appends exactly one shim directory at the end of user PATH'
    Toggle (Id 'EnableAliases'); WaitFor { (UserValue 'Path') -eq 'C:\Cleanup\User;%USER_TOOLS_PATH%' } 'Disabling aliases changed unrelated user PATH entries'
    SelectNode (Find (Id 'Navigation') '变量监控' ([Windows.Automation.ControlType]::ListItem))
    Assert ((Id 'StartupEnabled').Current.Name -eq '登录时自动运行后台程序') 'Startup has moved to monitoring with its requested label'
    Toggle (Id 'StartupEnabled')
    Assert ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PathMan).PathMan -eq '"C:\PathManTestApp\internal\PathMan.Service.exe"') 'The startup switch writes the current user login command'
    Toggle (Id 'StartupEnabled')
    Assert ($null -eq (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PathMan -ErrorAction SilentlyContinue)) 'Disabling startup removes its login command'
    SelectNode (Find (Id 'Navigation') '关于' ([Windows.Automation.ControlType]::ListItem)); Assert ($null -eq (Id 'StartupEnabled')) 'About no longer contains the startup setting'
    @{Passed=$true;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
} catch { @{Passed=$false;Error=$_.Exception.Message;Line=$_.InvocationInfo.ScriptLineNumber;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8; throw }
