[CmdletBinding()]
param([string]$AppDirectory='C:\PathManUpdateTestApp',[string]$ResultPath='C:\PathManUpdateTest\gui-results.json',[string]$ExpectedGuiHash)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Use only the disposable test VM.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type 'using System;using System.Runtime.InteropServices;public static class UpdateInput{[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr xtra);[DllImport("user32.dll")]public static extern void keybd_event(byte k,byte s,uint f,UIntPtr xtra);}'
$data=Join-Path $env:LOCALAPPDATA 'PathMan\update-test'
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message);$message|Set-Content ($ResultPath+'.progress')}
function WaitFor([scriptblock]$test,[string]$message,[int]$seconds=60){$end=[DateTime]::UtcNow.AddSeconds($seconds);do{if(& $test){return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $end);throw $message}
function Id([string]$id,$root=$window){if($null -ne $root){$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}}
function Key([byte]$key){[UpdateInput]::keybd_event($key,0,0,[UIntPtr]::Zero);[UpdateInput]::keybd_event($key,0,2,[UIntPtr]::Zero);Start-Sleep -Milliseconds 200}
function Click($element){$pattern=$null;if($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)){$pattern.Invoke()}elseif($element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)){$pattern.Toggle()}else{$point=$element.GetClickablePoint();[void][UpdateInput]::SetCursorPos([int]$point.X,[int]$point.Y);[UpdateInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[UpdateInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)};Start-Sleep -Milliseconds 200}
function CancelDialog{$cancel=$dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'取消'));Click $cancel;WaitFor {!(Dialog)} 'Release details did not close'}
function Go([string]$name){[void][UpdateInput]::SetForegroundWindow($app.MainWindowHandle);$row=(Id 'Navigation').FindFirst([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name));$row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 300}
function Cache{Get-Content (Join-Path $data 'updates.json') -Raw -Encoding UTF8|ConvertFrom-Json}
function Dialog{$script:dialog=@([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id))|Where-Object {$null -ne (Id 'ReleaseTitle' $_)})|Select-Object -First 1;$null -ne $script:dialog}
function Screenshot([string]$name,$target=$window){$rect=$target.Current.BoundingRectangle;$bitmap=[Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height);$graphics=[Drawing.Graphics]::FromImage($bitmap);try{$graphics.CopyFromScreen([int]$rect.Left,[int]$rect.Top,0,0,$bitmap.Size);$bitmap.Save($ResultPath+'.'+$name+'.png')}finally{$graphics.Dispose();$bitmap.Dispose()}}
function StartApp([switch]$FromToast){if($FromToast){Start-Process ('pathman-update-test://release/'+$first)|Out-Null;WaitFor {$script:app=@(Get-Process PathMan -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq (Join-Path $AppDirectory 'PathMan.exe')})|Select-Object -First 1;$null -ne $script:app} 'Toast did not start PathMan'}else{$script:app=Start-Process (Join-Path $AppDirectory 'PathMan.exe') -PassThru};WaitFor {$app.Refresh();$app.MainWindowHandle -ne [IntPtr]::Zero} 'Main window did not open';$script:window=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle);[void][UpdateInput]::SetForegroundWindow($app.MainWindowHandle)}
try{
 StartApp
 WaitFor {Test-Path (Join-Path $data 'updates.json')} 'Startup update check did not persist its result'
 WaitFor {(Cache).notifiedVersions.Count -gt 0 -and (Get-Content (Join-Path $data 'update-test-toast.txt') -Raw -ErrorAction SilentlyContinue) -like 'Shown*'} 'Automatic update check did not deliver a Toast'
 $first=(Cache).latest.tag
 Assert ($first -match '^v99\.0\.\d+$') 'The dedicated test build detects a generated update at startup'
 Assert ((Cache).notifiedVersions.Count -eq 1) 'The automatic result is recorded as notified once'
 Go '关于';WaitFor {$null -ne (Id 'ReleaseLink') -and !(Id 'ReleaseLink').Current.IsOffscreen} 'View new version button is missing'
 Assert ((Id 'UpdateStatus').Current.Name.Contains($first) -and (Id 'CheckUpdatesButton').Current.ControlType -eq [Windows.Automation.ControlType]::Button) 'About displays the cached result as a subtitle with explicit buttons'
 Screenshot 'about'
 Start-Process ('pathman-update-test://release/'+$first)|Out-Null
 WaitFor {Dialog} 'Toast protocol did not open release details'
 $instances=@(Get-Process PathMan -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq (Join-Path $AppDirectory 'PathMan.exe')})
 Assert ($instances.Count -eq 1) 'Toast activation reuses the existing application window'
 Assert ((Id 'ReleaseTitle' $dialog).Current.Name -eq (Cache).latest.title) 'Toast activation navigates to About and opens the release title'
 $body=(Id 'ReleaseBody' $dialog).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current
 Assert ($body.IsReadOnly -and $body.Value -ceq (Cache).latest.body) 'Release notes show the complete original body read-only'
 Assert ($null -eq (Id 'IgnoreUpdateButton' $dialog) -and $null -ne (Id 'InstallUpdateButton' $dialog)) 'The update dialog has Cancel and Install with no Ignore action'
 Screenshot 'details' $dialog
 CancelDialog
 Go '设置';Click (Id 'AutoCheckUpdates')
 $app.CloseMainWindow()|Out-Null;Assert ($app.WaitForExit(15000)) 'The test application closes normally'
 StartApp -FromToast;WaitFor {Dialog} 'Toast activation after application exit did not open release details';CancelDialog
 Go '关于'
 Assert ((Cache).latest.tag -eq $first -and (Id 'UpdateStatus').Current.Name.Contains($first) -and !(Id 'ReleaseLink').Current.IsOffscreen) 'The previous result is available after restart without a new check'
 Click (Id 'CheckUpdatesButton');WaitFor {(Cache).latest.tag -ne $first -and (Id 'CheckUpdatesButton').Current.IsEnabled} 'Manual detection did not generate the next version'
 Assert ((Cache).notifiedVersions.Count -eq 1) 'Manual detection does not send an automatic update Toast'
 Click (Id 'ReleaseLink');WaitFor {Dialog} 'Release dialog missing after manual check'
 $fixture=Join-Path $data 'update-test-installer.exe'
 $stream=[IO.File]::Open($fixture,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try{$original=$stream.ReadByte();$stream.Position=0;$stream.WriteByte($original -bxor 1)}finally{$stream.Dispose()}
 Click (Id 'InstallUpdateButton' $dialog)
 WaitFor {$status=Id 'UpdateDownloadStatus' $dialog;$null -ne $status -and $status.Current.Name.Contains('校验失败')} 'The corrupted installer was not rejected'
 Assert (!$app.HasExited -and (Id 'InstallUpdateButton' $dialog).Current.IsEnabled) 'A damaged installer is never launched and the application stays open'
 Screenshot 'integrity-error' $dialog
 $stream=[IO.File]::Open($fixture,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$stream.WriteByte($original)}finally{$stream.Dispose()}
 Click (Id 'InstallUpdateButton' $dialog)
 Assert ($app.WaitForExit(60000)) 'Installing a verified update closes the old application'
 WaitFor {try{@(Get-Process PathMan -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq (Join-Path $AppDirectory 'PathMan.exe') -and $_.Id -ne $app.Id -and $_.MainWindowHandle -ne [IntPtr]::Zero}).Count -gt 0 -and ((Get-FileHash (Join-Path $AppDirectory 'PathMan.exe') -ErrorAction Stop).Hash -eq $ExpectedGuiHash)}catch{$false}} 'Automatic update did not install and reopen the release application' 120
 Assert $true 'Verified installation replaces the test binary with the normal release and reopens PathMan'
 Get-Process PathMan -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq (Join-Path $AppDirectory 'PathMan.exe')}|ForEach-Object {$_.CloseMainWindow()|Out-Null;$_.WaitForExit(10000)|Out-Null}
 New-Item -ItemType File (Join-Path $AppDirectory 'licenses\user-file.txt') -Force|Out-Null
 $uninstall=Start-Process (Join-Path $AppDirectory 'internal\Uninstall.exe') -ArgumentList '/S /CurrentUser' -PassThru;$uninstall.WaitForExit(30000)|Out-Null
 WaitFor {!(Test-Path (Join-Path $AppDirectory 'THIRD-PARTY-NOTICES.txt')) -and !(Test-Path (Join-Path $AppDirectory 'licenses\dotnet-THIRD-PARTY-NOTICES.txt'))} 'Uninstall left third-party notices'
 Assert (Test-Path (Join-Path $AppDirectory 'licenses\user-file.txt')) 'Uninstall removes shipped third-party licenses while preserving unrelated user files'
 Assert (!(Test-Path 'HKCU:\Software\Classes\pathman-update-test') -and !(Test-Path 'HKCU:\Software\Classes\AppUserModelId\PathMan.UpdateTest')) 'Uninstall removes this installation notification and protocol registrations'
 @{Passed=$true;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
}catch{try{Screenshot 'failure'}catch{};@{Passed=$false;Error=$_.Exception.Message;Trace=$_.ScriptStackTrace;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8;throw}
