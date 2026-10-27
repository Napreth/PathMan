[CmdletBinding()]
param([string]$ResultPath='C:\PathManFeaturesTest\results.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Use only the disposable VM and restore its initial checkpoint.'}
if($PrepareFixture){
 Get-Process PathMan,OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force
 $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
 try{foreach($pair in @{Path='%REL_A_PATH%;%REL_B_PATH%';REL_A_PATH='C:\same;C:\unique-a';REL_B_PATH='C:\same;C:\unique-b';PATHMAN_EMPTY_PATH='C:\plain'}.GetEnumerator()){$user.SetValue($pair.Key,$pair.Value,[Microsoft.Win32.RegistryValueKind]::ExpandString)}}finally{$user.Dispose()}
 $machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true)
 try{$machine.SetValue('MACHINE_EDIT_A','before-a');$machine.SetValue('MACHINE_EDIT_B','before-b');$machine.SetValue('MACHINE_TYPE','%SystemRoot%\tools',[Microsoft.Win32.RegistryValueKind]::String)}finally{$machine.Dispose()}
 $data=Join-Path $env:LOCALAPPDATA 'PathMan';$shims=Join-Path $data 'shims';New-Item -ItemType Directory -Force $shims | Out-Null
 New-Item -ItemType HardLink -Path (Join-Path $shims 'fixture.exe') -Target C:\PathManTestApp\internal\PathMan.Shim.exe -Force | Out-Null
 [IO.File]::WriteAllText((Join-Path $shims 'fixture.shim'),"[PathMan.Shim]`r`nFormat=1`r`nTarget=C:\Windows\System32\notepad.exe`r`n",[Text.Encoding]::Unicode)
 @{preferences=@{language='zh-CN';theme='system';autoCheckUpdates=$false};groups=@(@{id='paths';name='路径变量';rule='paths'},@{id='system';name='系统变量';rule='builtin';collapsed=$true},@{id='other';name='其他变量';rule='fallback'});revision=[guid]::NewGuid().ToString('N')}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $data 'environment.json') -Encoding UTF8
 return
}
# Reuse the UIA primitives from the health regression without running its fixture or tests.
$common=(Get-Content C:\PathManFeaturesTest\gui-health-actions.ps1 -Raw).Replace("`r`n","`n")
$start=$common.IndexOf('Add-Type -AssemblyName');$end=$common.IndexOf("try{`n StartApp")
if($start -lt 0 -or $end -le $start){throw 'UI automation helper section not found'}
. ([scriptblock]::Create($common.Substring($start,$end-$start)))
function AliasRow([string]$name){Find (Id 'Aliases') $name ([Windows.Automation.ControlType]::ListItem)}
function AliasText([string]$name){Find (AliasRow $name) $name ([Windows.Automation.ControlType]::Text)}
function DoubleClick($element){$r=$element.Current.BoundingRectangle;[void][HealthInput]::SetCursorPos([int]($r.Left+8),[int]($r.Top+$r.Height/2));for($n=0;$n -lt 2;$n++){[HealthInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[HealthInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 90};Start-Sleep -Milliseconds 200}
function Workers{@(Get-Process PathMan -ErrorAction SilentlyContinue|Where-Object Id -ne $window.Current.ProcessId)}
function MachineValue([string]$name){$key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment');try{$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}}
function SetUserPath([string]$raw){$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment',$true);try{$key.SetValue('Path',$raw,[Microsoft.Win32.RegistryValueKind]::ExpandString)}finally{$key.Dispose()}}
$shims=Join-Path $env:LOCALAPPDATA 'PathMan\shims'
function Manifest([string]$name){Join-Path $shims ($name+'.shim')}
function AliasTarget([string]$name){$lines=Get-Content (Manifest $name);if($lines[1] -eq 'Format=2'){[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($lines[2].Substring(13)))}else{$lines[2].Substring(7)}}
function Buttons{ @('NewAliasButton','RenameAliasButton','EditAliasButton','BrowseAliasButton','CopyAliasButton','DeleteAliasButton') }
try{
 StartApp
 Assert ($window.Current.Name -eq 'PathMan · 以非管理员身份运行') 'The title reports the actual unelevated desktop token'
 Go '健康检测';Invoke (Id 'ScanButton');WaitHealth
 $issue=Issue 'Duplicate:User:Path:1';Invoke (Find $issue '查看详情' ([Windows.Automation.ControlType]::Button))
 $related=(Id 'RelatedVariables').FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
 Assert ($related.Count -eq 3) 'Health details include PATH and both referenced source variables in the current page'
 $button=Find (Id 'DetailActions') '删除用户 REL_A_PATH 第 1 行' ([Windows.Automation.ControlType]::Button)
 Assert ($button.Current.BoundingRectangle.Left -gt (Id 'RelatedVariables').Current.BoundingRectangle.Right) 'Health detail actions sit outside the related-variable preview on the right'
 Invoke $button;WaitHealth
 Assert ((UserValue 'REL_A_PATH') -eq 'C:\unique-a' -and ($null -eq (Id 'DetailsPanel') -or (Id 'DetailsPanel').Current.IsOffscreen)) 'A detail repair updates its source and returns to the overview when resolved'
 Go '变量管理'
 foreach($pair in @(@('MACHINE_EDIT_A','after-a'),@('MACHINE_EDIT_B','after-b'))){
  SelectNode (Find (Id 'SystemVariablesTree') $pair[0] ([Windows.Automation.ControlType]::TreeItem))
  (Id 'ScalarValueBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($pair[1])
  if($pair[0] -eq 'MACHINE_EDIT_A'){'WAITING_FOR_FIRST_SESSION_UAC'|Set-Content ($ResultPath+'.progress')}
  Invoke (Id 'SaveButton');WaitFor {(MachineValue $pair[0]) -eq $pair[1] -and !(Id 'SaveButton').Current.IsEnabled} 'Confirm only the first system-write UAC prompt'
  if($pair[0] -eq 'MACHINE_EDIT_A'){$worker=(Workers)[0].Id;Assert ((Workers).Count -eq 1) 'The first authorized write creates exactly one elevated worker'}
  else{Assert ((Workers).Count -eq 1 -and (Workers)[0].Id -eq $worker) 'The second system edit reuses authorization and the same worker'}
 }
 Assert ($window.Current.Name -eq 'PathMan · 管理员操作已授权') 'The title immediately reports reusable administrator authorization'
 Go '健康检测';Invoke (Id 'ScanButton');WaitHealth
 Invoke (Find (Issue 'RegistryType:Machine:MACHINE_TYPE:0') '查看详情' ([Windows.Automation.ControlType]::Button))
 Invoke (Find (Id 'DetailActions') '修正类型' ([Windows.Automation.ControlType]::Button));WaitHealth
 Assert ((ValueKind 'MACHINE_TYPE' $true) -eq 'ExpandString' -and (Workers)[0].Id -eq $worker) 'Health repairs share the authorized system-write session'
 Go '程序别名';WaitFor {$null -ne (AliasRow 'fixture')} 'Alias list did not load'
 SelectNode (AliasRow 'fixture')
 Assert (@(Buttons|Where-Object {(Id $_).Current.IsEnabled}).Count -eq 0) 'All six alias actions are disabled while its directory is absent from user PATH'
 DoubleClick (AliasText 'fixture');Assert ($null -eq (Input) -and (AliasRow 'fixture').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) 'Disabled aliases still allow selection and suppress double-click editing'
 Assert ($null -ne (Find $window '此功能将在用户PATH中添加垫片目录' ([Windows.Automation.ControlType]::Text))) 'The enable switch has the requested subtitle'
 (Id 'EnableAliases').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
 WaitFor {(Id 'NewAliasButton').Current.IsEnabled} 'Enabling aliases did not enable its actions'
 Assert ((UserValue 'Path').EndsWith($shims)) 'Enabling aliases appends the shim directory to user PATH'
 Invoke (Id 'NewAliasButton');WaitFor {$null -ne (Input)} 'New alias did not start inline name input'
 SetInput 'bad?name';Key 13;Assert ($null -ne (Input)) 'Invalid Windows file names keep the inline editor open'
 SetInput 'gui_alias';Key 13;WaitFor {$null -ne (Input) -and (Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq ''} 'A valid new name did not advance to target input'
 SetInput 'C:\Windows\System32\notepad.exe';Key 13
 WaitFor {(AliasTarget 'gui_alias') -eq 'C:\Windows\System32\notepad.exe'} 'New alias did not save its target'
 Assert (Test-Path (Join-Path $shims 'gui_alias.exe')) 'New aliases create an executable and matching manifest'
 SelectNode (AliasRow 'gui_alias');Invoke (Id 'RenameAliasButton');SetInput 'CON';Key 13;Assert ($null -ne (Input)) 'Rename rejects reserved Windows device names'
 SetInput 'fixture';Key 13;Assert ($null -ne (Input)) 'Rename rejects an existing alias without overwriting it'
 SetInput 'gui_renamed';Key 13;WaitFor {Test-Path (Manifest 'gui_renamed')} 'Rename did not finish'
 Assert (!(Test-Path (Manifest 'gui_alias')) -and (Test-Path (Join-Path $shims 'gui_renamed.exe'))) 'Rename moves both files and preserves the selected alias'
 DoubleClick (Find (AliasRow 'gui_renamed') 'C:\Windows\System32\notepad.exe' ([Windows.Automation.ControlType]::Text));WaitFor {$null -ne (Input)} 'Double-click target editing did not begin'
 Assert ((Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'C:\Windows\System32\notepad.exe') 'Double-click edits the alias target inline'
 SetInput 'C:\Windows\System32\cmd.exe';Key 13;WaitFor {(AliasTarget 'gui_renamed') -eq 'C:\Windows\System32\cmd.exe'} 'Edited target did not save'
 $launched=Start-Process (Join-Path $shims 'gui_renamed.exe') -ArgumentList '/c exit 37' -WindowStyle Hidden -Wait -PassThru
 Assert ($launched.ExitCode -eq 37) 'The edited alias launches its target and returns its exit code'
 Invoke (Id 'CopyAliasButton');Assert ((Get-Clipboard -Raw) -eq 'gui_renamed') 'Copy places the selected command name on the clipboard'
 Invoke (Id 'BrowseAliasButton');$dialog=$null;WaitFor {$script:dialog=Find ([Windows.Automation.AutomationElement]::RootElement) '选择目标文件' ([Windows.Automation.ControlType]::Window);$null -ne $dialog} 'The file picker did not open'
 $fileName=Find $dialog 'File name:' ([Windows.Automation.ControlType]::Edit)
 if($null -eq $fileName){$fileName=Find $dialog '文件名:' ([Windows.Automation.ControlType]::Edit)}
 $fileName.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('C:\Windows\System32\where.exe')
 Invoke (Find $dialog 'Open' ([Windows.Automation.ControlType]::Button))
 WaitFor {(AliasTarget 'gui_renamed') -eq 'C:\Windows\System32\where.exe'} 'Browse did not update the alias target'
 Assert ($true) 'Browse uses a file picker and saves the selected target'
 Invoke (Id 'DeleteAliasButton');WaitFor {!(Test-Path (Manifest 'gui_renamed'))} 'Deletion did not finish'
 Assert (!(Test-Path (Join-Path $shims 'gui_renamed.exe')) -and (Test-Path C:\Windows\System32\where.exe)) 'Deletion removes only the alias pair, preserving its target program'
 SetUserPath ($shims+';%REL_A_PATH%;%REL_B_PATH%')
 Go '健康检测';Invoke (Id 'ScanButton');WaitHealth
 Invoke (Find (Issue 'AliasPathOrder:User:Path:1') '查看详情' ([Windows.Automation.ControlType]::Button))
 Assert ($null -ne (Find (Id 'DetailActions') '移到用户 PATH 末尾' ([Windows.Automation.ControlType]::Button))) 'Misplaced shim paths have a warning, details and a move-to-end action'
 Invoke (Find (Id 'DetailActions') '移到用户 PATH 末尾' ([Windows.Automation.ControlType]::Button));WaitHealth
 Assert ((UserValue 'Path') -eq '%REL_A_PATH%;%REL_B_PATH%;'+$shims) 'The shim-order repair preserves other references and moves its directory last'
 Go '程序别名';SelectNode (AliasRow 'fixture');SetUserPath '%REL_A_PATH%;%REL_B_PATH%'
 WaitFor {@(Buttons|Where-Object {(Id $_).Current.IsEnabled}).Count -eq 0} 'External PATH removal did not disable alias actions'
 DoubleClick (AliasText 'fixture');Assert ($null -eq (Input)) 'External disabling also suppresses double-click editing'
 $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
 WaitFor {$null -eq (Get-Process -Id $worker -ErrorAction SilentlyContinue)} 'Elevated worker survived its desktop parent'
 Assert ($true) 'Closing the desktop releases its elevated worker without leaving a privileged background program'
 @{Passed=$true;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
}catch{@{Passed=$false;Error=$_.Exception.Message;Stack=$_.ScriptStackTrace;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8;throw}
