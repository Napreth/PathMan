[CmdletBinding()]
param([string]$AppDirectory='C:\PathManTestApp',[string]$ResultPath='C:\PathManSettingsTest\gui-results.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Use only a disposable test VM.'}
$data=Join-Path $env:LOCALAPPDATA 'PathMan/environment.json'
if($PrepareFixture){
 Get-Process PathMan,OneDrive,SystemSettings -ErrorAction SilentlyContinue|Stop-Process -Force
 New-Item -ItemType Directory -Force -Path (Split-Path $data),(Split-Path $ResultPath)|Out-Null
 $state=Get-Content (Join-Path $PSScriptRoot 'groups.json') -Raw -Encoding UTF8|ConvertFrom-Json
 $state|Add-Member -NotePropertyName preferences -NotePropertyValue @{language='zh-CN';theme='system';autoCheckUpdates=$false} -Force
 $state|ConvertTo-Json -Depth 20|Set-Content $data -Encoding UTF8
 return
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class SettingsInput{[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);[DllImport("user32.dll")]public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);[DllImport("user32.dll")]public static extern IntPtr GetDC(IntPtr h);[DllImport("user32.dll")]public static extern int ReleaseDC(IntPtr h,IntPtr dc);[DllImport("gdi32.dll")]public static extern uint GetPixel(IntPtr dc,int x,int y);}'
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message);[IO.File]::WriteAllText($ResultPath+'.progress',$message)}
function WaitFor([scriptblock]$test,[string]$message){for($i=0;$i -lt 300;$i++){if(& $test){return};Start-Sleep -Milliseconds 100};throw $message}
function Find($root,[string]$name,$type=$null){if($null -eq $root){return $null};$condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name);if($type){$condition=[Windows.Automation.AndCondition]::new($condition,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$type))};$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)}
function Id([string]$id,$root=$window){if($null -ne $root){$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}}
function Key([byte]$key){[SettingsInput]::keybd_event($key,0,0,[UIntPtr]::Zero);[SettingsInput]::keybd_event($key,0,2,[UIntPtr]::Zero);Start-Sleep -Milliseconds 100}
function Click($element){WaitFor {$null -ne $element -and $element.Current.IsEnabled -and $element.Current.BoundingRectangle.Width -gt 0} 'Control is not ready';[void][SettingsInput]::SetForegroundWindow($app.MainWindowHandle);$rect=$element.Current.BoundingRectangle;[void][SettingsInput]::SetCursorPos([int]($rect.Left+$rect.Width/2),[int]($rect.Top+$rect.Height/2));[SettingsInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[SettingsInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 200}
function Go([string]$name){$row=Find (Id 'Navigation') $name ([Windows.Automation.ControlType]::ListItem);$row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 250}
function Choice([string]$id,[int]$index){Click (Id $id);Key 36;for($i=0;$i -lt $index;$i++){Key 40};Key 13;Start-Sleep -Milliseconds 300}
function State{Get-Content $data -Raw|ConvertFrom-Json}
function Brightness{$dc=[SettingsInput]::GetDC([IntPtr]::Zero);try{$rect=$window.Current.BoundingRectangle;$pixel=[SettingsInput]::GetPixel($dc,[int]($rect.Left+330),[int]($rect.Top+50));(($pixel -band 255)+(($pixel -shr 8) -band 255)+(($pixel -shr 16) -band 255))/3}finally{[void][SettingsInput]::ReleaseDC([IntPtr]::Zero,$dc)}}
function StartApp{
 $script:app=Start-Process (Join-Path $AppDirectory 'PathMan.exe') -PassThru
 WaitFor {$app.Refresh();$app.MainWindowHandle -ne [IntPtr]::Zero} 'Main window did not open'
 $script:window=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 [void][SettingsInput]::SetForegroundWindow($app.MainWindowHandle)
}
try{
 StartApp
 $info=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $AppDirectory 'PathMan.exe'))
 $expected=$info.ProductVersion.Split('+')[0]+' ('+([version]$info.FileVersion).ToString(3)+')'
 Assert ((Id 'AppVersion').Current.Name -eq $expected) 'Sidebar version is read from assembly metadata'
 $nav=(Id 'Navigation').FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)
 Assert ($nav.Count -eq 7 -and $nav[$nav.Count-2].Current.Name -eq '设置' -and $nav[$nav.Count-1].Current.Name -eq '关于') 'Settings is immediately above About'
 Go '设置';WaitFor {$null -ne (Id 'LanguageChoice')} 'Settings page missing'
 Assert ($null -ne (Id 'ThemeChoice') -and $null -ne (Id 'AutoCheckUpdates') -and $null -ne (Id 'AdvancedSettingsLink')) 'Language, theme, automatic updates and advanced settings share the Settings page'
 Choice 'LanguageChoice' 2
 WaitFor {(State).preferences.language -eq 'en-US' -and $null -ne (Find (Id 'Navigation') 'Settings')} 'English language did not apply and persist'
 Go 'Variables';WaitFor {$null -ne (Find $window 'User variables')} 'Variable page did not translate'
 Assert ($null -ne (Find $window 'Other variables')) 'Default group labels translate without rewriting user configuration'
 Go 'Settings';Choice 'ThemeChoice' 2
 WaitFor {(State).preferences.theme -eq 'dark' -and (Brightness) -lt 100} 'Explicit dark theme did not update Mica'
 Assert ((Brightness) -lt 100) 'Dark theme updates the native backdrop'
 Choice 'ThemeChoice' 1
 WaitFor {(State).preferences.theme -eq 'light' -and (Brightness) -gt 160} 'Explicit light theme did not update Mica'
 Assert ((Brightness) -gt 160) 'Light theme updates the native backdrop'
 Choice 'ThemeChoice' 2
 Click (Id 'AutoCheckUpdates');WaitFor {(State).preferences.autoCheckUpdates} 'Automatic update option was not saved'
 Click (Id 'AutoCheckUpdates');WaitFor {!((State).preferences.autoCheckUpdates)} 'Automatic update option did not disable'
 Assert (!((State).preferences.autoCheckUpdates)) 'Automatic update preference can be enabled and disabled'
 Click (Id 'ReceivePrereleaseUpdates');WaitFor {(State).preferences.receivePrereleaseUpdates} 'Prerelease update preference was not saved'
 Click (Id 'ReceivePrereleaseUpdates');WaitFor {!((State).preferences.receivePrereleaseUpdates)} 'Prerelease update preference did not disable'
 Click (Id 'AdvancedSettingsLink');WaitFor {$null -ne (Id 'AllowBuiltInVersionTargets')} 'Advanced page missing'
 Assert ($null -ne (Find $window 'Allow built-in system variables')) 'Advanced settings are localized and moved into Settings'
 Go 'About';WaitFor {$null -ne (Id 'SourceCodeLink')} 'About page missing'
 Assert ((Id 'VersionText').Current.Name -eq $expected) 'About and sidebar display the same assembly version'
 Assert ($null -ne (Find $window 'Windows environment variable and program alias manager')) 'About subtitle is localized'
 Assert ((Id 'SourceCodeLink').Current.HelpText -eq 'Napreth/PathMan' -and $null -ne (Id 'CheckUpdatesButton') -and (Id 'LicenseLink').Current.HelpText -eq 'MIT license') 'About displays repository, updates and MIT license rows'
 Assert ($null -eq (Id 'AdvancedSettingsLink')) 'About no longer contains advanced settings'
 Click (Id 'LicenseLink');WaitFor {$null -ne (Find ([Windows.Automation.AutomationElement]::RootElement) 'MIT license' ([Windows.Automation.ControlType]::Window))} 'License dialog missing'
 $dialog=Find ([Windows.Automation.AutomationElement]::RootElement) 'MIT license' ([Windows.Automation.ControlType]::Window)
 $license=(Id 'LicenseOriginalText' $dialog).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current
 Assert ($license.IsReadOnly -and $license.Value -ceq [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LICENSE'))) 'License dialog displays the exact original MIT text read-only'
 Key 27
 Click (Id 'CheckUpdatesButton');WaitFor {(Id 'CheckUpdatesButton').Current.IsEnabled -and (Id 'UpdateStatus').Current.Name.Length -gt 0} 'Manual release check did not finish'
 Assert ((Id 'UpdateStatus').Current.Name -notmatch '[\u4e00-\u9fff]') 'Manual release checking reports its result in the selected language'
 $app.Kill();$app.WaitForExit();StartApp
 WaitFor {$null -ne (Find (Id 'Navigation') 'Settings')} 'Saved language did not survive restart'
 Assert ((State).preferences.theme -eq 'dark' -and (Brightness) -lt 100) 'Saved language and explicit theme survive application restart'
 Go 'Settings';Choice 'LanguageChoice' 1
 WaitFor {$null -ne (Find (Id 'Navigation') '设置')} 'Chinese language did not apply'
 Assert ((State).preferences.language -eq 'zh-CN') 'Chinese can be restored without restarting'
 @{Passed=$true;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
}catch{@{Passed=$false;Error=$_.Exception.Message;Trace=$_.ScriptStackTrace;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8;throw}
