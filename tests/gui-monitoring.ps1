[CmdletBinding()]
param([string]$ResultPath='C:\PathManMonitorTest\results.json',[switch]$PrepareFixture)
# The host driver adds F:\IgnoredBeforeEnabling at ready-machine-disabled,
# then appends F:\ExternalTool plus a duplicate at ready-machine,
# approves UAC, then appends G:\AnotherExternalTool at ready-machine-second.
# Stage/result files let that driver coordinate without privileged test code in the GUI session.
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Only run in the disposable Windows VM; restore the initial checkpoint afterwards.'}
$data=Join-Path $env:LOCALAPPDATA 'PathMan'
if($PrepareFixture){
 Get-Process PathMan,PathMan.Service -ErrorAction SilentlyContinue|Stop-Process -Force
 $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
 try{$user.SetValue('Path','C:\UserBase;C:\Keep;;C:\UserBase',[Microsoft.Win32.RegistryValueKind]::String);foreach($name in @('EXTERNEL_PATH','EXTERNEL_USER_PATH','PATHMAN_EXTERNAL','PATHMAN_KEEP','PATHMAN_CLOSED','PATHMAN_STOPPED')){$user.DeleteValue($name,$false)};$user.SetValue('PATHMAN_KEEP','before',[Microsoft.Win32.RegistryValueKind]::String)}finally{$user.Dispose()}
 $machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true)
 try{$machine.SetValue('Path','C:\MachineTool;C:\Windows\System32;D:\MachineTool',[Microsoft.Win32.RegistryValueKind]::String);$machine.DeleteValue('EXTERNEL_PATH',$false)}finally{$machine.Dispose()}
 New-Item -ItemType Directory -Path $data -Force|Out-Null
 @{groups=@(@{id='paths';name='路径变量';rule='paths'},@{id='system';name='系统变量';rule='builtin'},@{id='other';name='其他变量';rule='fallback'},@{id='external';name='外部变量';rule='external';requiresMonitoring=$true});revision=[guid]::NewGuid().ToString('N')}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $data 'environment.json') -Encoding UTF8
 return
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class MonitorInput{[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);}'
$checks=[Collections.Generic.List[string]]::new()
function State{Get-Content (Join-Path $data 'environment.json') -Raw|ConvertFrom-Json}
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message);[IO.File]::WriteAllText($ResultPath+'.progress',$message)}
function WaitFor([scriptblock]$condition,[string]$message){for($i=0;$i -lt 800;$i++){if(& $condition){return};Start-Sleep -Milliseconds 150};throw $message}
function Find($root,[string]$name,$type=$null){$condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name);if($type){$condition=[Windows.Automation.AndCondition]::new($condition,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$type))};$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)}
function Id([string]$name){$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$name))}
function Invoke($element){$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 200}
function Click($element){[void][MonitorInput]::SetForegroundWindow($app.MainWindowHandle);$r=$element.Current.BoundingRectangle;[void][MonitorInput]::SetCursorPos([int]($r.Left+$r.Width/2),[int]($r.Top+$r.Height/2));[MonitorInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[MonitorInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)}
function MonitorBottom{$scroll=(Id 'MonitorScroll').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern);if($scroll.Current.VerticallyScrollable){$scroll.SetScrollPercent(-1,100)};Start-Sleep -Milliseconds 200}
function Go([string]$name){(Find (Id 'Navigation') $name ([Windows.Automation.ControlType]::ListItem)).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 250}
function Toggle([string]$name){(Id $name).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle();Start-Sleep -Milliseconds 300}
function Checked([string]$name){(Id $name).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On}
function Raw([string]$name,[bool]$machine=$false){$key=if($machine){[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment')}else{[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')};try{$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}}
function SetUser([string]$name,[string]$value){$key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true);try{$key.SetValue($name,$value,[Microsoft.Win32.RegistryValueKind]::String)}finally{$key.Dispose()}}
function StartApp{
 $script:app=Start-Process 'C:\PathManTestApp\PathMan.exe' -PassThru
 WaitFor {$app.Refresh();$app.MainWindowHandle -ne 0} 'GUI did not open'
 $script:window=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle);[void][MonitorInput]::SetForegroundWindow($app.MainWindowHandle)
 WaitFor {$null -ne (Id 'CleanupButton')} 'Variables did not load'
}
function Stage([string]$name){[IO.File]::WriteAllText($ResultPath+'.stage',$name)}
try{
 StartApp
 Invoke (Id 'CleanupButton');WaitFor {$null -ne (Id 'CleanupOrderWarningText')} 'Cleanup preview missing'
 Assert (!(Id 'CleanupOrderWarningText').Current.IsOffscreen -and (Id 'CleanupOrderWarningText').Current.Name.Contains('顺序')) 'Manual cleanup warns when expanded search order changes'
 Go '变量监控'
 Assert (!(Checked 'EnableMonitoring') -and !(Checked 'AutoArrangeUser') -and !(Checked 'AutoArrangeMachine') -and !(Id 'RemoveUserDuplicates').Current.IsEnabled) 'Monitoring and both PATH switches initially disabled; child options follow the scope switches'
 Assert ((Id 'StartupEnabled').Current.Name -eq '登录时自动运行后台程序') 'Startup uses the requested label'
 Toggle 'StartupEnabled';Assert (Checked 'StartupEnabled') 'Startup can be enabled independently';Toggle 'StartupEnabled'
 Toggle 'EnableMonitoring';WaitFor {(State).monitoringEnabled -and (Get-Process PathMan.Service -ErrorAction SilentlyContinue)} 'Background failed to start'
 Assert (Checked 'EnableMonitoring') 'Run switch reflects a real background process'
 SetUser 'PATHMAN_EXTERNAL' 'created';SetUser 'PATHMAN_KEEP' 'edited'
 WaitFor {@((State).changes|Where-Object {$_.name -eq 'PATHMAN_EXTERNAL' -and $_.source -eq '外部程序'}).Count -eq 1} 'External writes not captured'
 Assert ((State).externalVariables -contains 'User:PATHMAN_EXTERNAL' -and (State).externalVariables -notcontains 'User:PATHMAN_KEEP') 'Only newly created variables are assigned external provenance'
 Toggle 'GroupExternalUser';Assert (!(State).groupExternalUserVariables -and (State).groupExternalMachineVariables -and (State).monitoringEnabled) 'User grouping can be disabled without changing system classification or stopping observation';Toggle 'GroupExternalUser'
 Toggle 'GroupExternalMachine';Assert ((State).groupExternalUserVariables -and !(State).groupExternalMachineVariables) 'The system classification switch is independent from the user card';Toggle 'GroupExternalMachine'
 MonitorBottom
 Invoke (Find $window '外部更改记录' ([Windows.Automation.ControlType]::Button))
 Assert ($null -ne (Id 'Changes') -and !(Id 'Changes').Current.IsOffscreen -and ($null -eq (Id 'EnableMonitoring') -or (Id 'EnableMonitoring').Current.IsOffscreen)) 'Records card navigates to an in-page table'
 Assert ($null -ne (Find (Id 'Changes') 'PATHMAN_EXTERNAL') -and $null -ne (Find (Id 'Changes') 'PATHMAN_KEEP')) 'External record table lists captured changes'
 Invoke (Find $window '返回' ([Windows.Automation.ControlType]::Button))
 Toggle 'AutoArrangeUser'
 Assert ((Checked 'AutoArrangeUser') -and !(Checked 'AutoArrangeMachine') -and (Id 'RemoveUserDuplicates').Current.IsEnabled -and !(Id 'RemoveMachineDuplicates').Current.IsEnabled) 'Each card enables only its own PATH suboptions'
 SetUser 'Path' 'C:\UserBase;C:\Keep;;C:\UserBase;D:\NewTool;c:\USERBASE\;D:\NewTool'
 WaitFor {(Raw 'Path') -eq 'C:\UserBase;C:\Keep;;C:\UserBase;%EXTERNEL_USER_PATH%'} 'User PATH was not automatically arranged'
 Assert ((Raw 'EXTERNEL_USER_PATH') -eq 'D:\NewTool') 'New duplicate entries are removed and unique additions move into EXTERNEL_USER_PATH'
 Assert ((Get-ChildItem (Join-Path $data 'backups') -Filter 'path-monitor-*.json').Count -ge 1) 'Automatic registry writes have original-value backups'
 Start-Sleep -Seconds 2
 Assert (@((State).changes|Where-Object {$_.name -eq 'Path' -and $_.source -eq '外部程序'}).Count -eq 1) 'Automatic own writes do not feed back into external history'
 Toggle 'MoveUserEntries'
 SetUser 'Path' ((Raw 'Path')+';E:\LeftInPath;c:\keep\')
 WaitFor {(Raw 'Path') -eq 'C:\UserBase;C:\Keep;;C:\UserBase;%EXTERNEL_USER_PATH%;E:\LeftInPath'} 'Duplicate-only suboption failed'
 Assert ((Raw 'EXTERNEL_USER_PATH') -eq 'D:\NewTool') 'Disabling migration keeps new unique paths in PATH'
 Toggle 'MoveUserEntries';Toggle 'RemoveUserDuplicates'
 Assert (!(State).removeUserPathDuplicates -and (State).removeMachinePathDuplicates -and (State).moveExternalMachinePathEntries) 'Changing user cleanup suboptions preserves the system card settings'
 SetUser 'Path' ((Raw 'Path')+';c:\KEEP\')
 WaitFor {(Raw 'EXTERNEL_USER_PATH') -eq 'D:\NewTool;c:\KEEP\'} 'Migration-only suboption failed'
 Assert ((Raw 'EXTERNEL_USER_PATH') -eq 'D:\NewTool;c:\KEEP\') 'Disabling deduplication preserves new duplicate entries while migrating'
 Toggle 'RemoveUserDuplicates'
 Stage 'ready-machine-disabled'
 WaitFor {@((State).changes|Where-Object {$_.scope -eq 'Machine' -and $_.name -eq 'Path' -and $_.after -like '*F:\IgnoredBeforeEnabling'}).Count -eq 1} 'Disabled system scope did not record its external change'
 Start-Sleep -Seconds 2
 Assert ((Raw 'Path' $true) -eq 'C:\MachineTool;C:\Windows\System32;D:\MachineTool;F:\IgnoredBeforeEnabling' -and @((State).pendingPathOrganizations).Count -eq 0) 'A disabled system PATH switch records changes without cleaning or requesting authorization'
 Toggle 'AutoArrangeMachine';Toggle 'AutoArrangeUser'
 Assert (!(Checked 'AutoArrangeUser') -and (Checked 'AutoArrangeMachine') -and !(Id 'RemoveUserDuplicates').Current.IsEnabled -and (Id 'RemoveMachineDuplicates').Current.IsEnabled) 'System PATH and its suboptions remain enabled independently after user cleanup is disabled'
 Toggle 'MoveMachineEntries';Assert (!(State).moveExternalMachinePathEntries -and (State).moveExternalUserPathEntries) 'Changing system migration does not change the user migration option';Toggle 'MoveMachineEntries'
 SetUser 'Path' ((Raw 'Path')+';H:\UserLeftAlone')
 WaitFor {@((State).snapshot|Where-Object {$_.scope -eq 'User' -and $_.name -eq 'Path' -and $_.value -like '*H:\UserLeftAlone'}).Count -eq 1} 'Disabled user scope was not observed'
 Start-Sleep -Seconds 2
 Assert ((Raw 'Path').EndsWith(';H:\UserLeftAlone') -and (Raw 'EXTERNEL_USER_PATH') -eq 'D:\NewTool;c:\KEEP\') 'A disabled user PATH switch leaves new user entries untouched while system cleanup is enabled'
 Stage 'ready-machine'
 WaitFor {@((State).pendingPathOrganizations|Where-Object {$_.scope -eq 'Machine'}).Count -eq 1} 'System additions were not queued'
 WaitFor {$null -ne (Id 'ApplyPending') -and $null -ne (Id 'MonitorStatus')} 'System authorization action missing'
 MonitorBottom
 WaitFor {!(Id 'ApplyPending').Current.IsOffscreen} 'System authorization action is outside the scrollable viewport'
 Assert ((Id 'MonitorStatus').Current.Name.Contains('管理员')) 'System changes wait visibly for administrator authorization'
 Click (Id 'ApplyPending');Stage 'awaiting-uac'
 WaitFor {@((State).pendingPathOrganizations|Where-Object {$_.scope -eq 'Machine'}).Count -eq 0} 'Authorized system organization did not finish'
 Assert ($window.Current.Name -eq 'PathMan · 管理员操作已授权') 'Reusable authorization updates the title'
 Assert ((Raw 'Path' $true) -eq 'C:\MachineTool;C:\Windows\System32;D:\MachineTool;F:\IgnoredBeforeEnabling;%EXTERNEL_PATH%' -and (Raw 'EXTERNEL_PATH' $true) -eq 'F:\ExternalTool') 'Authorized system organization writes the expected PATH while preserving additions made before that scope was enabled'
 Stage 'ready-machine-second'
 WaitFor {@((State).changes|Where-Object {$_.source -eq 'PathMan' -and $_.scope -eq 'Machine' -and $_.name -eq 'EXTERNEL_PATH'}).Count -ge 2} 'Subsequent system writes did not reuse authorization'
 Start-Sleep -Seconds 2
 Assert (@((State).changes|Where-Object {$_.source -eq '外部程序' -and $_.scope -eq 'Machine' -and $_.name -eq 'Path'}).Count -eq 3) 'Elevated own writes are excluded from external monitoring'
 Assert (@((State).pendingPathOrganizations).Count -eq 0) 'Both scopes finish without pending work'
 Assert ((Raw 'EXTERNEL_PATH' $true) -eq 'F:\ExternalTool;G:\AnotherExternalTool') 'Subsequent system additions append to the existing external group'
 [void]$app.CloseMainWindow();WaitFor {$app.HasExited} 'GUI did not close'
 SetUser 'PATHMAN_CLOSED' 'while-gui-closed'
 WaitFor {@((State).changes|Where-Object {$_.name -eq 'PATHMAN_CLOSED'}).Count -eq 1} 'Background stopped with the GUI'
 Assert ($null -ne (Get-Process PathMan.Service -ErrorAction SilentlyContinue)) 'Monitoring continues with the desktop application closed'
 StartApp;Go '变量监控';Assert (Checked 'EnableMonitoring') 'Reopening the GUI reflects the running monitor'
 Assert (!(Checked 'AutoArrangeUser') -and (Checked 'AutoArrangeMachine')) 'Both scope settings survive closing and reopening the GUI'
 Toggle 'EnableMonitoring';WaitFor {!((State).monitoringEnabled) -and !(Get-Process PathMan.Service -ErrorAction SilentlyContinue)} 'Stop switch did not stop background'
 SetUser 'PATHMAN_STOPPED' 'not-monitored';Start-Sleep -Seconds 2
 Assert (@((State).changes|Where-Object {$_.name -eq 'PATHMAN_STOPPED'}).Count -eq 0) 'Stopped monitoring does not record subsequent writes'
 Toggle 'EnableMonitoring';WaitFor {(State).monitoringEnabled} 'Restart failed'
 Assert (@((State).changes|Where-Object {$_.name -eq 'PATHMAN_STOPPED'}).Count -eq 0) 'Restart establishes a fresh baseline without inventing missed changes'
 Stage 'complete'
 @{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8
}catch{
 @{passed=$false;error=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8
 throw
}
