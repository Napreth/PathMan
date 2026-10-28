[CmdletBinding()]
param([string]$Installer='C:\PathManCommandLineTest\setup.exe',[string]$ResultPath='C:\PathManCommandLineTest\results.json')
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable VM only; restore the initial checkpoint after testing.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$checks=[Collections.Generic.List[string]]::new()
$directory=Join-Path $env:LOCALAPPDATA 'Programs\PathMan'
$data=Join-Path $env:LOCALAPPDATA 'PathMan\environment.json'
$bin=Join-Path $directory 'bin'
$cli=Join-Path $bin 'pathman.exe'
$gui=$null
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message);[IO.File]::WriteAllText($ResultPath+'.stage',$message)}
function WaitFor([scriptblock]$test,[string]$message){$limit=[DateTime]::UtcNow.AddSeconds(45);do{if(& $test){return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $limit);throw $message}
function State {Get-Content $data -Raw -Encoding UTF8|ConvertFrom-Json}
function Save($state){$state|ConvertTo-Json -Depth 40|Set-Content $data -Encoding UTF8}
function Value([string]$name){$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment');try{$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}}
function SetValue([string]$name,[string]$value){$key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment');try{$key.SetValue($name,$value,[Microsoft.Win32.RegistryValueKind]::ExpandString)}finally{$key.Dispose()}}
function Find([string]$name){$script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name))}
function Navigate([string]$name){$item=Find $name;$item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 250}
function OpenGui {
 $script:gui=Start-Process (Join-Path $directory 'PathMan.exe') -PassThru
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$gui.Id)
 WaitFor {$script:window=@([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)|Where-Object {!$_.Current.IsOffscreen})|Select-Object -First 1;$null -ne $script:window} 'The main window did not open'
 WaitFor {if($null -eq (Find '启用命令行版本切换')){Navigate '版本管理'};$script:toggle=Find '启用命令行版本切换';$null -ne $script:toggle -and $script:toggle.Current.IsEnabled} 'The command-line switch did not become ready'
}
function CloseGui {$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close();if(!$gui.WaitForExit(15000)){throw 'The main window did not close'};$gui.Dispose();$script:gui=$null}
function Toggle([bool]$enabled){$toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle();WaitFor {([bool](State).commandLineVersionSwitchingEnabled) -eq $enabled -and $toggle.Current.IsEnabled} 'The command-line switch did not save'}
function Invoke-TestCli([int]$expected){
 $start=[Diagnostics.ProcessStartInfo]::new($cli,'list');$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.StandardOutputEncoding=[Text.Encoding]::UTF8;$start.StandardErrorEncoding=[Text.Encoding]::UTF8
 $process=[Diagnostics.Process]::Start($start)
 try{$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();if(!$process.WaitForExit(15000)){$process.Kill();throw 'CLI did not exit'};$output=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult();Assert ($process.ExitCode -eq $expected) "CLI list exits with $expected (actual $($process.ExitCode))";if($expected -ne 0){Assert ($errorText -match '未启用') 'Disabled CLI explains how to enable command-line switching'};return $output}finally{$process.Dispose()}
}
function Install { $p=Start-Process $Installer -ArgumentList ('/S /CurrentUser /D='+$directory) -WindowStyle Hidden -PassThru;Assert ($p.WaitForExit(90000) -and $p.ExitCode -eq 0) 'The installer completes successfully' }
try {
 $original=Value 'Path'
 Install
 Assert ((Value 'Path') -ceq $original) 'Installation never registers bin or rewrites the original PATH'
 New-Item -ItemType Directory -Path (Split-Path $data) -Force|Out-Null
 $state=Get-Content (Join-Path $PSScriptRoot 'groups.json') -Raw -Encoding UTF8|ConvertFrom-Json
 foreach($pair in @{preferences=@{language='zh-CN';theme='system';autoCheckUpdates=$false};versionProjects=@(@{name='test';variableName='PATHMAN_CLI_TEST_HOME';type='User';versions=@{'1'='C:\fixture\one'}});commandLineVersionSwitchingEnabled=$false}.GetEnumerator()){$state|Add-Member -NotePropertyName $pair.Key -NotePropertyValue $pair.Value -Force};Save $state
 $null=Invoke-TestCli 1
 OpenGui
 Assert ($toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) 'Command-line switching defaults to off'
 Toggle $true
 Assert (@((Value 'Path').Split(';')|Where-Object {$_.TrimEnd('\') -ieq $bin}).Count -eq 1) 'Enabling registers bin exactly once in user PATH'
 Assert ((Invoke-TestCli 0) -match 'test:') 'Enabled CLI reads the saved version catalog'
 Toggle $false
 Assert ((Value 'Path') -ceq $original) 'Disabling restores the exact original PATH including empty entries'
 Assert (@((State).versionProjects).Count -eq 1) 'Disabling preserves version projects and their registered versions'
 $null=Invoke-TestCli 1
 CloseGui
 SetValue 'SOFTWARE7_PATH' 'C:\fixture\tools;'
 $groupPath=$original+';%SOFTWARE7_PATH%'
 SetValue 'Path' $groupPath
 $state=State;foreach($pair in @{monitoringEnabled=$true;autoArrangeUserPath=$true}.GetEnumerator()){$state|Add-Member -NotePropertyName $pair.Key -NotePropertyValue $pair.Value -Force};Save $state
 OpenGui
 Toggle $true
 Assert ((Value 'Path') -ceq $groupPath) 'Automatic placement leaves PATH byte-for-byte unchanged'
 Assert ((Value 'SOFTWARE7_PATH') -ceq ('C:\fixture\tools;;'+$bin)) 'Automatic placement writes directly to the referenced numbered software variable'
 $null=Invoke-TestCli 0
 CloseGui
 $beforeGroup=Value 'SOFTWARE7_PATH'
 Install
 Assert ((Value 'Path') -ceq $groupPath -and (Value 'SOFTWARE7_PATH') -ceq $beforeGroup) 'Reinstallation leaves both PATH and the software variable untouched even when CLI is enabled'
 OpenGui
 Toggle $false
 Assert ((Value 'SOFTWARE7_PATH') -ceq 'C:\fixture\tools;' -and (Value 'Path') -ceq $groupPath) 'Disabling removes only bin from the software variable and preserves its PATH reference'
 CloseGui
 SetValue 'Path' $original
 OpenGui
 Navigate '健康检测'
 WaitFor {@($window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|Where-Object {$_.Current.Name -like '*已启用自动整理，但 PATH 未引用*'}).Count -gt 0} 'The missing software reference was not reported in health checks'
 Assert $true 'Health checks display the missing SOFTWARE*_PATH reference error'
 Navigate '版本管理';$script:toggle=Find '启用命令行版本切换'
 Toggle $true
 Assert ((Value 'Path') -ceq $original -and (Value 'SOFTWARE7_PATH') -ceq ('C:\fixture\tools;;'+$bin)) 'A missing reference is reported without silently inserting a new PATH entry'
 Toggle $false
 CloseGui
 @{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8
}catch {@{passed=$false;error=$_.Exception.Message;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8;throw}
finally {if($gui -and !$gui.HasExited){$gui.Kill();$gui.WaitForExit()}}
