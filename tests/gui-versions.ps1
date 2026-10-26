[CmdletBinding()]
param([string]$AppDirectory='C:\PathManTestApp',[string]$ResultPath='C:\PathManVersionTest\results.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable VM only; restore the initial checkpoint afterwards.'}
$data=Join-Path $env:LOCALAPPDATA 'PathMan';$cli=Join-Path $AppDirectory 'bin\pathman.exe'
if($PrepareFixture){
 Get-Process PathMan,PathMan.Service,OneDrive,SystemSettings -ErrorAction SilentlyContinue|Stop-Process -Force
 $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
 try{$user.SetValue('PATHMAN_VERSION_USER','C:\SDK\one');$user.SetValue('PATHMAN_VERSION_EXTRA','keep-current');$user.SetValue('Path','%PATHMAN_VERSION_USER%',[Microsoft.Win32.RegistryValueKind]::ExpandString)}finally{$user.Dispose()}
 $machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true)
 try{$machine.SetValue('PATHMAN_VERSION_MACHINE','before');$machine.SetValue('PATHMAN_VERSION_EXTRA','machine-extra');$machine.SetValue('PATHMAN_CUSTOM_TARGET','custom')}finally{$machine.Dispose()}
 New-Item -ItemType Directory -Path $data -Force|Out-Null
 New-Item -ItemType Directory -Path C:\SDK\chosen -Force|Out-Null
 @{groups=@(@{id='paths';name='路径变量';rule='paths'},@{id='system';name='已改名的内置分类';rule='builtin'},@{id='other';name='其他变量';rule='fallback'});assignments=@{'Machine:ComSpec'='other';'Machine:PATHMAN_CUSTOM_TARGET'='system'};revision=[guid]::NewGuid().ToString('N')}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $data 'environment.json') -Encoding UTF8
 return
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class InlineInput{[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);[DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);[DllImport("user32.dll")]public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);}'
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message);[IO.File]::WriteAllText($ResultPath+'.progress',$message)}
function Stage([string]$name){[IO.File]::WriteAllText($ResultPath+'.stage',$name)}
function WaitFor([scriptblock]$test,[string]$message){for($i=0;$i -lt 800;$i++){if(& $test){return};Start-Sleep -Milliseconds 150};throw $message}
function Id([string]$id,$root=$window){if($null -ne $root){$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}}
function Find($root,[string]$name,$type=$null){if($null -eq $root){return $null};$c=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name);if($type){$c=[Windows.Automation.AndCondition]::new($c,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$type))};$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$c)}
function Invoke($element){WaitFor {$null -ne $element -and $element.Current.IsEnabled} 'Action is not ready';[IO.File]::WriteAllText($ResultPath+'.action',$element.Current.AutomationId+' '+$element.Current.Name);$r=$element.Current.BoundingRectangle;ClickAt ([int]($r.Left+$r.Width/2)) ([int]($r.Top+$r.Height/2))}
function SelectNode($element){$element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 200}
function Go([string]$name){SelectNode (Find (Id 'Navigation') $name ([Windows.Automation.ControlType]::ListItem))}
function Project([string]$name){Find (Id 'ProjectsList') $name ([Windows.Automation.ControlType]::ListItem)}
function SelectProject([string]$name){WaitFor {$null -ne (Project $name)} 'Project list is not ready';SelectNode (Project $name)}
function Version([string]$name){Find (Id 'VersionsList') $name ([Windows.Automation.ControlType]::ListItem)}
function Dialog([string]$name){Find ([Windows.Automation.AutomationElement]::RootElement) $name ([Windows.Automation.ControlType]::Window)}
function InputBox{Find $window '行内编辑' ([Windows.Automation.ControlType]::Edit)}
function Key([byte]$key){[InlineInput]::keybd_event($key,0,0,[UIntPtr]::Zero);[InlineInput]::keybd_event($key,0,2,[UIntPtr]::Zero);Start-Sleep -Milliseconds 150}
function SetInput([string]$value){WaitFor {$null -ne (InputBox)} 'Inline editor missing';(InputBox).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value);(InputBox).SetFocus()}
function CommitInput([string]$value){SetInput $value;Key 13;WaitFor {$null -eq (InputBox)} 'Inline edit did not commit'}
function ClickAt([int]$x,[int]$y){[void][InlineInput]::SetCursorPos($x,$y);[InlineInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[InlineInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 180}
function ToggleOption($element){$r=$element.Current.BoundingRectangle;ClickAt ([int]($r.Right-20)) ([int]($r.Top+$r.Height/2))}
function ClickCell($row,[double]$fraction,[switch]$Double){$r=$row.Current.BoundingRectangle;[void][InlineInput]::SetForegroundWindow($app.MainWindowHandle);$x=[int]($r.Left+$r.Width*$fraction);$y=[int]($r.Top+$r.Height/2);[IO.File]::WriteAllText($ResultPath+'.action',"Cell $($row.Current.Name) $x,$y double=$Double");if(!$Double){ClickAt $x $y;return};[void][InlineInput]::SetCursorPos($x,$y);[InlineInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[InlineInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 60;[InlineInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[InlineInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 180}
function Cell($row,[double]$fraction){ClickCell $row $fraction -Double;WaitFor {$null -ne (InputBox)} 'Double-click did not enter inline editing'}
function Blur{ClickAt 505 95;WaitFor {$null -eq (InputBox)} 'Blur did not commit the row'}
function State{Get-Content (Join-Path $data 'environment.json') -Raw|ConvertFrom-Json}
function Config([string]$name){(State).versionProjects|Where-Object name -eq $name}
function Raw([string]$name,[switch]$Machine){$k=if($Machine){[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment')}else{[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')};try{$k.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$k.Dispose()}}
function RunCli([string[]]$arguments,[int]$expected=0){$savedPreference=$ErrorActionPreference;try{$ErrorActionPreference='Continue';$text=& $cli @arguments 2>&1;$exitCode=$LASTEXITCODE}finally{$ErrorActionPreference=$savedPreference};if($exitCode -ne $expected){throw ('Unexpected CLI result: '+($arguments -join ' ')+': '+($text -join ' '))};$text -join "`n"}
function ClipboardText([string]$value,[switch]$Read){
 $runspace=[Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace();$runspace.ApartmentState='STA';$runspace.ThreadOptions='ReuseThread';$runspace.Open();$shell=[powershell]::Create();$shell.Runspace=$runspace
 try{$null=$shell.AddScript({param($text,$read)Add-Type -AssemblyName System.Windows.Forms;if($read){[Windows.Forms.Clipboard]::GetText()}else{[Windows.Forms.Clipboard]::SetText($text)}}.ToString()).AddArgument($value).AddArgument([bool]$Read);$result=$shell.Invoke();if($shell.HadErrors){throw ($shell.Streams.Error|Out-String)};if($Read){[string]$result[0]}}
 finally{$shell.Dispose();$runspace.Dispose()}
}
function Browse([string]$project){ClickCell (Project $project) 0.7 -Double;WaitFor {$null -ne (Dialog '选择环境变量')} 'Double-click did not open the target picker'}
function Target([string]$name,[string]$scope){Find (Id $scope (Dialog '选择环境变量')) $name ([Windows.Automation.ControlType]::TreeItem)}
function Choose([string]$name,[string]$scope){SelectNode (Target $name $scope);Invoke (Id 'ChooseTargetButton' (Dialog '选择环境变量'))}
function CancelPicker{Invoke (Find (Dialog '选择环境变量') '取消' ([Windows.Automation.ControlType]::Button))}
try{
 $null=RunCli @('add','java','8','D:/Java/jdk-8/','--variable','PATHMAN_VERSION_MACHINE','--type','Machine')
 $null=RunCli @('add','java','25','D:/Java/jdk-25/')
 $null=RunCli @('add','tools','1','C:\SDK\one','--variable','PATHMAN_VERSION_USER')
 $null=RunCli @('add','forbidden','1','x','--variable','Path') 1
 $null=RunCli @('add','forbidden','1','x','--variable','ComSpec','--type','Machine') 1
 Assert (!(Config 'forbidden')) 'CLI uses the same PATH and built-in target restrictions'
 $app=Start-Process (Join-Path $AppDirectory 'PathMan.exe') -PassThru
 WaitFor {$app.Refresh();$app.MainWindowHandle -ne 0} 'GUI did not open'
 $window=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle);[void][InlineInput]::SetForegroundWindow($app.MainWindowHandle);[void][InlineInput]::SetWindowPos($app.MainWindowHandle,[IntPtr](-1),0,0,0,0,0x43)
 WaitFor {$null -ne (Id 'UserVariablesTree')} 'Initial page missing';Go '版本管理';WaitFor {$null -ne (Project 'java')} 'CLI projects missing'
 $a=(Id 'ProjectsList').Current.BoundingRectangle;$b=(Id 'VersionsList').Current.BoundingRectangle
 Assert ([Math]::Abs($a.Width-$b.Width) -lt 3 -and [Math]::Abs($a.Top-$b.Top) -lt 3 -and [Math]::Abs($a.Height-$b.Height) -lt 3) 'The two list panels have equal widths, tops and heights'
 foreach($action in 'AddVersionButton','RenameVersionButton','EditVersionButton','BrowseVersionButton','CopyVersionButton','DeleteVersionButton'){Assert ((Id $action).Current.BoundingRectangle.Left -gt $b.Right) ($action+' is outside the version list')}
 Assert ([Math]::Abs((Id 'AddVersionButton').Current.BoundingRectangle.Top-$b.Top) -lt 3) 'The external button column starts at the top of the editing panel'
 Assert ($null -eq (Id 'BrowseTargetButton')) 'Project rows have no inline browse button'
 ClickCell (Project 'java') 0.15;Assert ($null -eq (InputBox)) 'A single click on a project name only selects it'
 ClickCell (Project 'java') 0.7;Assert ($null -eq (InputBox) -and $null -eq (Dialog '选择环境变量')) 'A single click on a variable does not edit or open a picker'
 Invoke (Id 'NewProjectButton');WaitFor {$null -ne (InputBox)} 'Project plus did not create an inline row'
 Assert ($null -eq (Dialog '新建版本项目')) 'Project plus starts an empty inline cell without a new-project window'
 Blur;Assert (@((State).versionProjects).Count -eq 2) 'An untouched empty project row is removed on blur'
 Invoke (Id 'NewProjectButton');SetInput 'python';Blur;WaitFor {$null -ne (Project 'python')} 'Named project was not saved'
 Assert ((Config 'python').variableName -eq '') 'A named row can be retained before binding an environment variable'
 SelectProject 'python';Browse 'python'
 WaitFor {$null -ne (Target 'ComSpec' 'MachineTargets')} 'Picker did not list the saved machine variables'
 Assert (!(Target 'Path' 'UserTargets').Current.IsEnabled -and !(Target 'Path' 'MachineTargets').Current.IsEnabled) 'Both user and machine PATH rows are unselectable'
 Assert (!(Target 'ComSpec' 'MachineTargets').Current.IsEnabled -and (Target 'PATHMAN_CUSTOM_TARGET' 'MachineTargets').Current.IsEnabled) 'Built-in restrictions use the name list rather than the contents or name of a displayed group'
 Stage 'picker-default';WaitFor {Test-Path C:\PathManVersionTest\continue-picker} 'Driver did not inspect the picker'
 Choose 'PATHMAN_VERSION_EXTRA' 'UserTargets';WaitFor {(Config 'python').variableName -eq 'PATHMAN_VERSION_EXTRA'} 'Binding selection was not saved'
 Assert ((Config 'python').type -eq 'User') 'Browsing sets the target variable and scope together'
 Assert ($null -ne (Find (Project 'python') 'PATHMAN_VERSION_EXTRA（用户）')) 'The variable column includes its scope in one display cell'
 Browse 'python';CancelPicker
 Assert ((Config 'python').variableName -eq 'PATHMAN_VERSION_EXTRA' -and (Config 'python').type -eq 'User') 'Canceling the target picker preserves both the variable and its scope'
 Browse 'python';Choose 'PATHMAN_VERSION_EXTRA' 'MachineTargets';WaitFor {(Config 'python').type -eq 'Machine'} 'Machine target binding was not saved'
 Assert ((Config 'python').variableName -eq 'PATHMAN_VERSION_EXTRA') 'A completed picker updates the variable and scope together'
 Browse 'python';Choose 'PATHMAN_VERSION_EXTRA' 'UserTargets';WaitFor {(Config 'python').type -eq 'User'} 'User target selection failed'
 Invoke (Id 'AddVersionButton');WaitFor {$null -ne (InputBox)} 'Version plus did not create a row';Blur
 Assert (@((Config 'python').versions.PSObject.Properties).Count -eq 0) 'An untouched empty version row is removed on blur'
 Invoke (Id 'AddVersionButton');CommitInput '3.14';WaitFor {$null -ne (Version '3.14')} 'Version name was not saved'
 Assert ((Version '3.14').FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)).Count -eq 0) 'Version rows contain no inline action buttons'
 ClickCell (Version '3.14') 0.45;Assert ($null -eq (InputBox)) 'A single click on a version value only selects it'
 Cell (Version '3.14') 0.45;SetInput 'literal value;more';Blur
 Assert ((Config 'python').versions.'3.14' -eq 'literal value;more' -and (Raw 'PATHMAN_VERSION_EXTRA') -eq 'keep-current') 'Editing the value section saves metadata without changing the current environment value'
 Invoke (Id 'RenameVersionButton');Assert ((InputBox).Current.BoundingRectangle.Width -le 82) 'Rename opens an input confined to the version name column'
 CommitInput 'stable';WaitFor {$null -ne (Version 'stable')} 'External rename button failed'
 Invoke (Id 'EditVersionButton');SetInput 'pending value'
 Invoke (Id 'BrowseVersionButton');WaitFor {$null -ne (Dialog '选择版本目录')} 'Folder browsing was blocked during value editing'
 Assert ((Config 'python').versions.stable -eq 'literal value;more') 'Opening browse does not save the unfinished value before selection'
 Invoke (Find (Dialog '选择版本目录') 'Cancel');WaitFor {$null -ne (InputBox)} 'Canceled browsing lost the value editor'
 Assert ((InputBox).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'pending value' -and (Config 'python').versions.stable -eq 'literal value;more') 'Canceling browse preserves the pending value without saving it'
 Invoke (Id 'BrowseVersionButton');WaitFor {$null -ne (Dialog '选择版本目录')} 'Folder picker did not reopen'
 Stage 'folder-picker';WaitFor {Test-Path C:\PathManVersionTest\continue-folder} 'Driver did not inspect the folder picker'
 $folderBox=Id '1152' (Dialog '选择版本目录');$folderRect=$folderBox.Current.BoundingRectangle;ClickAt ([int]($folderRect.Left+50)) ([int]($folderRect.Top+$folderRect.Height/2));ClipboardText 'C:\SDK\chosen';[InlineInput]::keybd_event(17,0,0,[UIntPtr]::Zero);Key 86;[InlineInput]::keybd_event(17,0,2,[UIntPtr]::Zero);Invoke (Find (Dialog '选择版本目录') 'Select Folder')
 WaitFor {(Config 'python').versions.stable -eq 'C:\SDK\chosen'} 'Selected folder was not saved'
 Assert ($null -eq (InputBox) -and (Raw 'PATHMAN_VERSION_EXTRA') -eq 'keep-current') 'A completed browse saves the chosen version value without switching the environment'
 ClipboardText 'before-copy';Invoke (Id 'CopyVersionButton');Assert ((ClipboardText -Read) -eq 'C:\SDK\chosen') 'Copy uses the selected version value'
 Cell (Version 'stable') 0.45;SetInput 'literal value;more';SelectProject 'tools'
 Assert ((Config 'python').versions.stable -eq 'literal value;more' -and @((Config 'tools').versions.PSObject.Properties).Count -eq 1) 'Changing projects saves the edited version into its original project'
 SelectProject 'python';WaitFor {$null -ne (Version 'stable')} 'Edited project did not reload';SelectNode (Version 'stable')
 Invoke (Id 'SwitchVersionButton');WaitFor {(Raw 'PATHMAN_VERSION_EXTRA') -eq 'literal value;more'} 'User version switch failed'
 Cell (Version 'stable') 0.1;SetInput 'invalid:label';Invoke (Id 'DeleteVersionButton')
 WaitFor {$null -eq (Version 'stable')} 'Delete did not remove the version'
 Assert ((Raw 'PATHMAN_VERSION_EXTRA') -eq 'literal value;more' -and $null -eq (Dialog '删除版本')) 'The external delete button bypasses an unfinished edit and removes metadata only'
 Cell (Project 'python') 0.15;SetInput 'py';Blur;WaitFor {$null -ne (Project 'py')} 'Project inline rename failed'
 Assert ((Config 'py').variableName -eq 'PATHMAN_VERSION_EXTRA') 'Double-click renaming a project preserves its target and scope'
 Go '关于';Invoke (Id 'AdvancedSettingsLink');ToggleOption (Id 'AllowBuiltInVersionTargets')
 WaitFor {$null -ne (Dialog '允许选择内置系统变量')} 'Advanced option did not show the red warning'
 Stage 'advanced-warning';WaitFor {Test-Path C:\PathManVersionTest\continue-warning} 'Driver did not inspect the red warning'
 Invoke (Id 'ConfirmDangerButton' (Dialog '允许选择内置系统变量'));WaitFor {(State).allowBuiltInVersionTargets} 'Advanced option did not persist'
 Go '版本管理';SelectProject 'py';Browse 'py'
 Assert ((Target 'ComSpec' 'MachineTargets').Current.IsEnabled -and !(Target 'Path' 'MachineTargets').Current.IsEnabled) 'The advanced switch unlocks built-ins but never PATH'
 Choose 'ComSpec' 'MachineTargets';WaitFor {$null -ne (Dialog '选择内置系统变量')} 'Selecting a built-in target did not show a red warning'
 Invoke (Id 'ConfirmDangerButton' (Dialog '选择内置系统变量'));WaitFor {(Config 'py').variableName -eq 'ComSpec'} 'Approved built-in binding was not saved'
 Invoke (Id 'AddVersionButton');CommitInput 'test';WaitFor {$null -ne (Version 'test')} 'Built-in version metadata was not added'
 $originalComSpec=Raw 'ComSpec' -Machine
 Go '关于';if($null -ne (Id 'AdvancedSettingsLink') -and !(Id 'AdvancedSettingsLink').Current.IsOffscreen){Invoke (Id 'AdvancedSettingsLink')}
 ToggleOption (Id 'AllowBuiltInVersionTargets');WaitFor {!((State).allowBuiltInVersionTargets)} 'Advanced option did not disable'
 Go '版本管理';SelectProject 'py';WaitFor {$null -ne (Version 'test')} 'Version list missing after returning'
 SelectNode (Version 'test');Assert (!(Id 'SwitchVersionButton').Current.IsEnabled) 'Disabling the advanced option also disables existing built-in bindings'
 $null=RunCli @('switch','py','test') 1
 Assert ((Raw 'ComSpec' -Machine) -eq $originalComSpec) 'CLI cannot bypass a disabled built-in restriction or alter its active value'
 Browse 'py';Choose 'PATHMAN_CUSTOM_TARGET' 'MachineTargets';WaitFor {(Config 'py').variableName -eq 'PATHMAN_CUSTOM_TARGET'} 'A custom variable inside the built-in display group could not be selected'
 Assert ((Config 'py').variableName -eq 'PATHMAN_CUSTOM_TARGET') 'A user-defined variable remains selectable even when manually grouped with system built-ins'
 Cell (Project 'py') 0.15;SetInput 'invalid:name';Invoke (Id 'DeleteProjectButton' (Project 'py'));WaitFor {$null -eq (Project 'py')} 'Project trash failed'
 Assert ((Raw 'PATHMAN_VERSION_EXTRA') -eq 'literal value;more' -and (Raw 'ComSpec' -Machine) -eq $originalComSpec -and (Raw 'PATHMAN_CUSTOM_TARGET' -Machine) -eq 'custom') 'Project trash removes only metadata and preserves every previously bound variable'
 SelectProject 'java';WaitFor {$null -ne (Version '25')} 'Machine versions missing';SelectNode (Version '25');Stage 'gui-machine-uac';Invoke (Id 'SwitchVersionButton')
 WaitFor {(Raw 'PATHMAN_VERSION_MACHINE' -Machine) -eq 'D:/Java/jdk-25/'} 'Machine version switch did not complete after authorization'
 Assert ((Raw 'PATHMAN_VERSION_MACHINE' -Machine) -eq 'D:/Java/jdk-25/') 'The external switch button writes the selected machine version through administrator authorization'
 Stage 'complete';@{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8
}catch{@{passed=$false;error=$_.Exception.Message;trace=$_.ScriptStackTrace;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8;throw}
