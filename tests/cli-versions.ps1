[CmdletBinding()]
param([string]$AppDirectory='C:\PathMan_测试目录',[string]$ResultPath='C:\PathManCliTest\results.json',[switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable VM only; restore the initial checkpoint afterwards.'}
$data=Join-Path $env:LOCALAPPDATA 'PathMan';$cli=Join-Path $AppDirectory 'bin\pathman.exe'
if($PrepareFixture){
 [IO.File]::Delete((Join-Path $data 'environment.json'))
 $user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment');try{$user.SetValue('PATHMAN_CLI_USER','user-before')}finally{$user.Dispose()}
 $machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true);try{$machine.SetValue('PATHMAN_CLI_MACHINE','machine-before')}finally{$machine.Dispose()}
 return
}
. (Join-Path $PSScriptRoot 'shim-fixtures.ps1')
$checks=[Collections.Generic.List[string]]::new();$background=$null;$gui=$null
function Assert($condition,[string]$message){if(!$condition){throw $message};$checks.Add($message);[IO.File]::WriteAllText($ResultPath+'.progress',$message)}
function WaitFor([scriptblock]$condition,[string]$message,[int]$Seconds=60){$timer=[Diagnostics.Stopwatch]::StartNew();while($timer.Elapsed.TotalSeconds -lt $Seconds){if(& $condition){return};Start-Sleep -Milliseconds 100};throw $message}
function Raw([string]$name,[switch]$Machine){$key=if($Machine){[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment')}else{[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')};try{$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}}
function RunCli([string[]]$arguments,[int]$expected=0){
 $start=[Diagnostics.ProcessStartInfo]::new($cli);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.StandardOutputEncoding=[Text.Encoding]::UTF8;$start.StandardErrorEncoding=[Text.Encoding]::UTF8
 $start.Arguments=($arguments|ForEach-Object{if($_.Contains('"') -or $_.EndsWith('\')){throw 'Fixture arguments must not contain quotes or trailing backslashes'};'"'+$_+'"'}) -join ' '
 $process=[Diagnostics.Process]::Start($start)
 try{$out=$process.StandardOutput.ReadToEndAsync();$err=$process.StandardError.ReadToEndAsync();if(!$process.WaitForExit(10000)){$process.Kill();throw 'CLI timed out or requested interactive elevation'};$text=$out.Result;$errorText=$err.Result;if($process.ExitCode -ne $expected){throw "Unexpected CLI exit $($process.ExitCode): $($arguments -join ' ') $errorText"};$text}
 finally{$process.Dispose()}
}
function Settings{Get-Content (Join-Path $data 'environment.json') -Raw|ConvertFrom-Json}
function SaveSettings($state){$state|ConvertTo-Json -Depth 40|Set-Content (Join-Path $data 'environment.json') -Encoding UTF8}
function RunRegistration{[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')}
try{
 Assert (!([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) 'The CLI regression runs without administrator privileges'
 $help=RunCli @('--help');Assert ((@([regex]::Matches($help,'(?m)^\s+pathman (\w+)')|ForEach-Object{$_.Groups[1].Value}) -join ',') -eq 'list,add,switch,delete') 'Help contains only the four version commands'
 Assert (!$help.Contains('--data-root') -and !$help.Contains('首次 add') -and !$help.Contains('管理员授权') -and !$help.Contains('硬链接')) 'Help omits the removed explanatory paragraphs'
 foreach($removed in @(@('layout'),@('shim','list'),@('startup','enable'),@('background','start'),@('--data-root','C:/unused','list'),@('--environment-session','invalid'))){$null=RunCli $removed 2}
 Assert ($true) 'Legacy commands, data-root overrides and the old CLI elevation worker are rejected'
 $null=RunCli @('add','cli-user','25','D:/SDK/User25','--variable','PATHMAN_CLI_USER','--type','User');$null=RunCli @('add','cli-user','lts','D:/SDK/User25')
 Assert ((Raw 'PATHMAN_CLI_USER') -eq 'user-before') 'Registering versions only saves metadata'
 $null=RunCli @('switch','cli-user','25');$listing=RunCli @('list','cli-user')
 Assert ((Raw 'PATHMAN_CLI_USER') -eq 'D:/SDK/User25' -and $listing.Contains('(25, lts)') -and $listing.Contains('Type: User')) 'User switching and shared current-version labels still work'
 $null=RunCli @('delete','cli-user','25');$null=RunCli @('delete','cli-user')
 Assert ((Raw 'PATHMAN_CLI_USER') -eq 'D:/SDK/User25') 'Deleting a tag or project preserves the environment value'
 $null=RunCli @('add','cli-machine','25','D:/SDK/Machine25','--variable','PATHMAN_CLI_MACHINE','--type','Machine')
 $watch=[Diagnostics.Stopwatch]::StartNew();$null=RunCli @('switch','cli-machine','25') 1;$watch.Stop()
 Assert ($watch.Elapsed.TotalSeconds -lt 5 -and (Raw 'PATHMAN_CLI_MACHINE' -Machine) -eq 'machine-before') 'A non-administrator machine switch exits promptly without UAC or a registry write'
 [IO.File]::WriteAllText($ResultPath+'.stage','machine-admin')
 WaitFor {(Raw 'PATHMAN_CLI_MACHINE' -Machine) -eq 'D:/SDK/Machine25'} 'The externally elevated CLI switch did not complete' -Seconds 300
 Assert ((Raw 'PATHMAN_CLI_MACHINE' -Machine) -eq 'D:/SDK/Machine25') 'The same command works when its caller supplies administrator privileges'
 $null=RunCli @('delete','cli-machine')
 Assert ((Raw 'PATHMAN_CLI_MACHINE' -Machine) -eq 'D:/SDK/Machine25') 'A normal user can remove machine-version metadata without changing the machine value'
 Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class SetupMaintenance{[DllImport("C:\\PathManCliTest\\PathMan.SetupActions.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.StdCall)]public static extern uint StopApplications(string root);[DllImport("C:\\PathManCliTest\\PathMan.SetupActions.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.StdCall)]public static extern uint RefreshShims(string root,string localData);}'
 $state=Settings;$state.monitoringEnabled=$true;SaveSettings $state
 $service=Join-Path $AppDirectory 'internal\PathMan.Service.exe';$run=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run');try{$run.SetValue('PathMan',('"'+$service+'"'))}finally{$run.Dispose()}
 $background=Start-Process $service -PassThru -WindowStyle Hidden
 WaitFor {$ready=$null;try{$ready=[Threading.EventWaitHandle]::OpenExisting((Test-BackgroundEventName $AppDirectory 'Ready'));$ready.WaitOne(0)}catch [Threading.WaitHandleCannotBeOpenedException]{$false}finally{if($ready){$ready.Dispose()}}} 'The background fixture did not become ready'
 $code=[SetupMaintenance]::StopApplications($AppDirectory)
 Assert ($code -eq 0 -and $background.WaitForExit(10000)) 'Native installer maintenance stops the background directly for a Unicode installation path'
 $run=RunRegistration;try{$registration=$run.GetValue('PathMan')}finally{$run.Dispose()}
 Assert ((Settings).monitoringEnabled -and $registration -eq ('"'+$service+'"')) 'Installer stop preserves monitoring and login-startup preferences'
 $state=Settings;$state.monitoringEnabled=$false;SaveSettings $state
 $run=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run');try{$run.DeleteValue('PathMan',$false)}finally{$run.Dispose()}
 $gui=Start-Process (Join-Path $AppDirectory 'PathMan.exe') -PassThru
 WaitFor {$gui.Refresh();$gui.MainWindowHandle -ne 0} 'The GUI fixture did not open'
 Assert ($gui.MainWindowTitle.StartsWith('PathMan ·')) 'The GUI fixture includes its permission state in its title'
 $code=[SetupMaintenance]::StopApplications($AppDirectory)
 Assert ($code -eq 0 -and $gui.WaitForExit(10000)) 'Native installer maintenance closes the GUI with its permission-state title'
 $alias=New-TestShim $AppDirectory $data 'fixture-maintenance' $env:ComSpec
 $template=Join-Path $AppDirectory 'internal\PathMan.Shim.exe';$replacement=$template+'.replacement';Copy-Item $template $replacement;Move-Item $replacement $template -Force
 $beforePath=Raw 'Path';$code=[SetupMaintenance]::RefreshShims($AppDirectory,$env:LOCALAPPDATA)
 $links=@(& fsutil hardlink list $alias)
 Assert ($code -eq 0 -and ($links -join "`n").Contains('internal\PathMan.Shim.exe')) 'Installer refresh updates aliases without calling the CLI'
 Assert (!(Settings).monitoringEnabled -and (Raw 'Path') -eq $beforePath) 'Installer refresh neither enables monitoring nor edits user PATH'
 Remove-TestShim $data 'fixture-maintenance'
 [IO.File]::WriteAllText($ResultPath+'.stage','complete');@{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8
}catch{@{passed=$false;error=$_.Exception.Message;trace=$_.ScriptStackTrace;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8;throw}
finally{Stop-TestBackground $AppDirectory;if($background){$background.Dispose()};if($gui){$gui.Dispose()}}
