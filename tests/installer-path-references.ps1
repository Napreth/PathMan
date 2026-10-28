[CmdletBinding()]
param([string]$ResultPath='C:\PathManLiveTest\installer-path-results.json')
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable VM only.'}
Add-Type 'using System;using System.Runtime.InteropServices;public static class SetupPathProbe{[DllImport("C:\\PathManLiveTest\\PathMan.SetupActions.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.StdCall)]public static extern uint ConfigureInstall(string path,[MarshalAs(UnmanagedType.Bool)]bool machine);}'
$user=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
$tracking=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\PathMan',$true)
$saved=@{}
foreach($name in @('Path','SOFTWARE_PATH','INSTALLER_TEST_NESTED_PATH')){
 $saved[$name]=@{value=$user.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);kind=if($user.GetValueNames() -contains $name){$user.GetValueKind($name)}else{$null}}
}
$owned=$tracking.GetValue('BinPathOwned',$null)
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$text){if(!$value){throw $text};$checks.Add($text)}
function PathValue{$user.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
try{
 $bin='C:\PathManTestApp\bin'
 $tracking.SetValue('BinPathOwned',0,[Microsoft.Win32.RegistryValueKind]::DWord)
 $user.SetValue('SOFTWARE_PATH',$bin+';C:\other',[Microsoft.Win32.RegistryValueKind]::String)
 $user.SetValue('Path','C:\Windows;%SOFTWARE_PATH%',[Microsoft.Win32.RegistryValueKind]::ExpandString)
 Assert (([SetupPathProbe]::ConfigureInstall('C:\PathManTestApp',$false)) -eq 0 -and (PathValue) -eq 'C:\Windows;%SOFTWARE_PATH%') 'A CLI directory already covered by SOFTWARE_PATH is not appended directly'
 $user.SetValue('INSTALLER_TEST_NESTED_PATH',$bin,[Microsoft.Win32.RegistryValueKind]::String)
 $user.SetValue('SOFTWARE_PATH','%INSTALLER_TEST_NESTED_PATH%;C:\other',[Microsoft.Win32.RegistryValueKind]::ExpandString)
 Assert (([SetupPathProbe]::ConfigureInstall('C:\PathManTestApp',$false)) -eq 0 -and (PathValue) -eq 'C:\Windows;%SOFTWARE_PATH%') 'Nested saved references are expanded before deciding whether the CLI is missing'
 $tracking.SetValue('BinPathOwned',1,[Microsoft.Win32.RegistryValueKind]::DWord)
 $user.SetValue('Path',$bin+';;%SOFTWARE_PATH%;C:\tail;',[Microsoft.Win32.RegistryValueKind]::ExpandString)
 Assert (([SetupPathProbe]::ConfigureInstall('C:\PathManTestApp',$false)) -eq 0 -and (PathValue) -eq $bin+';;%SOFTWARE_PATH%;C:\tail;') 'Upgrade leaves an existing owned CLI entry untouched even when a reference also covers it'
 $tracking.SetValue('BinPathOwned',0,[Microsoft.Win32.RegistryValueKind]::DWord)
 $value=$bin+';%SOFTWARE_PATH%'
 $user.SetValue('Path',$value,[Microsoft.Win32.RegistryValueKind]::ExpandString)
 Assert (([SetupPathProbe]::ConfigureInstall('C:\PathManTestApp',$false)) -eq 0 -and (PathValue) -eq $value) 'Repair does not delete a preexisting direct entry the installer did not own'
 $value=((0..1200|ForEach-Object {'C:\long-path-'+$_}) -join ';')+';%SOFTWARE_PATH%'
 $user.SetValue('Path',$value,[Microsoft.Win32.RegistryValueKind]::ExpandString)
 Assert (([SetupPathProbe]::ConfigureInstall('C:\PathManTestApp',$false)) -eq 0 -and (PathValue) -eq $value) 'Reference detection preserves long PATH values without NSIS string truncation'
 @{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json|Set-Content $ResultPath -Encoding UTF8
}catch{
 @{passed=$false;error=$_.Exception.Message;checks=$checks.ToArray()}|ConvertTo-Json|Set-Content $ResultPath -Encoding UTF8
 throw
}finally{
 foreach($name in $saved.Keys){if($null -eq $saved[$name].value){$user.DeleteValue($name,$false)}else{$user.SetValue($name,$saved[$name].value,$saved[$name].kind)}}
 if($null -eq $owned){$tracking.DeleteValue('BinPathOwned',$false)}else{$tracking.SetValue('BinPathOwned',$owned,[Microsoft.Win32.RegistryValueKind]::DWord)}
 $user.Dispose();$tracking.Dispose()
}
