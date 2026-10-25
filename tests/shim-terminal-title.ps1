[CmdletBinding()]
param([string]$ResultPath='C:\PathManTitleTest\results.json')
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Use only the disposable VM and restore its initial checkpoint.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$checks=[Collections.Generic.List[string]]::new();$observations=[ordered]@{}
function Assert($condition,[string]$message){if(!$condition){throw $message};$checks.Add($message)}
function WaitFor([scriptblock]$condition,[string]$message){for($n=0;$n -lt 500;$n++){if(& $condition){return};Start-Sleep -Milliseconds 100};throw $message}
function Read([string]$prefix,[string]$name){[IO.File]::ReadAllText($prefix+'.'+$name,[Text.Encoding]::UTF8)}
function WindowWithTitle([string]$title){
 $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window))
 foreach($window in $windows){
  if($window.Current.ClassName -ne 'CASCADIA_HOSTING_WINDOW_CLASS'){continue}
  if($window.Current.Name.Contains($title)){return $window}
  $tabs=$window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TabItem))
  if(@($tabs|Where-Object {$_.Current.Name.Contains($title)}).Count -gt 0){return $window}
 }
}
function CloseTerminal($window){
 if($null -eq $window){return}
 try{$pattern=$null;if($window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern,[ref]$pattern)){$pattern.Close()}}catch [Windows.Automation.ElementNotAvailableException]{}
}
function StartTerminal([string]$executable,[string[]]$parameters){
 $command=Get-Command wt.exe -ErrorAction SilentlyContinue
 $wt=if($command){$command.Source}else{Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\WindowsApps\wt.exe'}
 if(!(Test-Path $wt)){throw 'Windows Terminal is not installed for the interactive test account'}
 $arguments=@('-w','new','new-tab','--useApplicationTitle',('"'+$executable+'"'))+@($parameters|ForEach-Object {'"'+$_+'"'})
 Start-Process -FilePath $wt -ArgumentList $arguments -WindowStyle Normal
}
$root='C:\PathManTitleTest';$target=Join-Path $root '目标 程序.exe';$label=[IO.Path]::GetFileName($target)
$before=Join-Path $root 'before\terminal-alias.exe';$after=Join-Path $root 'after\terminal-alias.exe'
try{
 Get-ChildItem -LiteralPath (Join-Path $root cases) -File|ForEach-Object {Remove-Item -LiteralPath $_.FullName}
 $prefix=Join-Path $root 'cases\before';StartTerminal $before @('observe',$prefix)
 WaitFor {Test-Path ($prefix+'.ready')} 'The baseline terminal did not start'
 $observations.Before=Read $prefix ready
 Assert ($observations.Before.Contains('terminal-alias.exe')) 'The baseline reproduces the shim path as the inherited console title'
 WaitFor {$null -ne (WindowWithTitle $observations.Before)} 'The baseline title did not reach Windows Terminal'
 $window=WindowWithTitle $observations.Before;[IO.File]::WriteAllText($prefix+'.release','');Start-Sleep -Milliseconds 400;CloseTerminal $window
 $prefix=Join-Path $root 'cases\after';StartTerminal $after @('observe',$prefix)
 WaitFor {Test-Path ($prefix+'.ready')} 'The fixed terminal did not start'
 $observations.After=Read $prefix ready;$observations.Startup=Read $prefix startup
 Assert ($observations.After -eq $label -and $observations.Startup -eq $label) 'The target sees its Unicode file name as both console and startup title'
 WaitFor {$null -ne (WindowWithTitle $label)} 'The target file name did not reach the Windows Terminal tab'
 $window=WindowWithTitle $label
 $tabs=$window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TabItem))
 Assert (@($tabs|Where-Object {$_.Current.Name.Contains($label)}).Count -gt 0) 'The visible Windows Terminal tab displays the target file name instead of the shim path'
 [IO.File]::WriteAllText($prefix+'.release','');Start-Sleep -Milliseconds 400;CloseTerminal $window
 $prefix=Join-Path $root 'cases\custom';$custom='Target controls its own title';StartTerminal $after @('observe',$prefix,$custom)
 WaitFor {Test-Path ($prefix+'.ready')} 'The custom-title target did not start'
 WaitFor {$null -ne (WindowWithTitle $custom)} 'A target-defined title did not reach Windows Terminal'
 Assert ((Read $prefix ready) -eq $custom) 'The shim does not override titles chosen by the running target'
 $window=WindowWithTitle $custom;[IO.File]::WriteAllText($prefix+'.release','');Start-Sleep -Milliseconds 400;CloseTerminal $window
 $prefix=Join-Path $root 'cases\parent';StartTerminal $target @('parent',$prefix,$after)
 WaitFor {Test-Path ($prefix+'.ready')} 'The parent-title test did not finish its child'
 $observations.CallerBefore=Read $prefix before;$observations.CallerAfter=Read $prefix ready;$observations.During=Read $prefix during
 Assert ($observations.During -eq $label -and $observations.CallerBefore -eq $observations.CallerAfter -and (Read $prefix exit) -eq '37') 'An inherited console shows the target while running and restores the caller title and exit code afterward'
 WaitFor {$null -ne (WindowWithTitle 'PathMan caller title')} 'The restored caller title did not reach Windows Terminal'
 $window=WindowWithTitle 'PathMan caller title';[IO.File]::WriteAllText($prefix+'.release','');Start-Sleep -Milliseconds 300;CloseTerminal $window
 @{Passed=$true;Checks=$checks;Observations=$observations}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
}catch{@{Passed=$false;Error=$_.Exception.Message;Stack=$_.ScriptStackTrace;Checks=$checks;Observations=$observations}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8;throw}
