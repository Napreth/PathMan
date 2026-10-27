[CmdletBinding()]
param([string]$AppDirectory='C:\PathManTestApp',[string]$ResultPath='C:\PathManKeyboardTest\results.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Use only the disposable Windows VM and restore its initial checkpoint.'}
$data=Join-Path $env:LOCALAPPDATA 'PathMan'
if($PrepareFixture){
 New-Item -ItemType Directory -Path $data,(Split-Path $ResultPath) -Force|Out-Null
 $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
 try{
  foreach($pair in @{EDIT_A='alpha';EDIT_B='beta';EDIT_C='gamma';EDIT_LIST='first;;second;third';EDIT_VALUE='original';EDIT_TARGET_B='keep-b';EDIT_TARGET_C='keep-c';PSModulePath='C:\modules;D:\modules';PATHEXT='.EXE;.CMD'}.GetEnumerator()){$user.SetValue($pair.Key,$pair.Value)}
  $path=[string]$user.GetValue('Path','',[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
  $shims=Join-Path $data 'shims';$path=(@($path.Split(';')|Where-Object {![Environment]::ExpandEnvironmentVariables($_).TrimEnd('\').Equals($shims,[StringComparison]::OrdinalIgnoreCase)}) -join ';')
  $user.SetValue('Path',$path.TrimEnd(';')+';'+$shims,[Microsoft.Win32.RegistryValueKind]::ExpandString)
 }finally{$user.Dispose()}
 $state=Get-Content (Join-Path $PSScriptRoot 'groups.json') -Raw -Encoding UTF8|ConvertFrom-Json
 $state.groups+=@{id='work';name='工作';rule='manual';scope='User'}
 $state|Add-Member -NotePropertyName assignments -NotePropertyValue @{'User:EDIT_A'='work';'User:EDIT_B'='work';'User:EDIT_C'='work';'User:EDIT_LIST'='work';'User:EDIT_VALUE'='work'}
 $state|Add-Member -NotePropertyName versionProjects -NotePropertyValue @(@{name='tool';variableName='EDIT_VALUE';type='User';versions=@{one='v1';two='v2';three='v3'}},@{name='extra-one';variableName='EDIT_TARGET_B';type='User';versions=@{one='x'}},@{name='extra-two';variableName='EDIT_TARGET_C';type='User';versions=@{one='y'}})
 $state|ConvertTo-Json -Depth 12|Set-Content (Join-Path $data 'environment.json') -Encoding UTF8
 . (Join-Path $PSScriptRoot 'shim-fixtures.ps1')
 foreach($name in 'alias-a','alias-b','alias-c','renamed'){Remove-TestShim $data $name}
 foreach($name in 'alias-a','alias-b','alias-c'){$null=New-TestShim $AppDirectory $data $name 'cmd.exe'}
 return
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class EditingInput{
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
 [DllImport("user32.dll")]public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
 [DllImport("user32.dll")]public static extern IntPtr GetDC(IntPtr h);
 [DllImport("user32.dll")]public static extern int ReleaseDC(IntPtr h,IntPtr dc);
 [DllImport("gdi32.dll")]public static extern uint GetPixel(IntPtr dc,int x,int y);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,string l,uint flags,uint timeout,out IntPtr result);
}
'@
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message);[IO.File]::WriteAllText($ResultPath+'.progress',$message)}
function WaitFor([scriptblock]$test,[string]$message){for($i=0;$i -lt 150;$i++){if(& $test){return};Start-Sleep -Milliseconds 150};throw $message}
function Find($root,[string]$name,$type=$null){if(!$root){return $null};$condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name);if($type){$condition=[Windows.Automation.AndCondition]::new($condition,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$type))};$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)}
function Id([string]$name){$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$name))}
function Rows($list){@($list.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))}
function SelectedCount($list){$list.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection().Length}
function Key([byte]$key){[EditingInput]::keybd_event($key,0,0,[UIntPtr]::Zero);[EditingInput]::keybd_event($key,0,2,[UIntPtr]::Zero);Start-Sleep -Milliseconds 180}
function Chord([byte[]]$modifiers,[byte]$key){foreach($modifier in $modifiers){[EditingInput]::keybd_event($modifier,0,0,[UIntPtr]::Zero)};Key $key;foreach($modifier in $modifiers){[EditingInput]::keybd_event($modifier,0,2,[UIntPtr]::Zero)};Start-Sleep -Milliseconds 250}
function Click($element,[byte[]]$modifiers=@()){
 if(!$element){throw 'Missing click target'};$scroll=$null;if($element.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$scroll)){$scroll.ScrollIntoView();Start-Sleep -Milliseconds 100}
 $r=$element.Current.BoundingRectangle;if($r.Width -lt 1){throw 'Empty click target'}
 foreach($modifier in $modifiers){[EditingInput]::keybd_event($modifier,0,0,[UIntPtr]::Zero)}
 $null=[EditingInput]::SetCursorPos([int]($r.Left+[Math]::Min(70,$r.Width/2)),[int]($r.Top+$r.Height/2))
 [EditingInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[EditingInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 foreach($modifier in $modifiers){[EditingInput]::keybd_event($modifier,0,2,[UIntPtr]::Zero)};Start-Sleep -Milliseconds 300
}
function Go([string]$name){(Find (Id 'Navigation') $name ([Windows.Automation.ControlType]::ListItem)).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 400}
function Node([string]$name,[string]$tree='UserVariablesTree'){Find (Id $tree) $name ([Windows.Automation.ControlType]::TreeItem)}
function Expand($node){$node.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand();Start-Sleep -Milliseconds 200}
function SelectNode($node){$node.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 250}
function Input{Find $window '行内编辑' ([Windows.Automation.ControlType]::Edit)}
function Row($list,[string]$name){@(Rows $list|Where-Object {$_.Current.Name -eq $name})|Select-Object -First 1}
function SetInput([string]$value){WaitFor {$null -ne (Input)} 'Inline editor missing';$box=Input;$box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value);$box.SetFocus();Key 13;Start-Sleep -Milliseconds 350}
function Raw([string]$name){$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment');try{$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}}
function State{Get-Content (Join-Path $data 'environment.json') -Raw -Encoding UTF8|ConvertFrom-Json}
function ClipboardText([string]$value,[switch]$Read){$runspace=[Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace();$runspace.ApartmentState='STA';$runspace.Open();$shell=[powershell]::Create();$shell.Runspace=$runspace;try{$null=$shell.AddScript({param($text,$read)Add-Type -AssemblyName System.Windows.Forms;if($read){[Windows.Forms.Clipboard]::GetText()}else{[Windows.Forms.Clipboard]::SetText($text)}}.ToString()).AddArgument($value).AddArgument([bool]$Read);$result=$shell.Invoke();if($Read){[string]$result[0]}}finally{$shell.Dispose();$runspace.Dispose()}}
function BackdropBrightness{
 $bounds=$window.Current.BoundingRectangle;$dc=[EditingInput]::GetDC([IntPtr]::Zero)
 try{$color=[EditingInput]::GetPixel($dc,[int]($bounds.Left+280),[int]($bounds.Top+45));(($color -band 255)*299+(($color -shr 8) -band 255)*587+(($color -shr 16) -band 255)*114)/1000}finally{$null=[EditingInput]::ReleaseDC([IntPtr]::Zero,$dc)}
}
try{
 $app=Start-Process (Join-Path $AppDirectory 'PathMan.exe') -PassThru
 WaitFor {$app.Refresh();$app.MainWindowHandle -ne 0} 'GUI did not open';$window=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 $null=[EditingInput]::SetForegroundWindow($app.MainWindowHandle);WaitFor {$null -ne (Id 'UserVariablesTree')} 'Variables page missing'
 Expand (Node '系统变量')
 foreach($name in 'TEMP','TMP','PATHEXT'){Assert ($null -ne (Find (Node '系统变量') $name ([Windows.Automation.ControlType]::TreeItem))) ($name+' belongs to user built-ins')}
 (Node '系统变量').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
 Expand (Node '其他变量');Assert ($null -ne (Find (Node '其他变量') 'PSModulePath' ([Windows.Automation.ControlType]::TreeItem))) 'PSModulePath defaults to other'
 Expand (Node '系统变量' 'SystemVariablesTree');foreach($name in 'TEMP','TMP','PATHEXT'){Assert ($null -ne (Find (Node '系统变量' 'SystemVariablesTree') $name ([Windows.Automation.ControlType]::TreeItem))) ($name+' belongs to machine built-ins')}
 Expand (Node '工作');Click (Node 'EDIT_A');Click (Node 'EDIT_B') @(17)
 Assert ((Node 'EDIT_A').Current.ItemStatus -eq '已选择' -and (Node 'EDIT_B').Current.ItemStatus -eq '已选择') 'Ctrl-click selects multiple tree variables'
 Click (Node 'EDIT_C') @(16);Assert ((Node 'EDIT_B').Current.ItemStatus -eq '已选择' -and (Node 'EDIT_C').Current.ItemStatus -eq '已选择' -and (Node 'EDIT_A').Current.ItemStatus -eq '') 'Shift-click selects a visible tree range'
 Key 113;Assert ($null -eq (Input)) 'F2 does not rename several selected variables at once'
 Key 46
 $dialogCondition=[Windows.Automation.AndCondition]::new([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'删除'))
 WaitFor {$script:confirmation=[Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants,$dialogCondition);$null -ne $script:confirmation} 'Tree multi-delete did not request confirmation'
 Assert ($null -ne $confirmation) 'Tree Delete requests one confirmation for the selection';Key 27
 SelectNode (Node 'EDIT_LIST');WaitFor {@(Rows (Id 'ValuesList')).Count -eq 4} 'List values missing'
 $list=Id 'ValuesList';$items=Rows $list;Click $items[0];Click $items[3] @(17)
 Assert ((SelectedCount $list) -eq 2) 'Ctrl-click selects nonadjacent value rows'
 Chord @(17) 67;Assert ((ClipboardText -Read) -eq "first`r`nthird") 'Ctrl+C copies every selected value in display order'
 Click $items[1] @(16);Assert ((SelectedCount $list) -eq 3) 'Shift-click extends a value-row range including an empty row'
 Key 46;WaitFor {@(Rows (Id 'ValuesList')).Count -eq 1} 'Bulk Delete failed';Assert ((Raw 'EDIT_LIST') -eq 'first;;second;third') 'Deleting draft rows does not write the registry before Save'
 Chord @(17) 90;WaitFor {@(Rows (Id 'ValuesList')).Count -eq 4} 'Undo did not restore all rows';Assert (@(Rows (Id 'ValuesList'))[1].Current.Name.Contains('空条目')) 'Undo restores the empty semicolon row'
 Chord @(17) 89;WaitFor {@(Rows (Id 'ValuesList')).Count -eq 1} 'Redo failed';Chord @(17) 90
 $list=Id 'ValuesList';$items=Rows $list;Click $items[2];Key 113;SetInput 'edited'
 WaitFor {$null -ne (Find (Id 'ValuesList') 'edited')} 'F2 edit failed';Chord @(17) 90;WaitFor {$null -ne (Find (Id 'ValuesList') 'second')} 'Undo after Enter lost keyboard focus'
 Chord @(17,16) 90;WaitFor {$null -ne (Find (Id 'ValuesList') 'edited')} 'Ctrl+Shift+Z failed';Chord @(17) 83
 WaitFor {(Raw 'EDIT_LIST') -eq 'first;;edited;third'} 'Ctrl+S failed'
 SelectNode (Node 'EDIT_VALUE');$box=Id 'ScalarValueBox';$box.SetFocus();Chord @(17) 65;ClipboardText 'changed';Chord @(17) 86;Chord @(17) 83
 WaitFor {(Raw 'EDIT_VALUE') -eq 'changed'} 'Ctrl+S inside text input failed'
 Go '程序别名';WaitFor {@(Rows (Id 'Aliases')).Count -eq 3} 'Alias fixtures missing'
 $list=Id 'Aliases';Click (Row $list 'alias-a');Key 27;Click (Row $list 'alias-b') @(17)
 Assert ((SelectedCount $list) -eq 2 -and $null -eq (Input)) 'Alias Ctrl-click selects without entering an inline editor'
 Key 46;WaitFor {@(Rows (Id 'Aliases')).Count -eq 1} 'Alias multi-delete failed';Chord @(17) 90;WaitFor {@(Rows (Id 'Aliases')).Count -eq 3} 'Alias undo failed'
 Chord @(17) 89;WaitFor {@(Rows (Id 'Aliases')).Count -eq 1} 'Alias redo failed';Chord @(17) 90;WaitFor {@(Rows (Id 'Aliases')).Count -eq 3} 'Alias restore failed'
 SelectNode (Row (Id 'Aliases') 'alias-a');Key 113;SetInput 'renamed'
 Assert (Test-Path (Join-Path $data 'shims\renamed.exe')) 'Alias F2 renames the actual alias';Chord @(17) 90;WaitFor {Test-Path (Join-Path $data 'shims\alias-a.exe')} 'Alias rename undo failed'
 Go '版本管理';WaitFor {@(Rows (Id 'ProjectsList')).Count -eq 3} 'Version projects missing';SelectNode (Row (Id 'ProjectsList') 'tool')
 $list=Id 'VersionsList';$items=Rows $list;Click $items[0];Click $items[2] @(17);Assert ((SelectedCount $list) -eq 2) 'Version rows support Ctrl-click'
 Key 46;WaitFor {@(Rows (Id 'VersionsList')).Count -eq 1} 'Version multi-delete failed';Chord @(17) 90;WaitFor {@(Rows (Id 'VersionsList')).Count -eq 3} 'Version undo failed'
 Chord @(17) 89;WaitFor {@(Rows (Id 'VersionsList')).Count -eq 1} 'Version redo failed';Chord @(17) 90;WaitFor {@(Rows (Id 'VersionsList')).Count -eq 3} 'Version restore failed'
 (Id 'ProjectsList').SetFocus();Chord @(17) 65;Assert ((SelectedCount (Id 'ProjectsList')) -eq 3) 'Ctrl+A selects all version projects'
 Key 46;WaitFor {@(Rows (Id 'ProjectsList')).Count -eq 0} 'Project multi-delete failed';Chord @(17) 90;WaitFor {@(Rows (Id 'ProjectsList')).Count -eq 3} 'Project undo failed'
 Assert ((Raw 'EDIT_VALUE') -eq 'changed' -and (Raw 'EDIT_TARGET_B') -eq 'keep-b') 'Version metadata shortcuts preserve actual environment values'
 Assert ((BackdropBrightness) -gt 160) 'The native backdrop uses the light theme'
 $theme=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Themes\Personalize');$theme.SetValue('AppsUseLightTheme',0);$theme.Dispose();[IntPtr]$result=[IntPtr]::Zero
 $null=[EditingInput]::SendMessageTimeout([IntPtr]$window.Current.NativeWindowHandle,0x1a,[IntPtr]::Zero,'ImmersiveColorSet',2,1000,[ref]$result)
 WaitFor {(BackdropBrightness) -lt 100} 'Native Mica did not follow dark mode';Assert ($true) 'Native Mica follows the system dark theme'
 $theme=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Themes\Personalize');$theme.SetValue('AppsUseLightTheme',1);$theme.Dispose()
 $null=[EditingInput]::SendMessageTimeout([IntPtr]$window.Current.NativeWindowHandle,0x1a,[IntPtr]::Zero,'ImmersiveColorSet',2,1000,[ref]$result)
 WaitFor {(BackdropBrightness) -gt 160} 'Native Mica did not follow light mode';Assert ($true) 'Native Mica follows the system light theme'
 @{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json -Depth 6|Set-Content $ResultPath -Encoding UTF8
}catch{@{passed=$false;checks=$checks.ToArray();error=$_.Exception.Message;trace=$_.ScriptStackTrace}|ConvertTo-Json -Depth 6|Set-Content $ResultPath -Encoding UTF8;throw}
finally{if($app -and !$app.HasExited){$app.Kill();$app.Dispose()}}
