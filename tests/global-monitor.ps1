[CmdletBinding()]
param([string]$AppDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish/win-x64/SelfContained'),[string]$ResultDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/global-monitor'),[string]$ProbeExecutable='')
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -and (Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable Windows VM or CI runner required.'}
$staging=$null
if($env:GITHUB_ACTIONS -eq 'true'){
    $staging=Join-Path $env:ProgramFiles ('PathMan-CI-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging|Out-Null
    Copy-Item -Path (Join-Path $AppDirectory '*') -Destination $staging -Recurse
    foreach($item in @((Get-Item $staging)) + @(Get-ChildItem $staging -Recurse)){
        $acl=if($item.PSIsContainer){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()}
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        $acl.SetAccessRuleProtection($true,$false)
        foreach($identity in 'S-1-5-18','S-1-5-32-544','S-1-5-11'){
            $rights=if($identity -eq 'S-1-5-11'){[Security.AccessControl.FileSystemRights]::ReadAndExecute}else{[Security.AccessControl.FileSystemRights]::FullControl}
            if($item.PSIsContainer){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($identity),$rights,([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))}
            else{$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($identity),$rights,[Security.AccessControl.AccessControlType]::Allow))}
        }
        Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
    $AppDirectory=$staging
}
$AppDirectory=[IO.Path]::GetFullPath($AppDirectory)
$service=Join-Path $AppDirectory 'internal/PathMan.Service.exe'
$marker=Join-Path $AppDirectory 'internal/install.ini'
$oldMarker=if(Test-Path $marker){[IO.File]::ReadAllBytes($marker)}else{$null}
$machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment',$true)
$oldPath=$machine.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);$oldPathKind=$machine.GetValueKind('Path')
$oldExternal=$machine.GetValue('EXTERNAL_PATH',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$oldExternalKind=if($null -ne $oldExternal){$machine.GetValueKind('EXTERNAL_PATH')}else{$null}
$customTarget='PATHMAN_TEST_GLOBAL_PATH'
$oldCustom=$machine.GetValue($customTarget,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$oldCustomKind=if($null -ne $oldCustom){$machine.GetValueKind($customTarget)}else{$null}
$global=Join-Path $env:ProgramData 'PathMan/monitor'
$checks=[Collections.Generic.List[string]]::new()
$recoverySnapshot=$null
New-Item -ItemType Directory -Force -Path $ResultDirectory|Out-Null
function Assert($condition,[string]$message){if(!$condition){throw $message};$checks.Add($message)}
function WaitFor([scriptblock]$test,[string]$message){for($i=0;$i -lt 300;$i++){if(& $test){return};Start-Sleep -Milliseconds 100};throw $message}
function Run([string]$arguments){$p=Start-Process $service -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait -RedirectStandardError (Join-Path $ResultDirectory 'service.stderr.log');return $p.ExitCode}
function ReadJson([string]$path){
    $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=[IO.StreamReader]::new($stream)
    try{$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
}
function State{ReadJson (Join-Path $global 'settings.json')}
function Status{if(Test-Path (Join-Path $global 'status.json')){ReadJson (Join-Path $global 'status.json')}}
function MachineState{ReadJson (Join-Path $global 'machine/environment.json')}
function Probe([string[]]$arguments){
    $start=[Diagnostics.ProcessStartInfo]::new($ProbeExecutable);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $arguments){if($argument.Contains('"')){throw 'Unsupported probe argument'}}
    $start.Arguments=($arguments|ForEach-Object {'"'+$_+'"'}) -join ' '
    $process=[Diagnostics.Process]::Start($start);$output=$process.StandardOutput.ReadToEnd();$errorText=$process.StandardError.ReadToEnd();$process.WaitForExit()
    return @{Exit=$process.ExitCode;Output=$output;Error=$errorText}
}
try{
    '[PathMan]','Scope=CurrentUser'|Set-Content $marker
    Assert ((Run '--configure-global-monitor true true true') -ne 0) 'Per-user installations reject global service configuration'
    '[PathMan]','Scope=AllUsers'|Set-Content $marker
    $originalAcl=Get-Acl -LiteralPath $AppDirectory
    try{
        $weakAcl=Get-Acl -LiteralPath $AppDirectory
        $weakAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::Write,[Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $AppDirectory -AclObject $weakAcl
        Assert ((Run '--configure-global-monitor true true false') -ne 0) 'Ordinary-user-writable installations cannot deploy a SYSTEM service'
        Assert ($null -eq (Get-Service PathMan.Monitor -ErrorAction SilentlyContinue)) 'Unsafe source rejection happens before SCM registration'
    }finally{Set-Acl -LiteralPath $AppDirectory -AclObject $originalAcl}
    $originalAcl=Get-Acl -LiteralPath $service
    try{
        $weakAcl=Get-Acl -LiteralPath $service
        $weakAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::Write,[Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $service -AclObject $weakAcl
        Assert ((Run '--configure-global-monitor true true false') -ne 0) 'A writable executable is rejected even inside a protected directory'
    }finally{Set-Acl -LiteralPath $service -AclObject $originalAcl}
    if($ProbeExecutable){
        Assert ((Probe @('--check-service-source',$AppDirectory)).Exit -eq 0) 'The protected payload and its hash manifest pass validation'
        Assert ((Probe @('--check-service-source',$AppDirectory,$AppDirectory)).Exit -ne 0) 'Deployment cannot replace a directory containing its own installation source'
        $manifest=Join-Path $AppDirectory 'internal/service-payload.json';$originalManifest=[IO.File]::ReadAllText($manifest)
        try{
            '{"sha256":"00"}'|Set-Content $manifest
            Assert ((Probe @('--check-service-source',$AppDirectory)).Exit -ne 0) 'A payload hash mismatch is rejected'
        }finally{[IO.File]::WriteAllText($manifest,$originalManifest)}
        $parent=Split-Path -Parent $AppDirectory
        $originalAcl=Get-Acl -LiteralPath $parent
        # Only the dedicated fixture parent may be weakened, never Program Files itself.
        if((Split-Path -Leaf $parent) -eq 'PathMan-Review-Fixture'){
            try{
                $weakAcl=Get-Acl -LiteralPath $parent
                $weakAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles,[Security.AccessControl.AccessControlType]::Allow))
                Set-Acl -LiteralPath $parent -AclObject $weakAcl
                Assert ((Probe @('--check-service-source',$AppDirectory)).Exit -ne 0) 'A replaceable parent invalidates an otherwise protected payload'
            }finally{Set-Acl -LiteralPath $parent -AclObject $originalAcl}
            $junction=Join-Path $parent 'LinkedSource'
            try{
                New-Item -ItemType Junction -Path $junction -Target $AppDirectory|Out-Null
                Assert ((Probe @('--check-service-source',$junction)).Exit -ne 0) 'A junction in the source path is rejected'
            }finally{if(Test-Path $junction){[IO.Directory]::Delete($junction)}}
        }
    }
    $machine.SetValue('Path','C:\GlobalBase',[Microsoft.Win32.RegistryValueKind]::String);$machine.DeleteValue('EXTERNAL_PATH',$false)
    Assert ((Run '--configure-global-monitor true true false true true false false') -eq 0) 'An all-users installation enables the unified SCM service'
    WaitFor {(Get-Service PathMan.Monitor).Status -eq 'Running' -and (Status).OriginsReady} 'Unified service or ETW collector did not become ready'
    Assert ((Get-ItemProperty ('HKLM:\SYSTEM\CurrentControlSet\Services\PathMan.Monitor')).FailureActionsOnNonCrashFailures -eq 1) 'SCM recovery includes reported nonzero service stops'
    $oldProcess=(Get-CimInstance Win32_Service -Filter "Name='PathMan.Monitor'").ProcessId
    $machineFile=Join-Path $global 'machine/environment.json';$recoverySnapshot=[IO.File]::ReadAllText($machineFile)
    [IO.File]::WriteAllText($machineFile,'{invalid')
    WaitFor {(Get-Service PathMan.Monitor).Status -eq 'Stopped'} 'The induced service failure was not reported to SCM'
    if($ProbeExecutable){Assert ((Probe @('--global-service-health')).Output.Trim() -ne 'OK') 'Enabled but stopped monitoring produces a GUI health warning'}
    [IO.File]::WriteAllText($machineFile,$recoverySnapshot);$recoverySnapshot=$null
    WaitFor {(Get-Service PathMan.Monitor).Status -eq 'Running' -and (Status).OriginsReady -and (Get-CimInstance Win32_Service -Filter "Name='PathMan.Monitor'").ProcessId -ne $oldProcess} 'SCM did not recover a reported service failure'
    Assert ($true) 'SCM automatically restarts the failed service without a manual launch'
    if($ProbeExecutable){Assert ((Probe @('--global-service-health')).Output.Trim() -eq 'OK') 'The GUI health warning clears after service recovery'}
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
    Assert ((Run '--configure-global-monitor true true true true false false false') -eq 0) 'Global duplicate removal can be enabled without moving entries'
    WaitFor {(MachineState).removeMachinePathDuplicates -and !(MachineState).moveExternalMachinePathEntries} 'The service did not load its duplicate-only policy'
    $duplicatePath='C:\GlobalBase;%EXTERNAL_PATH%;E:\GlobalDuplicate'
    $machine.SetValue('Path',($duplicatePath+';E:\GlobalDuplicate'),[Microsoft.Win32.RegistryValueKind]::ExpandString)
    WaitFor {$machine.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -eq $duplicatePath} 'Global duplicate removal did not run independently'
    Assert ($machine.GetValue('EXTERNAL_PATH') -eq 'D:\GlobalNew') 'Duplicate-only organization leaves new unique entries in PATH'
    Assert ((Run '--configure-global-monitor true true true false true false false') -eq 0) 'Global moving can be enabled without duplicate removal'
    WaitFor {!(MachineState).removeMachinePathDuplicates -and (MachineState).moveExternalMachinePathEntries} 'The service did not load its move-only policy'
    $machine.SetValue('Path',($duplicatePath+';F:\GlobalMove;F:\GlobalMove'),[Microsoft.Win32.RegistryValueKind]::ExpandString)
    WaitFor {$machine.GetValue('EXTERNAL_PATH') -eq 'D:\GlobalNew;F:\GlobalMove;F:\GlobalMove'} 'Global move-only policy did not preserve duplicate additions'
    Assert ($machine.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -eq $duplicatePath) 'Move-only organization preserves the existing PATH entries'
    Assert ((Run '--configure-global-monitor true true true true true true true') -eq 0) 'Both global source exclusions can be configured'
    WaitFor {(MachineState).ignoreSystemEditorMachineChanges -and (MachineState).ignorePowerToysMachineChanges} 'The service did not load its source exclusions'
    $csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if(!(Test-Path $csc)){$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'}
    $source=Join-Path $ResultDirectory 'registry-writer.cs'
    @'
using Microsoft.Win32;
class Writer {
    static void Main(string[] args) {
        using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",true))
            key.SetValue("Path",args[0],RegistryValueKind.ExpandString);
        System.Threading.Thread.Sleep(750);
    }
}
'@ | Set-Content $source
    foreach($name in 'SystemPropertiesAdvanced','PowerToys.EnvironmentVariables'){
        $writer=Join-Path $ResultDirectory ($name+'.exe')
        & $csc /nologo /target:exe ("/out:"+$writer) $source
        if($LASTEXITCODE -ne 0){throw 'Cannot compile the registry writer fixture'}
        $excludedPath=$duplicatePath+';H:\'+$name
        $process=Start-Process $writer -ArgumentList ('"'+$excludedPath+'"') -WindowStyle Hidden -PassThru -Wait -RedirectStandardError (Join-Path $ResultDirectory 'writer.stderr.log')
        Assert ($process.ExitCode -eq 0) ($name+' fixture writes the system PATH')
        WaitFor {@((MachineState).snapshot|Where-Object {$_.name -eq 'Path' -and $_.value -eq $excludedPath}).Count -eq 1} 'The service did not observe the excluded change'
        Start-Sleep -Seconds 2
        Assert ($machine.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -eq $excludedPath) ('Global exclusion preserves the actual '+$name+' write')
    }
    $writer=Join-Path $ResultDirectory 'AllowedWriter.exe'
    & $csc /nologo /target:exe ("/out:"+$writer) $source
    if($LASTEXITCODE -ne 0){throw 'Cannot compile the allowed registry writer fixture'}
    $allowedPath=$excludedPath+';G:\GlobalAllowed;G:\GlobalAllowed'
    $process=Start-Process $writer -ArgumentList ('"'+$allowedPath+'"') -WindowStyle Hidden -PassThru -Wait -RedirectStandardError (Join-Path $ResultDirectory 'writer.stderr.log')
    Assert ($process.ExitCode -eq 0) 'The allowed fixture writes PATH with a newly opened, traceable registry handle'
    WaitFor {$machine.GetValue('EXTERNAL_PATH') -eq 'D:\GlobalNew;F:\GlobalMove;F:\GlobalMove;G:\GlobalAllowed'} 'A known, allowed writer was blocked by the source exclusion policy'
    Assert ($machine.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -eq $excludedPath) 'Allowed writers still organize PATH while both source exclusions are enabled'
    $machine.DeleteValue($customTarget,$false)
    Assert ((Run ('--configure-global-monitor true true true true true false false '+$customTarget)) -eq 0) 'The global service accepts a custom PATH migration target'
    WaitFor {(MachineState).externalMachinePathVariable -eq $customTarget} 'The service did not load its custom destination'
    $machine.SetValue('Path',($excludedPath+';I:\CustomTarget'),[Microsoft.Win32.RegistryValueKind]::ExpandString)
    WaitFor {$machine.GetValue($customTarget) -eq 'I:\CustomTarget'} 'The service did not migrate additions to the selected variable'
    Assert ($machine.GetValue('EXTERNAL_PATH') -eq 'D:\GlobalNew;F:\GlobalMove;F:\GlobalMove;G:\GlobalAllowed') 'Selecting a new destination preserves the previous target contents'
    Assert ((Run '--configure-global-monitor true true true true true false false Path') -ne 0) 'The privileged service rejects PATH as its own migration destination'
    Assert ((State).externalMachinePathVariable -eq $customTarget) 'Rejected destinations leave the saved policy unchanged'
    Assert ((Run '--configure-global-monitor true true true true true true true') -eq 0) 'The previous seven-switch interface retains the selected destination'
    Assert ((Run '--configure-global-monitor false true true') -eq 0) 'The unified service can be disabled'
    WaitFor {(Get-Service PathMan.Monitor).Status -eq 'Stopped'} 'Unified service did not stop'
    Assert (!(State).enabled -and (State).autoArrangeMachinePath -and (State).provideNonAdministratorOrigins) 'Disabling unified monitoring retains its saved options'
    Assert ((State).removeMachinePathDuplicates -and (State).moveExternalMachinePathEntries -and (State).ignoreSystemEditorMachineChanges -and (State).ignorePowerToysMachineChanges) 'All four global child options survive a legacy three-switch update'
    Assert ((State).externalMachinePathVariable -eq $customTarget) 'The selected destination survives a legacy three-switch update'
    Assert ((Run '--configure-global-monitor true false false') -eq 0) 'The unified service can restart without recreating the installation'
    WaitFor {(Status).Running -and (Status).OriginsReady} 'Unified service did not restart'
    Assert ((Run '--remove-origin-tasks') -eq 0) 'Uninstall maintenance removes the SCM service and privileged collectors'
    Assert ($null -eq (Get-Service PathMan.Monitor -ErrorAction SilentlyContinue)) 'No SCM service remains after uninstall maintenance'
    @{Passed=$true;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $ResultDirectory 'results.json') -Encoding UTF8
    Write-Host 'PASS: installation scope, SCM lifecycle, single-process origins, global PATH writes/backups, option persistence, restart and uninstall.'
}catch{@{Passed=$false;Error=$_.Exception.Message;Trace=$_.ScriptStackTrace;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $ResultDirectory 'results.json') -Encoding UTF8;throw}
finally{
    if($recoverySnapshot){[IO.File]::WriteAllText((Join-Path $global 'machine/environment.json'),$recoverySnapshot)}
    $null=Run '--remove-origin-tasks'
    $machine.SetValue('Path',$oldPath,$oldPathKind)
    if($null -eq $oldExternal){$machine.DeleteValue('EXTERNAL_PATH',$false)}else{$machine.SetValue('EXTERNAL_PATH',$oldExternal,$oldExternalKind)}
    if($null -eq $oldCustom){$machine.DeleteValue($customTarget,$false)}else{$machine.SetValue($customTarget,$oldCustom,$oldCustomKind)};$machine.Dispose()
    if($null -ne $oldMarker){[IO.File]::WriteAllBytes($marker,$oldMarker)}elseif(Test-Path $marker){Remove-Item -LiteralPath $marker -Force}
    if($staging -and [IO.Path]::GetFullPath($staging).StartsWith((Join-Path $env:ProgramFiles 'PathMan-CI-'),[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $staging -Recurse -Force}
}
