[CmdletBinding()]
param([string]$ResultPath='C:\PathManFeaturesTest\regressions.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Run only in the disposable VM, then restore the initial checkpoint.'}
if($PrepareFixture){
 Get-Process PathMan,OneDrive -ErrorAction SilentlyContinue|Stop-Process -Force
 $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
 try{foreach($pair in @{Path='%NESTED_PATH%;%GOOD_PATH%;%MISSING%';NESTED_PATH='%LEAF_PATH%;C:\nested';LEAF_PATH='C:\one;;C:\two';GOOD_PATH='C:\good';PATHMAN_EMPTY_PATH='C:\plain'}.GetEnumerator()){$user.SetValue($pair.Key,$pair.Value,[Microsoft.Win32.RegistryValueKind]::ExpandString)}}finally{$user.Dispose()}
 $machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true)
 try{$machine.SetValue('MACHINE_EDIT_A','before')}finally{$machine.Dispose()}
 $data=Join-Path $env:LOCALAPPDATA 'PathMan';$shims=Join-Path $data 'shims';New-Item -ItemType Directory -Force $shims|Out-Null
 foreach($name in 'fixture','delete-me','renamed','blank-target','copied-name'){foreach($suffix in '.exe','.shim'){Remove-Item -LiteralPath (Join-Path $shims ($name+$suffix)) -ErrorAction SilentlyContinue}}
 foreach($name in 'fixture','delete-me'){New-Item -ItemType HardLink -Path (Join-Path $shims ($name+'.exe')) -Target C:\PathManTestApp\internal\PathMan.Shim.exe -Force|Out-Null;[IO.File]::WriteAllText((Join-Path $shims ($name+'.shim')),"[PathMan.Shim]`r`nFormat=1`r`nTarget=C:\Windows\System32\notepad.exe`r`n",[Text.Encoding]::Unicode)}
 [IO.File]::WriteAllText('C:\PathManFeaturesTest\browse target.cmd',"@echo off`r`nexit /b 39`r`n")
 [IO.File]::WriteAllText('C:\PathManFeaturesTest\browse target.txt','ordinary file')
 [IO.File]::WriteAllText('C:\PathManFeaturesTest\browse-target','no extension')
 @{preferences=@{language='zh-CN';theme='system';autoCheckUpdates=$false};groups=@(@{id='paths';name='路径变量';rule='paths'},@{id='system';name='系统变量';rule='builtin';collapsed=$true},@{id='other';name='其他变量';rule='fallback'});revision=[guid]::NewGuid().ToString('N')}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $data 'environment.json') -Encoding UTF8
 return
}
$common=(Get-Content C:\PathManFeaturesTest\gui-health-actions.ps1 -Raw).Replace("`r`n","`n")
$start=$common.IndexOf('Add-Type -AssemblyName');$end=$common.IndexOf("try{`n StartApp")
. ([scriptblock]::Create($common.Substring($start,$end-$start)))
$shims=Join-Path $env:LOCALAPPDATA 'PathMan\shims'
function AliasRow([string]$name){Find (Id 'Aliases') $name ([Windows.Automation.ControlType]::ListItem)}
function Go([string]$name){Click (Find (Id 'Navigation') $name ([Windows.Automation.ControlType]::ListItem))}
function Target([string]$name){$lines=Get-Content (Join-Path $shims ($name+'.shim'));if($lines[1] -eq 'Format=2'){[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($lines[2].Substring(13)))}else{$lines[2].Substring(7)}}
function ClickRegion([string]$name,[bool]$detail){$r=(AliasRow $name).Current.BoundingRectangle;[void][HealthInput]::SetCursorPos([int]($r.Left+$(if($detail){230}else{30})),[int]($r.Top+$r.Height/2));[HealthInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[HealthInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 200;WaitFor {$null -ne (Input)} 'Clicking a column did not open its editor'}
function Related{(Id 'RelatedVariables').FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))}
function Question{Find ([Windows.Automation.AutomationElement]::RootElement) 'PathMan' ([Windows.Automation.ControlType]::Window)}
function ClickBlank{$r=(Id 'Aliases').Current.BoundingRectangle;[void][HealthInput]::SetCursorPos([int]($r.Left+300),[int]($r.Bottom-30));[HealthInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[HealthInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 200}
function BrowseTarget {
 param([string]$Selection='C:\Windows\System32\notepad.exe',[switch]$Cancel)
 $manifest=Join-Path $shims 'renamed.shim';$before=(Get-FileHash $manifest).Hash;$written=[IO.File]::GetLastWriteTimeUtc($manifest)
 $draft=(Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
 Click (Id 'BrowseAliasButton');$script:dialog=$null;WaitFor {$script:dialog=Find ([Windows.Automation.AutomationElement]::RootElement) '选择目标文件' ([Windows.Automation.ControlType]::Window);$null -ne $script:dialog} 'Browse was blocked during target editing'
 Start-Sleep -Milliseconds 250
 Assert ((Get-FileHash $manifest).Hash -eq $before -and [IO.File]::GetLastWriteTimeUtc($manifest) -eq $written) 'Opening and using the file picker does not save the unconfirmed editor'
 if($Cancel){
  Invoke (Find $dialog 'Cancel' ([Windows.Automation.ControlType]::Button));Start-Sleep -Milliseconds 200
  Assert ((Get-FileHash $manifest).Hash -eq $before -and [IO.File]::GetLastWriteTimeUtc($manifest) -eq $written -and (Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $draft) 'Cancelling Browse preserves the manifest and the exact pending editor text'
  return
 }
 $fileName=Find $dialog 'File name:' ([Windows.Automation.ControlType]::Edit);if($null -eq $fileName){$fileName=Find $dialog '文件名:' ([Windows.Automation.ControlType]::Edit)}
 $fileName.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Selection);Invoke (Find $dialog 'Open' ([Windows.Automation.ControlType]::Button))
}
try{
 StartApp;Go '健康检测';Invoke (Id 'ScanButton');WaitHealth
 Invoke (Find (Issue 'UndefinedReference:User:Path:0') '查看详情' ([Windows.Automation.ControlType]::Button))
 Assert ((Related).Count -eq 1 -and (Related)[0].Current.Name -eq '用户 · Path') 'A direct PATH error displays PATH alone, excluding healthy referenced variables'
 $bad=Find (Id 'RelatedVariables') '第 3 行（问题）' ([Windows.Automation.ControlType]::ListItem)
 Assert ($null -ne $bad -and $bad.Current.BoundingRectangle.Width -gt (Id 'RelatedVariables').Current.BoundingRectangle.Width*.85) 'The problematic reference is identified across the complete row width'
 Invoke (Find (Id 'DetailsPanel') '返回' ([Windows.Automation.ControlType]::Button))
 Invoke (Find (Issue 'EmptyPathEntry:User:Path:2') '查看详情' ([Windows.Automation.ControlType]::Button))
 Assert (((Related)|ForEach-Object {$_.Current.Name}) -join ',' -eq '用户 · Path,用户 · NESTED_PATH,用户 · LEAF_PATH') 'Nested detail previews contain only PATH and the recursive chain to the failing leaf'
 $problemRows=@((Id 'RelatedVariables').FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))|Where-Object {$_.Current.Name -like '*（问题）'})
 Assert ($problemRows.Count -eq 3) 'Only the two reference rows and the actual empty leaf row are marked problematic'
 Go '程序别名';(Id 'EnableAliases').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle();WaitFor {(Id 'NewAliasButton').Current.IsEnabled} 'Alias controls did not enable'
 ClickRegion 'fixture' $false
 $r=(AliasRow 'fixture').Current.BoundingRectangle;$inputRect=(Input).Current.BoundingRectangle
 Assert ($inputRect.Width -le 182 -and $inputRect.Right -lt $r.Left+200) 'Clicking the left column opens a name editor confined to that column'
 SetInput 'CON';Key 13;Assert ($null -ne (Input)) 'Alias names still reject reserved Windows file names'
 SetInput 'renamed';ClickBlank;WaitFor {$null -ne (AliasRow 'renamed') -and $null -eq (Input)} 'Clicking blank space did not save the name'
 Assert ($true) 'Clicking blank list space saves a name without Enter'
 ClickRegion 'renamed' $true;$inputRect=(Input).Current.BoundingRectangle;$r=(AliasRow 'renamed').Current.BoundingRectangle
 Assert ($inputRect.Left -ge $r.Left+180 -and $inputRect.Right -le $r.Right+1) 'Clicking the right column opens a target editor confined to that column'
 SetInput 'invalid?:<>|target';ClickBlank;WaitFor {(Target 'renamed') -eq 'invalid?:<>|target' -and $null -eq (Input)} 'Clicking blank space did not save the target'
 Assert ($null -ne (AliasRow 'renamed')) 'Invalid target paths do not hide or remove the alias'
 ClickRegion 'renamed' $true;SetInput '';Key 13;WaitFor {(Target 'renamed') -eq ''} 'An empty target could not be stored'
 Assert (Test-Path (Join-Path $shims 'renamed.exe')) 'An empty target preserves the alias executable'
 ClickRegion 'renamed' $true;SetInput 'column-switch-target';ClickRegion 'renamed' $false
 Assert ((Target 'renamed') -eq 'column-switch-target' -and (Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'renamed') 'Changing columns saves the previous field and preserves the newly opened editor'
 Key 27
 ClickRegion 'renamed' $true;SetInput 'tab-saved';Key 9;WaitFor {(Target 'renamed') -eq 'tab-saved'} 'Tab did not save the previous field'
 Assert ($true) 'Keyboard focus leaving the editor saves without Enter'
 ClickRegion 'renamed' $true;SetInput 'pending-inside';Click (Input)
 Assert ((Target 'renamed') -eq 'tab-saved' -and $null -ne (Input)) 'Clicking inside the input keeps editing without prematurely saving'
 ClickBlank;WaitFor {(Target 'renamed') -eq 'pending-inside'} 'Leaving the input did not save'
 ClickRegion 'renamed' $true;SetInput 'unsaved invalid target';BrowseTarget -Cancel
 (Id 'BrowseAliasButton').SetFocus();Start-Sleep -Milliseconds 200
 Assert ((Target 'renamed') -eq 'pending-inside') 'Keyboard focus on Browse also defers saving until a file is selected'
 BrowseTarget -Selection 'C:\PathManFeaturesTest\browse target.cmd';WaitFor {(Target 'renamed') -eq 'C:\PathManFeaturesTest\browse target.cmd'} 'The picker rejected a CMD file'
 Assert ($true) 'Selecting a CMD file saves the chosen path without an EXE filter'
 ClickRegion 'renamed' $true;SetInput 'second unconfirmed target';BrowseTarget -Selection 'C:\PathManFeaturesTest\browse target.txt';WaitFor {(Target 'renamed') -eq 'C:\PathManFeaturesTest\browse target.txt'} 'The picker rejected an ordinary file'
 Assert ($true) 'Ordinary text files can be selected as alias targets'
 ClickRegion 'renamed' $true;SetInput 'third unconfirmed target';BrowseTarget -Selection 'C:\PathManFeaturesTest\browse-target';WaitFor {(Target 'renamed') -eq 'C:\PathManFeaturesTest\browse-target'} 'The picker added or required an extension'
 Assert ($true) 'Selecting a file without an extension does not add EXE'
 Assert ($true) 'Browse remains clickable during an uncommitted target edit'
 ClickRegion 'delete-me' $true;SetInput 'uncommitted target'
 $watcher=[IO.FileSystemWatcher]::new($shims,'delete-me.shim*');$watchId='PathManDelete-'+[guid]::NewGuid().ToString('N')
 try{
  Register-ObjectEvent $watcher Created -SourceIdentifier $watchId|Out-Null;$watcher.EnableRaisingEvents=$true
  (Id 'DeleteAliasButton').SetFocus();Start-Sleep -Milliseconds 150
  Assert ((Target 'delete-me') -eq 'C:\Windows\System32\notepad.exe') 'Focusing Delete does not commit the pending target'
  Click (Id 'DeleteAliasButton');WaitFor {!(Test-Path (Join-Path $shims 'delete-me.exe'))} 'Deletion was blocked by its target editor';Start-Sleep -Milliseconds 250
  Assert (@(Get-Event -SourceIdentifier $watchId -ErrorAction SilentlyContinue).Count -eq 0) 'Delete removes the alias without writing a temporary manifest first'
 }finally{$watcher.Dispose();Unregister-Event -SourceIdentifier $watchId -ErrorAction SilentlyContinue;Get-Event -SourceIdentifier $watchId -ErrorAction SilentlyContinue|Remove-Event}
 Assert (!(Test-Path (Join-Path $shims 'delete-me.shim'))) 'Delete remains usable during editing and removes the alias pair'
 ClickRegion 'renamed' $true;SetInput 'page-target';Go '健康检测';WaitFor {$null -ne (Id 'Summary') -and (Target 'renamed') -eq 'page-target'} 'Navigation did not save the alias automatically'
 Assert ($null -eq (Question)) 'Switching pages saves directly without an unsaved-edit prompt'
 Go '程序别名';WaitFor {$null -ne (AliasRow 'renamed')} 'Aliases did not reload';ClickRegion 'renamed' $false;SetInput 'CON';Go '健康检测'
 Assert ($null -ne (Id 'Aliases') -and $null -ne (Input) -and $null -eq (Question)) 'An invalid alias name stays editable without opening a modal prompt'
 Key 27;Go '健康检测';WaitFor {$null -ne (Id 'Summary')} 'Cancelling the invalid name did not allow navigation'
 Assert (Test-Path (Join-Path $shims 'renamed.exe')) 'Esc preserves the saved alias name'
 Go '程序别名';WaitFor {$null -ne (AliasRow 'renamed')} 'Aliases did not reload';ClickRegion 'renamed' $false;SetInput 'copied-name';Click (Id 'CopyAliasButton');WaitFor {Test-Path (Join-Path $shims 'copied-name.exe')} 'Copy did not save the active rename'
 Assert ((Get-Clipboard -Raw) -eq 'copied-name') 'Copy acts on the saved name from the same click'
 ClickRegion 'copied-name' $true;SetInput 'refresh-saved';Click (Id 'RefreshAliasesButton');WaitFor {(Target 'copied-name') -eq 'refresh-saved' -and $null -eq (Input)} 'Refresh did not save its active editor'
 Assert ($null -eq (Question)) 'Refresh saves before reloading without a prompt'
 Go '程序别名';WaitFor {$null -ne (AliasRow 'copied-name')} 'Aliases did not reload';Invoke (Id 'NewAliasButton');SetInput 'blank-target';Key 13;WaitFor {Test-Path (Join-Path $shims 'blank-target.exe')} 'New alias required a valid target';Go '健康检测'
 Assert ((Target 'blank-target') -eq '') 'Creating a valid name is sufficient to retain an alias with no target'
 Go '变量管理';SelectNode (Find (Id 'SystemVariablesTree') 'MACHINE_EDIT_A' ([Windows.Automation.ControlType]::TreeItem));(Id 'ScalarValueBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('authorized')
 'WAITING_FOR_UAC'|Set-Content ($ResultPath+'.progress');Invoke (Id 'SaveButton');WaitFor {$window.Current.Name -eq 'PathMan · 管理员操作已授权' -and !(Id 'SaveButton').Current.IsEnabled} 'Confirm the first UAC prompt in the VM'
 Assert ($null -ne (Find $window 'PathMan · 管理员操作已授权' ([Windows.Automation.ControlType]::Text))) 'Both native and custom titles update as soon as administrator operations are authorized'
 Go '程序别名';WaitFor {$null -ne (AliasRow 'copied-name')} 'Aliases did not reload';ClickRegion 'copied-name' $true;SetInput 'close-saved'
 $desktopId=$window.Current.ProcessId;$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
 WaitFor {$null -eq (Get-Process -Id $desktopId -ErrorAction SilentlyContinue)} 'Closing did not save and exit'
 Assert ((Target 'copied-name') -eq 'close-saved' -and $null -eq (Question)) 'Closing saves the current alias without a prompt'
 @{Passed=$true;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
}catch{@{Passed=$false;Error=$_.Exception.Message;Stack=$_.ScriptStackTrace;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8;throw}
