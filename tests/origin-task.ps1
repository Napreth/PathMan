[CmdletBinding()]
param(
    [string]$AppDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish/win-x64/SelfContained'),
    [string]$ResultDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/origin-task'),
    [switch]$UserPhase,
    [switch]$MissingTask
)
$ErrorActionPreference='Stop'
$AppDirectory=[IO.Path]::GetFullPath($AppDirectory).TrimEnd('\')
$ResultDirectory=[IO.Path]::GetFullPath($ResultDirectory)
if($env:GITHUB_ACTIONS -ne 'true' -and (Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Use a disposable Windows VM or GitHub Actions runner.'}
$service=Join-Path $AppDirectory 'internal/PathMan.Service.exe'
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$hash=[Security.Cryptography.SHA256]::Create()
try{$id=([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($service.ToUpperInvariant())))).Replace('-','').Substring(0,24)}finally{$hash.Dispose()}
$taskName='PathMan.Origins.'+$id+'.'+$sid
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$checks=[Collections.Generic.List[string]]::new()
function Assert($condition,[string]$message){if(!$condition){throw $message};$checks.Add($message)}
function WaitFor([scriptblock]$condition,[string]$message){for($i=0;$i -lt 200;$i++){if(& $condition){return};Start-Sleep -Milliseconds 150};throw $message}
function RunService([string[]]$arguments){$p=Start-Process $service -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait;return $p.ExitCode}
New-Item -ItemType Directory -Force -Path $ResultDirectory|Out-Null
if($UserPhase){
    $result=Join-Path $ResultDirectory $(if($MissingTask){'missing-task.json'}else{'user.json'})
    $dataRoot=Join-Path $ResultDirectory 'data';New-Item -ItemType Directory -Force -Path $dataRoot|Out-Null
    $config=Join-Path $dataRoot 'environment.json'
    $run=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
    $old=$run.GetValue('PathMan',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $kind=if($null -ne $old){$run.GetValueKind('PathMan')}else{$null}
    $probeName='PATHMAN_ORIGIN_TEST_'+[guid]::NewGuid().ToString('N')
    $process=$null
    function State{Get-Content -LiteralPath $config -Raw|ConvertFrom-Json}
    function Configure([bool]$monitoring,[bool]$tracking){$state=State;$state.monitoringEnabled=$monitoring;$state.trackChangeOrigins=$tracking;$state.originTrackingRequest=[guid]::NewGuid().ToString('N');$state|ConvertTo-Json -Depth 40|Set-Content -LiteralPath $config -Encoding UTF8}
    function Launch{$script:process=Start-Process $service -ArgumentList @('--data-root',('"'+$dataRoot+'"')) -WindowStyle Hidden -PassThru}
    try{
        Assert (!$admin) 'Background launch runs with a limited user token'
        @{groups=@(@{id='paths';name='Paths';rule='paths'},@{id='system';name='System';rule='builtin'},@{id='other';name='Other';rule='fallback'},@{id='external';name='External';rule='external';requiresMonitoring=$true});preferences=@{language='en-US';autoCheckUpdates=$false};monitoringEnabled=$true;trackChangeOrigins=$true;originTrackingRequest=[guid]::NewGuid().ToString('N')}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $config -Encoding UTF8
        $run.SetValue('PathMan',('"'+$service+'"'))
        Launch
        if($MissingTask){
            WaitFor {![string]::IsNullOrEmpty((State).originTrackingMessage)} 'Missing authorization was not reported'
            Assert (!(State).originTrackingReady -and !$process.HasExited) 'A missing task leaves ordinary monitoring running and reports an authorization error'
        }else{
            WaitFor {(State).originTrackingReady} 'Scheduled origin tracking did not become ready'
            Assert ((State).originTrackingReady) 'A limited monitor starts the privileged collector without interactive elevation'
            $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$folder=$scheduler.GetFolder('\');$task=$folder.GetTask($taskName)
            $denied=$false;try{$task.Enabled=$false}catch{$denied=$true};Assert $denied 'The desktop user cannot change the privileged task'
            $denied=$false;try{$stream=[IO.File]::Open($task.Definition.Actions.Item(1).Path,'Open','Write');$stream.Dispose()}catch [UnauthorizedAccessException]{$denied=$true};Assert $denied 'The desktop user cannot overwrite the collector executable'
            $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true)
            try{$key.SetValue($probeName,'origin-test')}finally{$key.Dispose()}
            WaitFor {@((State).changes|Where-Object {$_.name -eq $probeName -and @($_.origins|Where-Object processId -eq $PID).Count -gt 0}).Count -gt 0} 'The scheduled collector did not identify the actual registry writer'
            Assert $true 'The SYSTEM collector reports the correct desktop user registry and writer PID'
            Configure $true $false;WaitFor {!(State).originTrackingReady} 'Disabling tracking did not stop collection'
            Configure $true $true;WaitFor {(State).originTrackingReady} 'Reenabling tracking did not reuse the saved task'
            Assert $true 'Origin tracking can stop and restart without another administrator authorization'
            $hash=[Security.Cryptography.SHA256]::Create()
            try{$instance=([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($AppDirectory.ToUpperInvariant())))).Replace('-','').Substring(0,16)}finally{$hash.Dispose()}
            $stop=[Threading.EventWaitHandle]::OpenExisting(('Local\PathMan.'+$sid+'.'+$instance+'.Stop'))
            try{$null=$stop.Set()}finally{$stop.Dispose()}
            WaitFor {$process.HasExited} 'The background did not stop'
            Launch;WaitFor {(State).originTrackingReady} 'The next background launch did not reuse the saved authorization'
            Assert $true 'A subsequent background launch reuses the task without a UAC prompt'
        }
        Assert (!(Get-Process consent -ErrorAction SilentlyContinue)) 'No UAC consent process is left waiting'
        @{Passed=$true;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $result -Encoding UTF8
    }catch{@{Passed=$false;Error=$_.Exception.Message;Trace=$_.ScriptStackTrace;State=$(if(Test-Path $config){State});Checks=$checks.ToArray()}|ConvertTo-Json -Depth 40|Set-Content -LiteralPath $result -Encoding UTF8;throw}
    finally{
        if(Test-Path $config){Configure $false $false}
        if($process -and !$process.HasExited){if(!$process.WaitForExit(10000)){$process.Kill()}}
        if($null -eq $old){$run.DeleteValue('PathMan',$false)}else{$run.SetValue('PathMan',$old,$kind)};$run.Dispose()
        $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment',$true);try{$key.DeleteValue($probeName,$false)}finally{$key.Dispose()}
    }
    return
}
if(!$admin){throw 'The setup phase requires an administrator token.'}
$acl=Get-Acl -LiteralPath $ResultDirectory
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),[Security.AccessControl.FileSystemRights]::Modify,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
Set-Acl -LiteralPath $ResultDirectory -AclObject $acl
$launcher=Join-Path $ResultDirectory 'limited-process.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /out:$launcher (Join-Path $PSScriptRoot 'limited-process.cs')
if($LASTEXITCODE -ne 0){throw 'Failed to compile the limited-token test launcher.'}
function RunUserPhase([switch]$Missing){
    $file=Join-Path $ResultDirectory $(if($Missing){'missing-task.json'}else{'user.json'})
    if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file -Force}
    $arguments='"'+(Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe')+'" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -AppDirectory "'+$AppDirectory+'" -ResultDirectory "'+$ResultDirectory+'" -UserPhase'+$(if($Missing){' -MissingTask'})
    $process=Start-Process $launcher -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait -RedirectStandardError (Join-Path $ResultDirectory 'launcher.stderr.log') -RedirectStandardOutput (Join-Path $ResultDirectory 'launcher.stdout.log')
    if($process.ExitCode -ne 0){throw ('Limited process failed ('+$process.ExitCode+'): '+(Get-Content (Join-Path $ResultDirectory 'launcher.stderr.log') -Raw))}
    WaitFor {Test-Path -LiteralPath $file} 'Limited test process did not report its result'
    $result=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
    if(!$result.Passed){throw ($result|ConvertTo-Json -Depth 40)}
    Assert ($process.ExitCode -eq 0) 'The limited test process completed successfully'
    $checks.AddRange([string[]]$result.Checks)
}
try{
    Assert ((RunService @('--register-origin-task',$sid)) -eq 0) 'One-time administrator setup registers the collector'
    $task=Get-ScheduledTask -TaskName $taskName
    Assert ($task.Principal.UserId -in @('SYSTEM','S-1-5-18') -and $task.Triggers.Count -eq 0) 'The SYSTEM task runs only on demand from the ordinary login monitor'
    Assert ($task.Actions.Execute.StartsWith([Environment]::GetFolderPath('ProgramFiles')+'\') -and $task.Actions.Execute -ne $service) 'The privileged executable is copied into a protected location'
    RunUserPhase
    Assert ((RunService @('--remove-origin-tasks')) -eq 0) 'Uninstall maintenance removes this installation task and protected files'
    Assert ($null -eq (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) -and !(Test-Path -LiteralPath $task.Actions.Execute)) 'No privileged task or collector executable remains after cleanup'
    RunUserPhase -Missing
    @{Passed=$true;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $ResultDirectory 'results.json') -Encoding UTF8
    Write-Host 'PASS: one-time authorization, limited-user login/restart, ETW origins, protected task/files, no automatic UAC fallback and uninstall cleanup.'
}finally{
    $null=RunService @('--remove-origin-tasks')
}
