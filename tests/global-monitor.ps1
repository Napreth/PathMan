[CmdletBinding()]
param([string]$AppDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish/win-x64/SelfContained'),[string]$ResultDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/global-monitor'))
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -and (Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable Windows VM or CI runner required.'}
$AppDirectory=[IO.Path]::GetFullPath($AppDirectory)
$service=Join-Path $AppDirectory 'internal/PathMan.Service.exe'
$marker=Join-Path $AppDirectory 'internal/install.ini'
$oldMarker=if(Test-Path $marker){[IO.File]::ReadAllBytes($marker)}else{$null}
$machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true)
$oldPath=$machine.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);$oldPathKind=$machine.GetValueKind('Path')
$oldExternal=$machine.GetValue('EXTERNAL_PATH',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$oldExternalKind=if($null -ne $oldExternal){$machine.GetValueKind('EXTERNAL_PATH')}else{$null}
$global=Join-Path $env:ProgramData 'PathMan/monitor'
$checks=[Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Force -Path $ResultDirectory|Out-Null
function Assert($condition,[string]$message){if(!$condition){throw $message};$checks.Add($message)}
function WaitFor([scriptblock]$test,[string]$message){for($i=0;$i -lt 300;$i++){if(& $test){return};Start-Sleep -Milliseconds 100};throw $message}
function Run([string]$arguments){$p=Start-Process $service -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait -RedirectStandardError (Join-Path $ResultDirectory 'service.stderr.log');return $p.ExitCode}
function State{Get-Content (Join-Path $global 'settings.json') -Raw|ConvertFrom-Json}
function Status{if(Test-Path (Join-Path $global 'status.json')){Get-Content (Join-Path $global 'status.json') -Raw|ConvertFrom-Json}}
try{
    '[PathMan]','Scope=CurrentUser'|Set-Content $marker
    Assert ((Run '--configure-global-monitor true true true') -ne 0) 'Per-user installations reject global service configuration'
    '[PathMan]','Scope=AllUsers'|Set-Content $marker
    $machine.SetValue('Path','C:\GlobalBase',[Microsoft.Win32.RegistryValueKind]::String);$machine.DeleteValue('EXTERNAL_PATH',$false)
    Assert ((Run '--configure-global-monitor true true false') -eq 0) 'An all-users installation enables the unified SCM service'
    WaitFor {(Get-Service PathMan.Monitor).Status -eq 'Running' -and (Status).OriginsReady} 'Unified service or ETW collector did not become ready'
    Assert ((State).enabled -and (State).provideNonAdministratorOrigins -and !(State).autoArrangeMachinePath) 'Global switches persist independently in ProgramData'
    Assert ((Run '--data-root "C:\PathMan-Global-NoBackground"') -eq 0) 'Personal background launches exit while the unified service owns monitoring'
    $svc=Get-CimInstance Win32_Service -Filter "Name='PathMan.Monitor'"
    Assert ($svc.StartName -eq 'LocalSystem' -and $svc.StartMode -eq 'Auto') 'Unified monitoring is an automatic machine service'
    $children=@(Get-CimInstance Win32_Process|Where-Object {$_.ParentProcessId -eq $svc.ProcessId -and $_.Name -eq 'PathMan.Service.exe'})
    Assert ($children.Count -eq 0) 'The unified service collects origins in its own process without worker programs'
    Assert ((Run '--configure-global-monitor true true true') -eq 0) 'Machine PATH organization can be enabled independently'
    Start-Sleep -Seconds 2
    $machine.SetValue('Path','C:\GlobalBase;D:\GlobalNew;D:\GlobalNew',[Microsoft.Win32.RegistryValueKind]::String)
    WaitFor {$machine.GetValue('EXTERNAL_PATH') -eq 'D:\GlobalNew'} 'The service did not organize the system PATH'
    Assert ($machine.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -eq 'C:\GlobalBase;%EXTERNAL_PATH%') 'Exactly one global writer deduplicates and moves new machine PATH entries'
    Assert (@(Get-ChildItem (Join-Path $global 'machine/backups') -Filter '*.json').Count -gt 0) 'Global machine changes have protected original-value backups'
    Assert ((Run '--configure-global-monitor false true true') -eq 0) 'The unified service can be disabled'
    WaitFor {(Get-Service PathMan.Monitor).Status -eq 'Stopped'} 'Unified service did not stop'
    Assert (!(State).enabled -and (State).autoArrangeMachinePath -and (State).provideNonAdministratorOrigins) 'Disabling unified monitoring retains its saved options'
    Assert ((Run '--configure-global-monitor true false false') -eq 0) 'The unified service can restart without recreating the installation'
    WaitFor {(Status).Running -and (Status).OriginsReady} 'Unified service did not restart'
    Assert ((Run '--remove-origin-tasks') -eq 0) 'Uninstall maintenance removes the SCM service and privileged collectors'
    Assert ($null -eq (Get-Service PathMan.Monitor -ErrorAction SilentlyContinue)) 'No SCM service remains after uninstall maintenance'
    @{Passed=$true;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $ResultDirectory 'results.json') -Encoding UTF8
    Write-Host 'PASS: installation scope, SCM lifecycle, single-process origins, global PATH writes/backups, option persistence, restart and uninstall.'
}catch{@{Passed=$false;Error=$_.Exception.Message;Trace=$_.ScriptStackTrace;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $ResultDirectory 'results.json') -Encoding UTF8;throw}
finally{
    $null=Run '--remove-origin-tasks'
    $machine.SetValue('Path',$oldPath,$oldPathKind)
    if($null -eq $oldExternal){$machine.DeleteValue('EXTERNAL_PATH',$false)}else{$machine.SetValue('EXTERNAL_PATH',$oldExternal,$oldExternalKind)};$machine.Dispose()
    if($null -ne $oldMarker){[IO.File]::WriteAllBytes($marker,$oldMarker)}elseif(Test-Path $marker){Remove-Item -LiteralPath $marker -Force}
}
