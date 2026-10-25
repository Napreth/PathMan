[CmdletBinding()]
param([string]$ResultPath='C:\PathManLiveTest\results.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable Windows VM only. Restore the initial checkpoint afterwards.'}
$data=Join-Path $env:LOCALAPPDATA 'PathMan'
if($PrepareFixture){
 Get-Process OneDrive,SystemSettings -ErrorAction SilentlyContinue|Stop-Process -Force
 Get-Process PathMan,PathMan.Service -ErrorAction SilentlyContinue|Stop-Process -Force
 $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
 try{
  foreach($pair in @{Path='C:\Live\Base;C:\Live\Keep';PATHMAN_LIVE_PATH='C:\Live\One;C:\Live\Two';PATHMAN_LIVE_SCALAR='before'}.GetEnumerator()){$user.SetValue($pair.Key,$pair.Value,[Microsoft.Win32.RegistryValueKind]::String)}
  foreach($name in @('EXTERNEL_USER_PATH','EXTERNEL_PATH','SOFTWARE_PATH')){$user.DeleteValue($name,$false)}
 }finally{$user.Dispose()}
 $run=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
 try{$run.SetValue('PathMan','"C:\PathManTestApp\internal\PathMan.Service.exe"')}finally{$run.Dispose()}
 New-Item -ItemType Directory -Path $data -Force|Out-Null
 @{groups=@(@{id='paths';name='路径变量';rule='paths'},@{id='system';name='系统变量';rule='builtin'},@{id='other';name='其他变量';rule='fallback'},@{id='external';name='外部变量';rule='external';requiresMonitoring=$true});autoArrangeUserPath=$true;revision=[guid]::NewGuid().ToString('N')}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $data 'environment.json') -Encoding UTF8
 return
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class LiveInput{[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);[DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);[DllImport("user32.dll")]public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);}'
$checks=[Collections.Generic.List[string]]::new()
function State{Get-Content (Join-Path $data 'environment.json') -Raw|ConvertFrom-Json}
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message);try{[IO.File]::WriteAllText($ResultPath+'.progress',$message)}catch [IO.IOException]{}}
function WaitFor([scriptblock]$condition,[string]$message){for($i=0;$i -lt 800;$i++){if(& $condition){return};Start-Sleep -Milliseconds 150};throw $message}
function Find($root,[string]$name,$type=$null){if($null -eq $root){return $null};$condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name);if($type){$condition=[Windows.Automation.AndCondition]::new($condition,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$type))};$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)}
function Id([string]$name){$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$name))}
function Invoke($element){$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 200}
function SelectNode($element){$element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 200}
function Go([string]$name){SelectNode (Find (Id 'Navigation') $name ([Windows.Automation.ControlType]::ListItem))}
function Toggle([string]$name){WaitFor {$null -ne (Id $name) -and (Id $name).Current.IsEnabled} ('Toggle is not enabled: '+$name);(Id $name).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle();Start-Sleep -Milliseconds 250}
function Checked([string]$name){(Id $name).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On}
function Key([byte]$code){[LiveInput]::keybd_event($code,0,0,[UIntPtr]::Zero);[LiveInput]::keybd_event($code,0,2,[UIntPtr]::Zero);Start-Sleep -Milliseconds 180}
function Input{Find $window '行内编辑' ([Windows.Automation.ControlType]::Edit)}
function Node([string]$name){Find (Id 'UserVariablesTree') $name ([Windows.Automation.ControlType]::TreeItem)}
function Row([string]$name){Find (Id 'ValuesList') $name ([Windows.Automation.ControlType]::ListItem)}
function EditRow([string]$name,[string]$value){WaitFor {$null -ne (Row $name) -and (Id 'UserVariablesTree').Current.IsEnabled} 'Editable row missing';SelectNode (Row $name);WaitFor {(Id 'EditEntryButton').Current.IsEnabled} 'Edit action did not enable';Invoke (Id 'EditEntryButton');WaitFor {$null -ne (Input)} 'Inline editor missing';(Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value);(Input).SetFocus();$r=(Input).Current.BoundingRectangle;[void][LiveInput]::SetCursorPos([int]($r.Left+5),[int]($r.Top+$r.Height/2));[LiveInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[LiveInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Key 13;WaitFor {$null -eq (Input)} 'Inline edit did not commit'}
function Raw([string]$name){$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment');try{$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}}
function SetUser([string]$name,[string]$value){$key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true);try{$key.SetValue($name,$value,[Microsoft.Win32.RegistryValueKind]::ExpandString)}finally{$key.Dispose()}}
function Startup{$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run');try{$key.GetValue('PathMan',$null)}finally{$key.Dispose()}}
function Save{Invoke (Id 'SaveButton');WaitFor {!(Id 'SaveButton').Current.IsEnabled -and (Id 'SaveButton').Current.ItemStatus -eq '已保存' -and (Id 'UserVariablesTree').Current.IsEnabled} 'Save did not finish'}
function Stage([string]$name){[IO.File]::WriteAllText($ResultPath+'.stage',$name)}
function Probe([string]$image,[string]$value,[string]$entry=''){
 $arguments=($entry+' Path "'+$value+'"').Trim()
 $process=Start-Process ('C:\PathManLiveTest\'+$image) -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
 if($process.ExitCode -ne 0){throw ('Origin probe failed: '+$image+' '+$process.ExitCode)}
 return $process.Id
}
try{
 $app=Start-Process C:\PathManTestApp\PathMan.exe -PassThru
 WaitFor {$app.Refresh();$app.MainWindowHandle -ne 0} 'GUI did not open'
 $window=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle);[void][LiveInput]::SetForegroundWindow($app.MainWindowHandle)
 [void][LiveInput]::SetWindowPos($app.MainWindowHandle,[IntPtr](-1),0,0,0,0,0x43)
 WaitFor {$null -ne (Node 'PATHMAN_LIVE_PATH')} 'Variables did not load'
 Assert ($null -eq (Id 'RefreshButton')) 'The obsolete refresh button is removed'
 WaitFor {$null -eq (Startup)} 'A startup registration without background permission was not removed'
 Assert (!(State).monitoringEnabled -and !(Get-Process PathMan.Service -ErrorAction SilentlyContinue)) 'Foreground observation does not start the background'
 SelectNode (Node 'PATHMAN_LIVE_PATH')
 EditRow 'C:\Live\Two' 'D:\Live\Mine'
 SetUser 'PATHMAN_LIVE_PATH' 'X:\Live\Incoming;C:\Live\Two;E:\Live\External'
 WaitFor {$null -ne (Row '+E:\Live\External') -and $null -ne (Row '+D:\Live\Mine')} 'Live external/local diff did not appear'
 Assert ((Raw 'PATHMAN_LIVE_PATH') -eq 'X:\Live\Incoming;C:\Live\Two;E:\Live\External') 'Readonly observation leaves the actual external value unchanged'
 Assert ($null -ne (Row '-C:\Live\One') -and $null -ne (Row '+X:\Live\Incoming') -and $null -ne (Row '-C:\Live\Two') -and (Id 'UndoExternalButton').Current.IsEnabled) 'External red/green rows, blue user rows and external actions are visible in the editor'
 Stage 'review-visible'
 WaitFor {Test-Path C:\PathManLiveTest\continue-review} 'The driver did not inspect the live diff'
 Invoke (Id 'UndoExternalButton');Save
 Assert ((Raw 'PATHMAN_LIVE_PATH') -eq 'C:\Live\One;D:\Live\Mine') 'Reverting external changes and saving retains the user draft'
 SelectNode (Node 'Path')
 Toggle 'TemporaryCleanupButton'
 EditRow 'C:\Live\Base' 'C:\Live\UserMine'
 SetUser 'Path' 'C:\Live\Base;C:\Live\Keep;F:\Live\External;C:\Live\Keep'
 WaitFor {$null -ne (Row '+%EXTERNEL_USER_PATH%')} 'Temporary external cleanup did not stage a reference'
 Assert ((Raw 'Path') -eq 'C:\Live\Base;C:\Live\Keep;F:\Live\External;C:\Live\Keep' -and $null -eq (Raw 'EXTERNEL_USER_PATH')) 'Temporary cleanup modifies only the draft until Save'
 Save
 Assert ((Raw 'Path') -eq 'C:\Live\UserMine;C:\Live\Keep;%EXTERNEL_USER_PATH%' -and (Raw 'EXTERNEL_USER_PATH') -eq 'F:\Live\External') 'Temporary cleanup saves the user edit and only the external additions'
 Toggle 'TemporaryCleanupButton'
 EditRow 'C:\Live\UserMine' 'C:\Live\SecondMine'
 SetUser 'Path' ((Raw 'Path')+';G:\Live\Manual;C:\Live\Keep')
 WaitFor {$null -ne (Row '+G:\Live\Manual')} 'Second external edit was not observed'
 Invoke (Id 'CleanExternalButton')
 Assert ((Raw 'Path').EndsWith(';G:\Live\Manual;C:\Live\Keep')) 'The explicit external cleanup button also stages its changes'
 Save
 Assert ((Raw 'Path').StartsWith('C:\Live\SecondMine;') -and (Raw 'EXTERNEL_USER_PATH').Contains('G:\Live\Manual')) 'Explicit external cleanup preserves local content and migrates the external item'
 Go '变量监控'
 Assert (!(Id 'StartupEnabled').Current.IsEnabled -and !(Id 'AutoArrangeUser').Current.IsEnabled -and (Checked 'AutoArrangeUser')) 'Background-off settings are greyed out while retaining their saved values'
 $direct=Start-Process C:\PathManTestApp\internal\PathMan.Service.exe -PassThru -Wait -WindowStyle Hidden
 Assert ($direct.ExitCode -eq 5) 'A direct service launch is refused when the two required switches are off'
 Toggle 'EnableMonitoring'
 Assert (!(Get-Process PathMan.Service -ErrorAction SilentlyContinue) -and (Id 'StartupEnabled').Current.IsEnabled) 'Background permission alone does not run the process'
 Toggle 'StartupEnabled';WaitFor {Get-Process PathMan.Service -ErrorAction SilentlyContinue} 'Both switches failed to start the background'
 Toggle 'TrackOrigins';Stage 'await-origin-uac'
 WaitFor {(State).originTrackingReady} 'Precise registry origin tracking did not become ready'
 Assert ((State).trackChangeOrigins -and (Id 'IgnoreSystemEditorUser').Current.IsEnabled) 'Source tracking enables the per-scope editor exclusion options'
 Toggle 'IgnoreSystemEditorUser';Toggle 'IgnorePowerToysUser'
 $before=Raw 'Path';$value=$before+';T:\FromPowerToys'
 $writer=Probe 'PowerToys.EnvironmentVariables.exe' $value
 WaitFor {@((State).changes|Where-Object {$_.name -eq 'Path' -and $_.after -eq $value -and @($_.origins|Where-Object processId -eq $writer).Count -gt 0}).Count -gt 0} 'ETW did not record the short-lived PowerToys writer PID'
 Start-Sleep -Seconds 2
 Assert ((Raw 'Path') -eq $value) 'PowerToys-origin edits are recorded but not automatically organized'
 $idle=Start-Process C:\PathManLiveTest\PowerToys.EnvironmentVariables.exe -ArgumentList '--idle' -PassThru -WindowStyle Hidden
 $value=(Raw 'Path')+';T:\FromPowerShell';SetUser 'Path' $value
 WaitFor {(Raw 'EXTERNEL_USER_PATH').Contains('T:\FromPowerShell')} 'A nonexcluded write was incorrectly blocked by an open PowerToys process'
 Assert ((Raw 'Path').Contains('T:\FromPowerToys') -and !(Raw 'EXTERNEL_USER_PATH').Contains('T:\FromPowerToys')) 'The exclusion is based on the actual writer rather than process presence'
 $idle.Kill();$idle.WaitForExit()
 $value=(Raw 'Path')+';S:\FromSystemEditor';$writer=Probe 'SystemPropertiesAdvanced.exe' $value
 WaitFor {@((State).changes|Where-Object {$_.after -eq $value -and @($_.origins|Where-Object processId -eq $writer).Count -gt 0}).Count -gt 0} 'System editor identity was not captured'
 Start-Sleep -Seconds 2
 Assert ((Raw 'Path') -eq $value) 'SystemPropertiesAdvanced-origin edits are excluded'
 $value=(Raw 'Path')+';R:\FromSysdm';$writer=Probe 'rundll32.exe' $value 'sysdm.cpl,EditEnvironmentVariables'
 WaitFor {@((State).changes|Where-Object {$_.after -eq $value -and @($_.origins|Where-Object processId -eq $writer).Count -gt 0}).Count -gt 0} 'The sysdm entry point was not captured'
 Start-Sleep -Seconds 2
 Assert ((Raw 'Path') -eq $value) 'The sysdm environment-variable entry point is excluded using its command line'
 $value=(Raw 'Path')+';R:\OtherRundll';$writer=Probe 'rundll32.exe' $value 'shell32.dll,Control_RunDLL'
 WaitFor {(Raw 'EXTERNEL_USER_PATH').Contains('R:\OtherRundll')} 'Unrelated rundll32 writes were incorrectly excluded'
 Assert ((Raw 'EXTERNEL_USER_PATH').Contains('R:\OtherRundll')) 'Other rundll32 entry points remain eligible for cleanup'
 SetUser 'PATHMAN_LIVE_ORIGIN_DELETE' 'temporary'
 WaitFor {@((State).changes|Where-Object {$_.name -eq 'PATHMAN_LIVE_ORIGIN_DELETE' -and $_.action -eq 'create'}).Count -gt 0} 'The deletion fixture was not observed'
 $deleting=Start-Process C:\PathManLiveTest\OriginProbe.exe -ArgumentList '--delete PATHMAN_LIVE_ORIGIN_DELETE' -PassThru -Wait -WindowStyle Hidden
 WaitFor {@((State).changes|Where-Object {$_.name -eq 'PATHMAN_LIVE_ORIGIN_DELETE' -and $_.action -eq 'delete' -and @($_.origins|Where-Object processId -eq $deleting.Id).Count -gt 0}).Count -gt 0} 'Registry value deletion did not retain its actual writer PID'
 Assert ($null -eq (Raw 'PATHMAN_LIVE_ORIGIN_DELETE')) 'Delete events preserve precise writer identity as well as set events'
 Toggle 'TrackOrigins';WaitFor {!((State).originTrackingReady)} 'Source collector did not stop'
 Assert ((Checked 'IgnoreSystemEditorUser') -and !(Id 'IgnoreSystemEditorUser').Current.IsEnabled) 'Turning off source tracking preserves and disables exclusion preferences'
 Toggle 'EnableMonitoring';WaitFor {!(Get-Process PathMan.Service -ErrorAction SilentlyContinue)} 'Background did not stop'
 Assert (!(Checked 'StartupEnabled') -and $null -eq (Startup) -and !(Id 'AutoArrangeUser').Current.IsEnabled -and (Checked 'AutoArrangeUser')) 'Stopping background monitoring removes startup and preserves disabled rule preferences'
 $run=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
 try{$run.SetValue('PathMan','"C:\PathManTestApp\internal\PathMan.Service.exe"')}finally{$run.Dispose()}
 WaitFor {$null -eq (Startup)} 'An externally reenabled startup entry was not automatically disabled'
 Go '变量管理';SelectNode (Node 'PATHMAN_LIVE_PATH')
 SetUser 'PATHMAN_LIVE_PATH' 'C:\Live\FinalOne;C:\Live\FinalTwo'
 WaitFor {$null -ne (Row 'C:\Live\FinalTwo')} 'Readonly foreground monitoring stopped with the background'
 Assert (!(Get-Process PathMan.Service -ErrorAction SilentlyContinue)) 'Foreground live updates remain independent and readonly after background shutdown'
 Stage 'complete'
 @{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json -Depth 12|Set-Content $ResultPath -Encoding UTF8
}catch{
 @{passed=$false;error=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber;trace=$_.ScriptStackTrace;checks=$checks.ToArray();state=(State)}|ConvertTo-Json -Depth 14|Set-Content $ResultPath -Encoding UTF8
 throw
}
