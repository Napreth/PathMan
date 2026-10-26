[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer,[string]$InstallDirectory='C:\PathManTestApp',[string]$ResultPath='C:\PathManTest\wizard.json')
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Run only in the disposable Windows VM and restore its initial checkpoint.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type 'using System;using System.Runtime.InteropServices;public static class WizardIcons{[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);[DllImport("user32.dll")]public static extern IntPtr GetDlgItem(IntPtr h,int id);[DllImport("user32.dll")]public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);[DllImport("shell32.dll",CharSet=CharSet.Unicode)]public static extern uint ExtractIconEx(string path,int index,IntPtr[] large,IntPtr[] small,uint count);[DllImport("user32.dll")]public static extern bool DestroyIcon(IntPtr icon);}'
. (Join-Path $PSScriptRoot 'shim-fixtures.ps1')
$checks=[Collections.Generic.List[string]]::new()
function Assert($condition,[string]$message){if(!$condition){throw $message};$checks.Add($message)}
function WaitFor([scriptblock]$check,[string]$message,[int]$seconds=60){$limit=[DateTime]::UtcNow.AddSeconds($seconds);do{if(& $check){return};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $limit);throw $message}
function Element([string]$id){$handle=[WizardIcons]::GetDlgItem([IntPtr]$script:handle,[int]$id);if($handle -ne [IntPtr]::Zero){[Windows.Automation.AutomationElement]::FromHandle($handle)}}
function Names { @($script:window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|ForEach-Object {$_.Current.Name}) -join "`n" }
function Next {
 $button=Element '1';if(!$button -or !$button.Current.IsEnabled){throw 'The wizard Next button is unavailable'}
 $before=Names
 $point=$button.GetClickablePoint();$null=[WizardIcons]::SetCursorPos([int]$point.X,[int]$point.Y)
 [WizardIcons]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[WizardIcons]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 WaitFor {$script:setup.HasExited -or (Names) -ne $before} 'The wizard did not advance after clicking Next' 20
 Start-Sleep -Milliseconds 600
}
function OpenWizard {
 $script:setup=Start-Process $Installer -ArgumentList "/CurrentUser /LANG=2052 /D=$InstallDirectory" -PassThru
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:setup.Id)
 WaitFor {$script:window=@([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)|Where-Object {$_.Current.Name.Trim() -eq 'PathMan 安装'})|Select-Object -First 1;$null -ne $script:window} 'Setup did not open'
 $script:handle=$script:window.Current.NativeWindowHandle
}
function OneWindow {
 $top=@([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:setup.Id))|Where-Object {!$_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0})
 Assert ($top.Count -eq 1 -and $top[0].Current.NativeWindowHandle -eq $script:handle) 'The installer keeps one wizard window without a maintenance confirmation dialog'
}
function FinishWizard {
 WaitFor {(Element '1').Current.Name -match '完成'} 'Setup did not reach its finish page'
 OneWindow
 $buttons=$script:window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::CheckBox))
 foreach($button in $buttons){$pattern=[Windows.Automation.TogglePattern]$button.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern);$wanted=$button.Current.Name -match '桌面';if(($pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) -ne $wanted){$pattern.Toggle()}}
 Next
 Assert ($script:setup.WaitForExit(20000) -and $script:setup.ExitCode -eq 0) 'Interactive maintenance completes successfully'
}
try {
 $gui=Start-Process (Join-Path $InstallDirectory 'PathMan.exe') -PassThru
 WaitFor {$gui.Refresh();$gui.MainWindowHandle -ne 0} 'PathMan did not open'
 $settings=Join-Path $env:LOCALAPPDATA 'PathMan\environment.json'
 $state=Read-TestWorkspace $settings
 $state|Add-Member -NotePropertyName monitoringEnabled -NotePropertyValue $true -Force
 $state|Add-Member -NotePropertyName trackChangeOrigins -NotePropertyValue $false -Force
 $state|ConvertTo-Json -Depth 40|Set-Content $settings -Encoding UTF8
 $service=Join-Path $InstallDirectory 'internal\PathMan.Service.exe'
 $run=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run');try{$run.SetValue('PathMan',('"'+$service+'"'))}finally{$run.Dispose()}
 $background=Start-Process $service -WindowStyle Hidden -PassThru
 WaitFor {try{$event=[Threading.EventWaitHandle]::OpenExisting((Test-BackgroundEventName $InstallDirectory 'Ready'));try{$event.WaitOne(0)}finally{$event.Dispose()}}catch [Threading.WaitHandleCannotBeOpenedException]{$false}} 'Background was not ready'
 $registration=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan')
 try{$registration.SetValue('DisplayVersion','0.0.1')}finally{$registration.Dispose()}
 OpenWizard
 Next;Next
 WaitFor {(Names).Contains('关闭正在运行的程序')} 'The running-applications page was not displayed inside the wizard'
 OneWindow
 Assert (!$gui.HasExited -and !$background.HasExited) 'The installer waits for Continue before terminating the GUI and background'
 [IO.File]::WriteAllText($ResultPath+'.stage','close-apps')
 WaitFor {Test-Path ($ResultPath+'.continue')} 'Timed out waiting for screenshot inspection' 120
 Next
 Assert ($gui.WaitForExit(10000) -and $background.WaitForExit(10000)) 'Continue terminates the GUI and background without unsaved-edit dialogs'
 FinishWizard
 $link=Join-Path ([Environment]::GetFolderPath('Desktop')) 'PathMan.lnk'
 $shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut($link)
 Assert ((Test-Path $link) -and $shortcut.IconLocation -match 'PathMan.exe,0') 'The desktop shortcut explicitly uses the application icon'
 foreach($file in @($Installer,(Join-Path $InstallDirectory 'PathMan.exe'),(Join-Path $InstallDirectory 'internal\Uninstall.exe'))){$large=[IntPtr[]]::new(1);$small=[IntPtr[]]::new(1);$count=[WizardIcons]::ExtractIconEx($file,0,$large,$small,1);try{Assert ($count -gt 0 -and $large[0] -ne [IntPtr]::Zero -and $small[0] -ne [IntPtr]::Zero) ('Large and small icons are embedded in '+[IO.Path]::GetFileName($file))}finally{if($large[0] -ne [IntPtr]::Zero){$null=[WizardIcons]::DestroyIcon($large[0])};if($small[0] -ne [IntPtr]::Zero){$null=[WizardIcons]::DestroyIcon($small[0])}}}
 $gui=Start-Process (Join-Path $InstallDirectory 'PathMan.exe') -PassThru
 WaitFor {$gui.Refresh();$gui.MainWindowHandle -ne 0} 'PathMan did not reopen'
 $icon=[WizardIcons]::SendMessage($gui.MainWindowHandle,0x7f,[IntPtr]1,[IntPtr]::Zero)
 Assert ($icon -ne [IntPtr]::Zero) 'The main window supplies an application icon for the taskbar'
 [IO.File]::WriteAllText($ResultPath+'.stage','icons')
 WaitFor {Test-Path ($ResultPath+'.icons-continue')} 'Timed out waiting for icon inspection' 120
 $restarted=@(Get-Process PathMan.Service -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq $service})
 Assert ($restarted.Count -eq 1 -and $restarted[0].Id -ne $background.Id) 'Maintenance restarts the previously running background with its saved settings'
 $gui.Kill();$gui.WaitForExit();Stop-TestBackground $InstallDirectory;Start-Sleep -Milliseconds 500
 # Give a real process the installed CLI image name to exercise the wait page.
 # It runs only in this disposable VM and setup replaces it after it exits.
 $cli=Join-Path $InstallDirectory 'bin\pathman.exe'
 Copy-Item $env:ComSpec $cli -Force
 $command=Start-Process $cli -ArgumentList '/d /c "ping -n 21 127.0.0.1 >nul"' -WindowStyle Hidden -PassThru
 OpenWizard;Next;Next
 WaitFor {(Names).Contains('关闭正在运行的程序')} 'The CLI wait page did not appear'
 Next
 Assert (!$command.HasExited -and (Names).Contains('正在等待命令行任务结束')) 'CLI tasks remain alive while the wizard shows its waiting status'
 OneWindow
 FinishWizard
 Assert ($command.HasExited) 'Installation waits for the CLI task to finish before replacing files'
 $setup.Dispose();$setup=$null
 @{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
} catch {@{passed=$false;error=$_.Exception.Message;trace=$_.ScriptStackTrace;checks=$checks.ToArray()}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8;throw}
finally {Stop-TestBackground $InstallDirectory;foreach($process in @($setup,$gui,$background,$command)){if($process){if(!$process.HasExited){$process.Kill()};$process.Dispose()}}}
