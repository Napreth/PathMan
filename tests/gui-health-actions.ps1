[CmdletBinding()]
param([string]$ResultPath='C:\PathManHealthTest\results.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Use only the disposable Windows VM and restore its initial checkpoint.'}
if($PrepareFixture){
 Get-Process PathMan -ErrorAction SilentlyContinue | Stop-Process -Force
 $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
 try{
  foreach($pair in @{Path='%DUP_A_PATH%;%DUP_B_PATH%';PATHMAN_EMPTY_PATH='C:\before;;C:\after;';DUP_A_PATH='C:\same;C:\unique-a';DUP_B_PATH='C:\same;C:\unique-b';ONLY_USER='C:\user'}.GetEnumerator()){
   $user.SetValue($pair.Key,$pair.Value,[Microsoft.Win32.RegistryValueKind]::ExpandString)
  }
  $user.SetValue('TYPE_REF','%USERPROFILE%\tools',[Microsoft.Win32.RegistryValueKind]::String)
  $user.DeleteValue('PATHMAN_CREATED',$false)
 }finally{$user.Dispose()}
 $machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true)
 try{
  $machine.SetValue('Path','%SystemRoot%\System32;%SystemRoot%;%SystemRoot%\System32\Wbem;C:\software;C:\SOFTWARE\',[Microsoft.Win32.RegistryValueKind]::ExpandString)
  $machine.SetValue('BAD_SCOPE','%ONLY_USER%',[Microsoft.Win32.RegistryValueKind]::ExpandString)
  $machine.SetValue('MACHINE_TYPE','%SystemRoot%\tools',[Microsoft.Win32.RegistryValueKind]::String)
  $machine.DeleteValue('SOFTWARE_PATH',$false)
 }finally{$machine.Dispose()}
 $data=Join-Path $env:LOCALAPPDATA 'PathMan';New-Item -ItemType Directory -Force $data | Out-Null
 @{preferences=@{language='zh-CN';theme='system';autoCheckUpdates=$false};groups=@(@{id='paths';name='路径变量';rule='paths'},@{id='system';name='系统变量';rule='builtin';collapsed=$true},@{id='other';name='其他变量';rule='fallback'},@{id='work';name='工作';scope='User';rule='manual'});assignments=@{'User:PATHMAN_EMPTY_PATH'='work';'User:TYPE_REF'='work'};revision=[guid]::NewGuid().ToString('N')}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $data 'environment.json') -Encoding UTF8
 return
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class HealthInput {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
 public static void Wheel(int delta){mouse_event(0x800,0,0,unchecked((uint)delta),UIntPtr.Zero);}
}
'@
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message);try{$message|Set-Content ($ResultPath+'.progress')}catch [IO.IOException]{}}
function WaitFor([scriptblock]$condition,[string]$message){for($i=0;$i -lt 1200;$i++){if(& $condition){return};Start-Sleep -Milliseconds 100};throw $message}
function Find($root,[string]$name,$type=$null){
 $c=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
 if($type){$c=[Windows.Automation.AndCondition]::new($c,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$type))}
 $root.FindFirst([Windows.Automation.TreeScope]::Descendants,$c)
}
function Id([string]$id){$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function SelectNode($node){$node.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 200}
function Click($element){$r=$element.Current.BoundingRectangle;[void][HealthInput]::SetCursorPos([int]($r.Left+$r.Width/2),[int]($r.Top+$r.Height/2));[HealthInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[HealthInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 250}
function Node([string]$name){Find (Id 'UserVariablesTree') $name ([Windows.Automation.ControlType]::TreeItem)}
function Invoke($element){$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 250}
function Go([string]$name){SelectNode (Find (Id 'Navigation') $name ([Windows.Automation.ControlType]::ListItem))}
function Rows{(Id 'ValuesList').FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))}
function Input{Find $window '行内编辑' ([Windows.Automation.ControlType]::Edit)}
function SetInput([string]$text){(Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text);(Input).SetFocus()}
function Key([byte]$code){[HealthInput]::keybd_event($code,0,0,[UIntPtr]::Zero);[HealthInput]::keybd_event($code,0,2,[UIntPtr]::Zero);Start-Sleep -Milliseconds 180}
function EditRow($index){SelectNode (Rows)[$index];Invoke (Id 'EditEntryButton');WaitFor {$null -ne (Input)} 'Inline input missing'}
function UserValue([string]$name){$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment');try{$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}}
function ValueKind([string]$name,[bool]$machine=$false){$key=if($machine){[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment')}else{[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')};try{$key.GetValueKind($name)}finally{$key.Dispose()}}
function Issue([string]$name){Find (Id 'IssueGroups') $name ([Windows.Automation.ControlType]::ListItem)}
function State{Get-Content (Join-Path $env:LOCALAPPDATA 'PathMan\environment.json') -Raw|ConvertFrom-Json}
function Window{Find ([Windows.Automation.AutomationElement]::RootElement) 'PathMan · 以非管理员身份运行' ([Windows.Automation.ControlType]::Window)}
function StartApp{Start-Process C:\PathManTestApp\PathMan.exe;WaitFor {$script:window=Window;$null -ne $window} 'Application did not launch';[void][HealthInput]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle);WaitFor {$null -ne (Node 'PATHMAN_EMPTY_PATH')} 'Variables did not load'}
function Save{Invoke (Id 'SaveButton');WaitFor {!(Id 'SaveButton').Current.IsEnabled} 'Save did not finish'}
function WaitHealth{WaitFor {(Id 'ScanButton').Current.IsEnabled} 'Health operation did not finish'}
try{
 StartApp
 SelectNode (Node 'PATHMAN_EMPTY_PATH')
 Assert ((Rows).Count -eq 4 -and (Rows)[1].Current.Name -eq '（空条目）' -and (Rows)[3].Current.Name -eq '（空条目）') 'Consecutive and trailing separators have visible empty rows'
 EditRow 1;Key 13
 Assert ((Rows).Count -eq 4 -and !(Id 'SaveButton').Current.IsEnabled) 'Entering an existing empty row and pressing Enter without editing preserves it'
 EditRow 1;Key 27
 Assert ((Rows).Count -eq 4 -and !(Id 'SaveButton').Current.IsEnabled) 'Escape preserves an unchanged existing empty row'
 EditRow 1;SetInput 'temporary';SetInput '';Key 13;Save
 Assert ((UserValue 'PATHMAN_EMPTY_PATH') -eq 'C:\before;C:\after;') 'A changed-and-cleared empty row removes exactly one separator position'
 EditRow 0;SetInput '';Key 13;Save
 Assert ((UserValue 'PATHMAN_EMPTY_PATH') -eq 'C:\after;') 'Clearing a populated row preserves other pre-existing empty entries'
 Go '健康检测';Invoke (Id 'ScanButton');WaitHealth
 $empty=Issue 'EmptyPathEntry:User:PATHMAN_EMPTY_PATH:2'
 Assert ($null -ne $empty -and $null -ne (Find $empty '上一行  C:\after' ([Windows.Automation.ControlType]::Text)) -and $null -ne (Find $empty '下一行  （列表结尾）' ([Windows.Automation.ControlType]::Text))) 'Empty findings show their exact previous and next row context'
 Invoke (Find $empty '查看详情' ([Windows.Automation.ControlType]::Button))
 Assert ($null -ne (Id 'RelatedVariables') -and $null -ne (Find (Id 'DetailActions') '删除空行' ([Windows.Automation.ControlType]::Button))) 'View details opens related variables and repair buttons in the current page'
 Invoke (Find (Id 'DetailsPanel') '返回' ([Windows.Automation.ControlType]::Button))
 $scroll=(Id 'HealthScroll').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern);$scroll.SetScrollPercent(-1,0)
 $r=(Id 'IssueGroups').Current.BoundingRectangle;[void][HealthInput]::SetCursorPos([int]($r.Left+80),[int]($r.Top+100));[HealthInput]::Wheel(-120);Start-Sleep -Milliseconds 250
 Assert ($scroll.Current.VerticalScrollPercent -gt 0) 'Health overview wheel scrolling works over nested issue content'
 Invoke (Find $empty '忽略' ([Windows.Automation.ControlType]::Button))
 WaitFor {@((State).ignoredHealthIssues).Count -eq 1 -and $null -eq (Issue 'EmptyPathEntry:User:PATHMAN_EMPTY_PATH:2')} 'Ignore did not hide the finding'
 Assert ((UserValue 'PATHMAN_EMPTY_PATH') -eq 'C:\after;') 'Ignoring a finding does not change the environment value'
 Get-Process PathMan | Stop-Process -Force;Start-Sleep -Milliseconds 300;StartApp;Go '健康检测';Invoke (Id 'ScanButton');WaitHealth
 Assert ($null -eq (Issue 'EmptyPathEntry:User:PATHMAN_EMPTY_PATH:2') -and (Id 'ShowIgnored').Current.Name -eq '显示已忽略（1）') 'Ignored findings persist after restarting the application'
 (Id 'ShowIgnored').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle();Start-Sleep -Milliseconds 200
 $empty=Issue 'EmptyPathEntry:User:PATHMAN_EMPTY_PATH:2';Invoke (Find $empty '恢复提醒' ([Windows.Automation.ControlType]::Button))
 WaitFor {@((State).ignoredHealthIssues).Count -eq 0} 'Restore ignore failed'
 $empty=Issue 'EmptyPathEntry:User:PATHMAN_EMPTY_PATH:2';Invoke (Find $empty '删除空行' ([Windows.Automation.ControlType]::Button))
 WaitFor {(UserValue 'PATHMAN_EMPTY_PATH') -eq 'C:\after'} 'Empty-row repair did not remove the trailing separator'
 WaitHealth
 Assert ($true) 'The health repair deletes one empty row and rechecks without a dialog'
 $duplicate=Issue 'Duplicate:User:Path:1'
 Assert ($null -ne (Find $duplicate '删除用户 DUP_A_PATH 第 1 行' ([Windows.Automation.ControlType]::Button)) -and $null -ne (Find $duplicate '删除用户 DUP_B_PATH 第 1 行' ([Windows.Automation.ControlType]::Button))) 'Duplicates offer separate deletion buttons for their actual source variables'
 Invoke (Find $duplicate '删除用户 DUP_A_PATH 第 1 行' ([Windows.Automation.ControlType]::Button))
 WaitFor {(UserValue 'DUP_A_PATH') -eq 'C:\unique-a'} 'Duplicate repair did not save'
 WaitHealth
 Assert ((UserValue 'Path') -eq '%DUP_A_PATH%;%DUP_B_PATH%' -and (UserValue 'DUP_B_PATH') -eq 'C:\same;C:\unique-b') 'A duplicate repair retains references and unique paths from both sources'
 $type=Issue 'RegistryType:User:TYPE_REF:0';Invoke (Find $type '修正类型' ([Windows.Automation.ControlType]::Button))
 WaitFor {(ValueKind 'TYPE_REF') -eq 'ExpandString'} 'Type repair failed'
 WaitHealth
 Assert ((UserValue 'TYPE_REF') -eq '%USERPROFILE%\tools') 'REG_EXPAND_SZ repair keeps the exact unexpanded reference text'
 $scopeError=Issue 'UserScopeReference:Machine:BAD_SCOPE:0'
 Assert ($null -ne $scopeError -and $null -ne (Find $scopeError '系统变量引用了仅在用户环境中可用的 %ONLY_USER%' ([Windows.Automation.ControlType]::Text))) 'Machine-to-user references appear as a separate error with a suggestion'
 $type=Issue 'RegistryType:Machine:MACHINE_TYPE:0';'WAITING_FOR_TYPE_UAC'|Set-Content ($ResultPath+'.progress');Invoke (Find $type '修正类型' ([Windows.Automation.ControlType]::Button))
 WaitFor {(ValueKind 'MACHINE_TYPE' $true) -eq 'ExpandString'} 'Confirm the type-repair UAC in the VM'
 WaitHealth
 Assert ($true) 'System type repairs use the existing guarded UAC save path'
 Go '变量管理';SelectNode (Node '工作')
 Invoke (Find (Id 'UserActions') '新建' ([Windows.Automation.ControlType]::Button))
 $menu=$null;WaitFor {$script:menu=Find ([Windows.Automation.AutomationElement]::RootElement) '新建环境变量' ([Windows.Automation.ControlType]::MenuItem);$null -ne $menu} 'User new menu missing';Invoke $menu
 $dialog=$null;WaitFor {$script:dialog=Find ([Windows.Automation.AutomationElement]::RootElement) '新建用户环境变量' ([Windows.Automation.ControlType]::Window);$null -ne $dialog} 'New user dialog missing'
 function DialogId([string]$id){$dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
 Assert ($null -eq (DialogId 'VariableScopeInput') -and $null -ne (DialogId 'VariableGroupInput')) 'New-variable dialogs inherit their scope and offer a group menu instead'
 $group=DialogId 'VariableGroupInput';$selected=$group.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
 Assert ($selected[0].Current.Name -eq '其他变量') 'New variables default to Other even when another group was selected'
 $group.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand();Start-Sleep -Milliseconds 200
 Click (Find ([Windows.Automation.AutomationElement]::RootElement) '工作' ([Windows.Automation.ControlType]::ListItem))
 (DialogId 'VariableNameInput').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('PATHMAN_CREATED')
 (DialogId 'VariableValueInput').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('sample')
 Invoke (Find $dialog '确定' ([Windows.Automation.ControlType]::Button));WaitFor {(UserValue 'PATHMAN_CREATED') -eq 'sample'} 'New variable save failed'
 Assert ((State).assignments.'User:PATHMAN_CREATED' -eq 'work') 'The chosen group assignment is saved in the inherited user scope'
 Invoke (Find (Id 'SystemActions') '新建' ([Windows.Automation.ControlType]::Button))
 WaitFor {$script:menu=Find ([Windows.Automation.AutomationElement]::RootElement) '新建环境变量' ([Windows.Automation.ControlType]::MenuItem);$null -ne $menu} 'System new menu missing';Invoke $menu
 WaitFor {$script:dialog=Find ([Windows.Automation.AutomationElement]::RootElement) '新建系统环境变量' ([Windows.Automation.ControlType]::Window);$null -ne $dialog} 'New system dialog missing'
 Assert ($null -eq (DialogId 'VariableScopeInput') -and $null -ne (DialogId 'VariableGroupInput')) 'System creation also inherits the selected scope without a scope picker'
 $group=DialogId 'VariableGroupInput';$group.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand();Start-Sleep -Milliseconds 200
 Assert ($null -eq (Find ([Windows.Automation.AutomationElement]::RootElement) '工作' ([Windows.Automation.ControlType]::ListItem))) 'The system group menu excludes groups belonging only to the user scope'
 Key 27;Invoke (Find $dialog '取消' ([Windows.Automation.ControlType]::Button))
 Invoke (Id 'CleanupButton')
 Assert ($null -ne (Find $window '修改前' ([Windows.Automation.ControlType]::Text)) -and $null -ne (Find $window '修改后' ([Windows.Automation.ControlType]::Text))) 'Cleanup offers labeled before and after columns'
 $before=Find $window 'C:\SOFTWARE\' ([Windows.Automation.ControlType]::Text);$after=Find $window '%SOFTWARE_PATH%' ([Windows.Automation.ControlType]::Text)
 Assert ($before.Current.BoundingRectangle.Left -lt $after.Current.BoundingRectangle.Left) 'Removed and added values occupy the left and right diff columns'
 @{Passed=$true;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
}catch{@{Passed=$false;Error=$_.Exception.Message;Line=$_.InvocationInfo.ScriptLineNumber;Stack=$_.ScriptStackTrace;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8;throw}
