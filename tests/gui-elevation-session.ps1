[CmdletBinding()]
param([string]$ResultPath='C:\PathManFeaturesTest\elevation.json',[switch]$Elevated)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Run only in the disposable VM and restore its initial checkpoint.'}
$common=(Get-Content C:\PathManFeaturesTest\gui-health-actions.ps1 -Raw).Replace("`r`n","`n")
$start=$common.IndexOf('Add-Type -AssemblyName');$end=$common.IndexOf("try{`n StartApp")
if($start -lt 0 -or $end -le $start){throw 'UI automation helper section not found'}
. ([scriptblock]::Create($common.Substring($start,$end-$start)))
function MachineValue {
 $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment')
 try{$key.GetValue('MACHINE_EDIT_A')}finally{$key.Dispose()}
}
function Workers {@(Get-Process PathMan -ErrorAction SilentlyContinue|Where-Object Id -ne $window.Current.ProcessId)}
try {
 if($Elevated){
  function Window {Find ([Windows.Automation.AutomationElement]::RootElement) 'PathMan · 以管理员身份运行' ([Windows.Automation.ControlType]::Window)}
  $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
  try{Assert ([Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) 'The elevated title test runs with a real administrator token'}finally{$identity.Dispose()}
 }
 StartApp
 SelectNode (Find (Id 'SystemVariablesTree') 'MACHINE_EDIT_A' ([Windows.Automation.ControlType]::TreeItem))
 $before=MachineValue
 $draft='elevation-regression-'+[guid]::NewGuid().ToString('N')
 (Id 'ScalarValueBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($draft)
 if(!$Elevated){'WAITING_FOR_CANCEL_UAC'|Set-Content ($ResultPath+'.progress')}
 Invoke (Id 'SaveButton')
 if($Elevated){
  WaitFor {(MachineValue) -eq $draft -and !(Id 'SaveButton').Current.IsEnabled} 'The elevated desktop could not save directly'
  Assert ($window.Current.Name -eq 'PathMan · 以管理员身份运行' -and (Workers).Count -eq 0) 'An elevated desktop reports its identity and writes without launching another worker'
 } else {
  WaitFor {(Id 'StatusText').Current.Name -like '*已取消管理员操作。'} 'Cancel the system-write UAC prompt in the VM'
  Assert ((MachineValue) -eq $before) 'Cancelling UAC leaves the system registry unchanged'
  Assert ((Id 'ScalarValueBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $draft -and (Id 'SaveButton').Current.IsEnabled) 'Cancelling UAC preserves the draft for another save'
  Assert ((Workers).Count -eq 0) 'Cancelling UAC leaves no privileged worker'
  Invoke (Id 'RevertButton');WaitFor {!(Id 'SaveButton').Current.IsEnabled} 'Reverting the cancelled edit failed'
 }
 $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
 @{Passed=$true;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8
}catch{@{Passed=$false;Error=$_.Exception.Message;Stack=$_.ScriptStackTrace;Checks=$checks}|ConvertTo-Json -Depth 5|Set-Content $ResultPath -Encoding UTF8;throw}
