[CmdletBinding()]
param([string]$Installer='C:\PathManMonitorTest\setup.exe',[string]$InstallDirectory='C:\Program Files\PathMan',[string]$ResultPath='C:\PathManMonitorTest\results.json')
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable VM only; restore the initial checkpoint after testing.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -Path (Join-Path $PSScriptRoot 'installer-focus-trace.cs')
Add-Type 'using System;using System.Runtime.InteropServices;public static class ResumeFocus{[DllImport("user32.dll")]public static extern IntPtr GetDlgItem(IntPtr h,int id);[DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);}'
. (Join-Path $PSScriptRoot 'shim-fixtures.ps1')
$checks=[Collections.Generic.List[string]]::new()
$data=Join-Path $env:LOCALAPPDATA 'PathMan\environment.json'
$service=Join-Path $InstallDirectory 'internal\PathMan.Service.exe'
function State {Get-Content $data -Raw -Encoding UTF8|ConvertFrom-Json}
function Stage([string]$text){[IO.File]::WriteAllText($ResultPath+'.stage',$text)}
function Assert($value,[string]$text){if(!$value){throw $text};$checks.Add($text)}
function WaitFor([scriptblock]$test,[string]$message,[int]$seconds=90){$limit=[DateTime]::UtcNow.AddSeconds($seconds);do{if(& $test){return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $limit);throw $message}
function Button { $h=[ResumeFocus]::GetDlgItem($script:handle,1);if($h -ne [IntPtr]::Zero){[Windows.Automation.AutomationElement]::FromHandle($h)} }
function Names {@($window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|ForEach-Object {$_.Current.Name}) -join "`n"}
function Next {
 $b=Button;$before=Names;[void][ResumeFocus]::SetForegroundWindow($script:handle)
 $point=$b.GetClickablePoint();[void][ResumeFocus]::SetCursorPos([int]$point.X,[int]$point.Y)
 [ResumeFocus]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[ResumeFocus]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 WaitFor {$setup.HasExited -or (Names) -ne $before} 'The wizard did not advance' 90
 Start-Sleep -Milliseconds 500
}
try {
 if(Test-Path $ResultPath){Remove-Item -LiteralPath $ResultPath -Force}
 Stop-TestBackground $InstallDirectory
 WaitFor {try{$ready=[Threading.EventWaitHandle]::OpenExisting((Test-BackgroundEventName $InstallDirectory 'Ready'));try{!$ready.WaitOne(0)}finally{$ready.Dispose()}}catch [Threading.WaitHandleCannotBeOpenedException]{$true}} 'Previous background did not stop'
 Start-Sleep -Milliseconds 750
 $state=Read-TestWorkspace $data
 foreach($pair in @{monitoringEnabled=$true;trackChangeOrigins=$true;originTrackingReady=$false;originTrackingMessage=$null;autoArrangeUserPath=$true;ignoreSystemEditorUserChanges=$true;originTrackingRequest=[guid]::NewGuid().ToString();preferences=@{language='zh-CN';theme='system';autoCheckUpdates=$false}}.GetEnumerator()){$state|Add-Member -NotePropertyName $pair.Key -NotePropertyValue $pair.Value -Force}
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
 try{$before=[string]$key.GetValue('Path','',[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);$beforeKind=$key.GetValueKind('Path');$after=$before+';C:\Program Files\dotnet\'}finally{$key.Dispose()}
 $now=[DateTime]::Now.AddMinutes(-3)
 foreach($pair in @{monitorMessage='未能确认本次 PATH 修改来源，已保留外部更改，未自动整理。';monitorMessageTime=$now.ToString('o');changes=@(@{time=$now.AddMinutes(2).ToString('o');scope='User';name='Path';action='set';before=$after;after=$before;source='PathMan';origins=@()},@{time=$now.ToString('o');scope='User';name='Path';action='set';before=$before;after=$after;source='外部程序';origins=@()})}.GetEnumerator()){$state|Add-Member -NotePropertyName $pair.Key -NotePropertyValue $pair.Value -Force}
 $state|ConvertTo-Json -Depth 40|Set-Content $data -Encoding UTF8
 $run=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run');try{$run.SetValue('PathMan',('"'+$service+'"'))}finally{$run.Dispose()}
 Stage 'initial-origin-uac'
 $background=Start-Process $service -WindowStyle Hidden -PassThru
 WaitFor {(State).originTrackingReady} 'Initial origin tracking did not become ready' 300
 Assert (!$background.HasExited) 'The initial ordinary background process runs with origin tracking enabled'
 Assert (@((State).changes|Where-Object {$_.source -eq '外部程序' -and $_.before -eq $before -and $_.after -eq $after}).Count -eq 1) 'The fixture retains the original change evidence before reinstall'
 Stage 'wizard'
 $setup=Start-Process $Installer -ArgumentList ('/AllUsers /LANG=2052 /D='+$InstallDirectory) -PassThru
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$setup.Id)
 WaitFor {$script:window=@([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)|Where-Object {!$_.Current.IsOffscreen})|Select-Object -First 1;$null -ne $script:window} 'Installer did not open'
 $script:handle=[IntPtr]$window.Current.NativeWindowHandle
 Next;Next
 WaitFor {(Button).Current.Name -match '继续'} 'The running-applications page did not appear'
 Stage 'installer-uac'
 [InstallerFocusTrace]::Start($ResultPath+'.focus.tsv')
 Next
 WaitFor {(Button).Current.Name -match '完成'} 'The installer did not reach its completion page'
 [InstallerFocusTrace]::Stop()
 Assert ([ResumeFocus]::GetForegroundWindow() -eq $script:handle) 'The original installer remains in the foreground after UAC and elevated maintenance'
 Assert ($background.HasExited) 'Maintenance terminated the original background before replacing files'
 Stage 'await-restarted-origins'
 WaitFor {(State).originTrackingReady} 'Restarted origin tracking requested another elevation or failed to attach' 35
 Assert $true 'Previously authorized setup resumes precise origin tracking without a second UAC'
 $current=State
 Assert ($null -eq $current.monitorMessage) 'Reinstall retires the old unknown-origin warning already resolved by an explicit PATH edit'
 Assert (@($current.changes|Where-Object {$_.name -eq 'Path' -and $_.source -eq '外部程序' -and $_.before -eq $before -and $_.after -eq $after}).Count -eq 1) 'The original unknown external change and exact values remain in the journal'
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
 try{Assert ($key.GetValue('Path','',[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -ceq $before -and $key.GetValueKind('Path') -eq $beforeKind) 'Reinstall leaves the original PATH value and registry type unchanged'}finally{$key.Dispose()}
 $top=@([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)|Where-Object {!$_.Current.IsOffscreen})
 Assert ($top.Count -eq 1) 'The installer retains one visible wizard window'
 foreach($box in $window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::CheckBox))){$toggle=$box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern);if($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On){$toggle.Toggle()}}
 Stage 'finish';Next
 Assert ($setup.WaitForExit(15000) -and $setup.ExitCode -eq 0) 'Reinstall completes successfully'
 @{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json -Depth 6|Set-Content $ResultPath -Encoding UTF8
 Stage 'passed'
} catch {
 @{passed=$false;error=$_.Exception.Message;checks=$checks.ToArray();state=(State)}|ConvertTo-Json -Depth 15|Set-Content $ResultPath -Encoding UTF8
 Stage 'failed';throw
} finally {[InstallerFocusTrace]::Stop()}
