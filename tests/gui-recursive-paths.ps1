[CmdletBinding()]
param([string]$ResultPath='C:\PathManRecursiveTest\results.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Use only the disposable VM and restore its initial checkpoint afterward.'}
if($PrepareFixture){
 Get-Process PathMan -ErrorAction SilentlyContinue | Stop-Process -Force
 $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
 try{
  foreach($pair in @{
   Path='C:\root;%A_PATH%;C:\tail;%OTHER_PATH%';A_PATH='%B_PATH%;C:\a1;C:\a2';B_PATH='C:\b1;C:\b2';OTHER_PATH='C:\other';
   SCALAR_REFERENCE='%USERPROFILE%\tools';PLAIN_VALUE='one';PATHMAN_LIST='one;;two;  spaced  ;';PATHMAN_CYCLE_A='%PATHMAN_CYCLE_B%';PATHMAN_CYCLE_B='%PATHMAN_CYCLE_A%';PATHMAN_UNDEFINED='%PATHMAN_NO_SUCH_VARIABLE%'
  }.GetEnumerator()){$user.SetValue($pair.Key,$pair.Value,[Microsoft.Win32.RegistryValueKind]::ExpandString)}
 }finally{$user.Dispose()}
 $machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true)
 try{
  $machine.SetValue('Path','%SystemRoot%\System32;%SystemRoot%;%SystemRoot%\System32\Wbem;%SystemRoot%\System32\WindowsPowerShell\v1.0\;%SystemRoot%\System32\OpenSSH;C:\tool;C:\TOOL\;%MACHINE_PATH%',[Microsoft.Win32.RegistryValueKind]::ExpandString)
  $base=[string]$machine.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
  $machine.SetValue('Path',$base+';'+((0..31 | ForEach-Object {"C:\Software\tool$_"}) -join ';'),[Microsoft.Win32.RegistryValueKind]::ExpandString)
  $machine.SetValue('MACHINE_PATH','C:\machine1;C:\machine2')
  $machine.DeleteValue('SOFTWARE_PATH',$false)
 }finally{$machine.Dispose()}
 $data=Join-Path $env:LOCALAPPDATA 'PathMan';New-Item -ItemType Directory -Force $data | Out-Null
 @{groups=@(@{id='paths';name='路径变量';rule='paths'},@{id='system';name='系统变量';rule='builtin';collapsed=$true},@{id='other';name='其他变量';rule='fallback'},@{id='work';name='工作';scope='User';rule='manual'});assignments=@{'User:SCALAR_REFERENCE'='work';'User:PLAIN_VALUE'='work';'User:PATHMAN_LIST'='work'};revision=[guid]::NewGuid().ToString('N')} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $data 'environment.json') -Encoding UTF8
 return
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class RecursiveInput {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
 public static void Wheel(int delta){mouse_event(0x800,0,0,unchecked((uint)delta),UIntPtr.Zero);}
}
'@
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message);$message | Set-Content ($ResultPath+'.progress')}
function WaitFor([scriptblock]$condition,[string]$message){for($i=0;$i -lt 1200;$i++){if(& $condition){return};Start-Sleep -Milliseconds 100};throw $message}
function Find($root,[string]$name,$type=$null){
 $c=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
 if($type){$c=[Windows.Automation.AndCondition]::new($c,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$type))}
 $root.FindFirst([Windows.Automation.TreeScope]::Descendants,$c)
}
function Id([string]$name){$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$name))}
function Node([string]$name){Find (Id 'UserVariablesTree') $name ([Windows.Automation.ControlType]::TreeItem)}
function SelectNode($node){$node.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 250}
function Invoke($element){$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 250}
function Rows{(Id 'ValuesList').FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))}
function Row([string]$name){Find (Id 'ValuesList') $name ([Windows.Automation.ControlType]::ListItem)}
function RowText([string]$name){Find (Row $name) $name ([Windows.Automation.ControlType]::Text)}
function ExpandRef([string]$name){Invoke (Find (Row $name) "展开 $name" ([Windows.Automation.ControlType]::Button))}
function UserValue([string]$name){$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment');try{$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}}
function MachineValue([string]$name){$key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment');try{$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}}
function MovePointer($x,$y){[void][RecursiveInput]::SetCursorPos([int]$x,[int]$y);Start-Sleep -Milliseconds 80}
function Down{[RecursiveInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)}
function Up{[RecursiveInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)}
function Key([byte]$code){[RecursiveInput]::keybd_event($code,0,0,[UIntPtr]::Zero);[RecursiveInput]::keybd_event($code,0,2,[UIntPtr]::Zero);Start-Sleep -Milliseconds 180}
function DragRow([string]$source,$x,$y){Start-Sleep -Milliseconds 550;$r=(RowText $source).Current.BoundingRectangle;MovePointer ($r.Left+10) ($r.Top+$r.Height/2);Down;MovePointer ($r.Left+25) ($r.Top+$r.Height/2+4);MovePointer $x $y;Start-Sleep -Milliseconds 150;Up;Start-Sleep -Milliseconds 300}
function Save{Invoke (Id 'SaveButton');WaitFor {!(Id 'SaveButton').Current.IsEnabled} 'Save did not complete'}
function Go([string]$page){SelectNode (Find (Id 'Navigation') $page ([Windows.Automation.ControlType]::ListItem))}
try{
 Start-Process 'C:\PathManTestApp\PathMan.exe'
 $window=$null
 WaitFor {$script:window=Find ([Windows.Automation.AutomationElement]::RootElement) 'PathMan · 以非管理员身份运行' ([Windows.Automation.ControlType]::Window);$null -ne $window} 'Window did not launch'
 [void][RecursiveInput]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle)
 WaitFor {$null -ne (Node 'Path')} 'Variables did not load'
 WaitFor {$null -ne (Find $window '环境变量存在问题' ([Windows.Automation.ControlType]::Text))} 'Startup health notification missing'
 Assert ($null -ne (Find (Id 'Navigation') '健康检测' ([Windows.Automation.ControlType]::ListItem)) -and $null -ne (Find (Id 'Navigation') '版本管理' ([Windows.Automation.ControlType]::ListItem))) 'Both requested navigation entries exist'
 $nav=(Id 'Navigation').FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
 Assert (($nav | ForEach-Object {$_.Current.Name}) -join ',' -eq '变量管理,健康检测,程序别名,版本管理,变量监控,关于') 'The two new navigation entries have the requested order'
 Invoke (Find $window '查看健康检测' ([Windows.Automation.ControlType]::Hyperlink))
 Assert ($null -ne (Id 'Summary') -and (Id 'Summary').Current.Name -like '发现*') 'OSC8 notification links navigate to the startup health report'
 Assert ($null -ne (Find $window '引用回环' ([Windows.Automation.ControlType]::Text)) -and $null -ne (Find $window '未定义变量' ([Windows.Automation.ControlType]::Text)) -and $null -ne (Find $window '重复条目' ([Windows.Automation.ControlType]::Text))) 'Health overview groups cycles, undefined variables and duplicates'
 Assert ($null -eq (Find $window '一键解决')) 'Health overview does not expose unimplemented automatic fixes'
 Invoke (Find $window '用户 · PATHMAN_UNDEFINED' ([Windows.Automation.ControlType]::Hyperlink))
 Assert ((Id 'VariableTitle').Current.Name -eq 'PATHMAN_UNDEFINED') 'Health report links route to the exact variable'
 Go '版本管理';Assert ($null -ne (Find $window '开发中' ([Windows.Automation.ControlType]::Text))) 'Version management is a placeholder only'
 Go '变量管理';SelectNode (Node 'SCALAR_REFERENCE');Start-Sleep -Milliseconds 300
 $r=(Id 'ScalarValueBox').Current.BoundingRectangle
 $bitmap=[Drawing.Bitmap]::new([int][Math]::Min(280,$r.Width),28);$graphics=[Drawing.Graphics]::FromImage($bitmap)
 try{
  $graphics.CopyFromScreen([int]$r.Left,[int]$r.Top,0,0,$bitmap.Size);$yellow=0
  for($y=0;$y -lt $bitmap.Height;$y++){for($x=0;$x -lt $bitmap.Width;$x++){$color=$bitmap.GetPixel($x,$y);if($color.R -eq 255 -and $color.G -eq 247 -and $color.B -eq 221){$yellow++}}}
  Assert ($yellow -gt 10 -and $yellow -lt $bitmap.Width*$bitmap.Height*.8) 'Scalar references have a yellow background limited to the referenced token'
 }finally{$graphics.Dispose();$bitmap.Dispose()}
 SelectNode (Node 'Path')
 Assert ((Rows).Count -eq 4) 'Pure reference groups start collapsed'
 $group=Find (Node '路径变量') '路径变量' ([Windows.Automation.ControlType]::Text)
 $variable=Find (Node 'Path') 'Path' ([Windows.Automation.ControlType]::Text)
 Assert (($variable.Current.BoundingRectangle.Left-$group.Current.BoundingRectangle.Left) -ge 14) 'Variables are indented to the right of category headings'
 ExpandRef '%A_PATH%';ExpandRef '%B_PATH%'
 Assert ((Rows).Count -eq 9 -and $null -ne (Row 'C:\b1')) 'Pure references recursively expand into their actual child entries'
 $rootLeft=(RowText 'C:\root').Current.BoundingRectangle.Left;$nestedLeft=(RowText 'C:\b1').Current.BoundingRectangle.Left
 Assert (($nestedLeft-$rootLeft) -ge 30) 'Nested references have a visible recursive indentation'
 $target=(Row '%A_PATH%').Current.BoundingRectangle
 DragRow 'C:\tail' ($target.Left+75) ($target.Top+2)
 Assert ((Rows)[0].Current.Name -eq 'C:\root' -and (Rows)[1].Current.Name -eq 'C:\tail') 'Literals and path-reference groups can reorder among one another'
 Assert ((UserValue 'Path') -eq 'C:\root;%A_PATH%;C:\tail;%OTHER_PATH%') 'Dragging remains staged until Save'
 $target=(Row '%B_PATH%').Current.BoundingRectangle
 DragRow 'C:\root' ($target.Left+90) ($target.Top+$target.Height/2)
 Save
 Assert ((UserValue 'Path') -eq 'C:\tail;%A_PATH%;%OTHER_PATH%' -and (UserValue 'B_PATH') -eq 'C:\b1;C:\b2;C:\root') 'Moving a nested entry saves both source and target variables together'
 Assert ($null -ne (Row 'C:\b1')) 'Expanded reference groups stay open after saving'
 $target=(Row '%A_PATH%').Current.BoundingRectangle
 DragRow '%OTHER_PATH%' ($target.Left+90) ($target.Top+$target.Height/2)
 Save
 Assert ((UserValue 'Path') -eq 'C:\tail;%A_PATH%' -and (UserValue 'A_PATH') -eq '%B_PATH%;C:\a1;C:\a2;%OTHER_PATH%' -and (UserValue 'OTHER_PATH') -eq 'C:\other') 'A whole reference group moves into another group without copying its definition'
 $before=UserValue 'Path';$target=(Row '%A_PATH%').Current.BoundingRectangle
 DragRow '%A_PATH%' ($target.Left+80) ($target.Top+12)
 Assert ((UserValue 'Path') -eq $before -and !(Id 'SaveButton').Current.IsEnabled) 'Dragging a reference group into itself is rejected without a staged mutation'
 DragRow 'C:\tail' 250 100
 Assert (!(Id 'SaveButton').Current.IsEnabled) 'Dropping outside the right list cancels without modifying data'
 # A user PATH can expose an inherited machine variable; edits must request UAC.
 $rootBefore=UserValue 'Path'
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment',$true)
 try{$key.SetValue('Path',$rootBefore+';%MACHINE_PATH%',[Microsoft.Win32.RegistryValueKind]::ExpandString)}finally{$key.Dispose()}
 Invoke (Id 'RefreshButton');ExpandRef '%MACHINE_PATH%';SelectNode (Row 'C:\machine1');Invoke (Id 'EditEntryButton')
 $inputBox=Find $window '行内编辑' ([Windows.Automation.ControlType]::Edit)
 $inputBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('C:\machine-edited');$inputBox.SetFocus();Key 13
 'WAITING_FOR_NESTED_SYSTEM_UAC' | Set-Content ($ResultPath+'.progress');Invoke (Id 'SaveButton')
 WaitFor {(MachineValue 'MACHINE_PATH') -eq 'C:\machine-edited;C:\machine2' -and !(Id 'SaveButton').Current.IsEnabled} 'Confirm the nested system-variable UAC in the VM'
 Assert ((UserValue 'Path') -eq $rootBefore+';%MACHINE_PATH%') 'Editing an inherited child saves its machine variable without flattening the user reference'
 # Exercise the right list's distance-based auto-scroll, then cancel the drag.
 $before=UserValue 'Path';$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment',$true)
 try{$key.SetValue('Path',$before+';'+((0..99 | ForEach-Object {"C:\overflow\$_"}) -join ';'),[Microsoft.Win32.RegistryValueKind]::ExpandString)}finally{$key.Dispose()}
 Invoke (Id 'RefreshButton');$scroll=(Id 'ValuesList').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern);$scroll.SetScrollPercent(-1,0);Start-Sleep -Milliseconds 200
 $bounds=(Id 'ValuesList').Current.BoundingRectangle;$r=(RowText 'C:\tail').Current.BoundingRectangle
 Start-Sleep -Milliseconds 550;MovePointer ($r.Left+10) ($r.Top+7);Down;MovePointer ($r.Left+25) ($r.Top+11)
 MovePointer ($bounds.Left+100) ($bounds.Bottom+8);Start-Sleep -Milliseconds 300;$s1=$scroll.Current.VerticalScrollPercent
 Start-Sleep -Milliseconds 450;$s2=$scroll.Current.VerticalScrollPercent
 MovePointer ($bounds.Left+100) ($bounds.Bottom+150);Start-Sleep -Milliseconds 450;$s3=$scroll.Current.VerticalScrollPercent
 Assert ($s2 -gt $s1 -and ($s3-$s2) -gt ($s2-$s1)) 'Right-list auto-scroll accelerates with vertical distance from the edge'
 Key 27;Up;Assert (!(Id 'SaveButton').Current.IsEnabled) 'Escape cancels an auto-scrolling path drag without staging changes'
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment',$true);try{$key.SetValue('Path',$before,[Microsoft.Win32.RegistryValueKind]::ExpandString)}finally{$key.Dispose()};Invoke (Id 'RefreshButton')
 Go '健康检测';Invoke (Id 'ScanButton');Go '变量管理';SelectNode (Node 'Path')
 Invoke (Find $window '关闭通知' ([Windows.Automation.ControlType]::Button))
 Invoke (Id 'RefreshButton');Start-Sleep -Milliseconds 500
 Assert ($null -eq (Find $window '环境变量存在问题' ([Windows.Automation.ControlType]::Text))) 'Dismissed health notifications do not return for unchanged findings'
 # Create a long machine PATH for the diff and wheel regression. User mode cannot
 # change it here; the prepared fixture already contains duplicate software paths.
 Invoke (Id 'CleanupButton')
 Assert ($null -eq (Id 'CleanupWarningsSurface')) 'Cleanup no longer runs or displays its own health checks'
 Assert ($null -ne (Find $window '+' ([Windows.Automation.ControlType]::Text)) -and $null -ne (Find $window '−' ([Windows.Automation.ControlType]::Text))) 'Cleanup previews use unified added and removed lines'
 $all=(Id 'CleanupChanges').FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text))
 Assert (@($all | Where-Object {$_.Current.Name -like '@@ -*'}).Count -gt 0) 'Unified previews include hunk headers and line numbers'
 $scroll=(Id 'CleanupScroll').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern);$scroll.SetScrollPercent(-1,0);Start-Sleep -Milliseconds 200
 $r=(Find $window 'C:\tool' ([Windows.Automation.ControlType]::Text)).Current.BoundingRectangle
 MovePointer ($r.Left+30) ($r.Top+6);$initial=$scroll.Current.VerticalScrollPercent;[RecursiveInput]::Wheel(-120);Start-Sleep -Milliseconds 300
 Assert ($scroll.Current.VerticalScrollPercent -gt $initial) 'Mouse wheel scrolls the outer diff while hovering a nested diff row'
 $scroll.SetScrollPercent(-1,100)
 Assert ((UserValue 'Path') -eq $before) 'Opening the diff does not modify PATH'
 'WAITING_FOR_CLEANUP_UAC' | Set-Content ($ResultPath+'.progress')
 Invoke (Id 'ApplyCleanupButton')
 WaitFor {(Id 'CleanupStatus').Current.Name -like '清理完成*'} 'Confirm the cleanup UAC in the VM'
 WaitFor {$null -ne (Find $window '环境变量存在问题' ([Windows.Automation.ControlType]::Text))} 'Health notification did not update after cleanup'
 Assert ($true) 'Outstanding health issues remain in the sidebar notification after cleanup'
 @{Passed=$true;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
}catch{Up;@{Passed=$false;Error=$_.Exception.Message;Stack=$_.ScriptStackTrace;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8;throw}
