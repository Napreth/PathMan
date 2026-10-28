[CmdletBinding()]
param([string]$Installer='C:\PathManUacTest\before.exe',[string]$ResultPath='C:\PathManUacTest\before-results.json',[switch]$ExpectWindowlessWorker,[switch]$Uninstall)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable VM only.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -Path (Join-Path $PSScriptRoot 'installer-window-trace.cs')
Add-Type 'using System;using System.Runtime.InteropServices;public static class WizardInput{[DllImport("user32.dll")]public static extern IntPtr GetDlgItem(IntPtr h,int id);[DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);}'
function Stage([string]$value){[IO.File]::WriteAllText($ResultPath+'.stage',$value)}
function WaitFor([scriptblock]$test,[string]$message){$until=[DateTime]::UtcNow.AddSeconds(240);do{if(& $test){return};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $until);throw $message}
function NextButton {[Windows.Automation.AutomationElement]::FromHandle([WizardInput]::GetDlgItem($script:handle,1))}
function ClickNext {$point=(NextButton).GetClickablePoint();[void][WizardInput]::SetCursorPos([int]$point.X,[int]$point.Y);[WizardInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[WizardInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)}
try {
 Stage 'start'
 if($Uninstall){$setup=Start-Process $Installer -PassThru;$condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window)}
 else{$setup=Start-Process $Installer -ArgumentList '/AllUsers /LANG=2052 /D=C:\Program Files\PathMan' -PassThru;$condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$setup.Id)}
 WaitFor {$script:window=@([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)|Where-Object {!$_.Current.IsOffscreen -and $_.Current.Name -like 'PathMan*'})|Select-Object -First 1;$null -ne $script:window} 'The installer wizard did not open'
 $script:handle=[IntPtr]$window.Current.NativeWindowHandle
 if($Uninstall){$setup=[Diagnostics.Process]::GetProcessById($window.Current.ProcessId)}
 $null=$setup.Handle
 [InstallerWindowTrace]::Start($ResultPath+'.windows.tsv',$setup.ProcessName)
 [InstallerWindowTrace]::Watch($handle)
 # Scope is preselected. Advance through the directory and Start Menu pages.
 if(!$Uninstall){ClickNext;Start-Sleep -Milliseconds 500}
 Stage 'await-uac';ClickNext
 WaitFor {(NextButton).Current.IsEnabled -and (NextButton).Current.Name -match '完成'} 'The installer did not finish'
 $foreground=[WizardInput]::GetForegroundWindow()
 [InstallerWindowTrace]::Stop()
 if($foreground -ne $handle){throw 'The installer lost the foreground after UAC'}
 $trace=Get-Content ($ResultPath+'.windows.tsv') -Raw -Encoding UTF8
 $workerWindows=@([regex]::Matches($trace,'\| (\d+)/(\d+)/')|Where-Object {[int]$_.Groups[2].Value -ne $setup.Id}|ForEach-Object {$_.Groups[1].Value}|Select-Object -Unique)
 if($ExpectWindowlessWorker -and $workerWindows.Count -gt 0){throw 'The elevated worker created a window during startup'}
 foreach($box in $window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::CheckBox))){$toggle=$box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern);if($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On){$toggle.Toggle()}}
 [void][WizardInput]::PostMessage($handle,0x111,[IntPtr]1,[IntPtr]::Zero)
 if(!$setup.WaitForExit(20000)){throw 'The installer did not exit'}
 @{completed=$true;foregroundPreserved=($foreground -eq $handle);workerWindows=$workerWindows;wizard=$handle.ToInt64();process=$setup.Id;foreground=$foreground.ToInt64();exitCode=$setup.ExitCode}|ConvertTo-Json|Set-Content $ResultPath -Encoding UTF8
 Stage 'finished'
}catch{@{completed=$false;error=$_.Exception.Message}|ConvertTo-Json|Set-Content $ResultPath -Encoding UTF8;Stage 'failed';throw}
finally{[InstallerWindowTrace]::Stop()}
