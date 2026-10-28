[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('x86','x64','arm64')][string]$Architecture,[Parameter(Mandatory)][string]$Version,[string]$BaselineVersion='0.0.0',[string]$PackageDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/setup'),[switch]$DisposableVM)
$ErrorActionPreference='Stop'
if(!($env:GITHUB_ACTIONS -eq 'true' -and !$env:ACT) -and !($DisposableVM -and (Get-CimInstance Win32_ComputerSystem).Model -match 'Virtual Machine')) {throw 'Installer acceptance requires a disposable Windows VM or a GitHub-hosted runner. It must not run on the local act host.'}
$root=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path $root "artifacts/installer-acceptance/$Architecture"
$resultFile=Join-Path $testRoot 'results.json'
New-Item -ItemType Directory -Force -Path $testRoot|Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$message) {if(!$value){throw $message};$checks.Add($message)}
function Run([string]$File,[string]$Arguments,[int]$Expected=0) {
    $process=Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    try {if(!$process.WaitForExit(90000)){$process.Kill();throw "Process timed out: $File"};Assert ($process.ExitCode -eq $Expected) "Exit code $($process.ExitCode), expected $Expected for $([IO.Path]::GetFileName($File)) ($Arguments)"}finally{$process.Dispose()}
}
function Registration([bool]$Machine) {
    $hive=if($Machine){[Microsoft.Win32.RegistryHive]::LocalMachine}else{[Microsoft.Win32.RegistryHive]::CurrentUser}
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,[Microsoft.Win32.RegistryView]::Registry32)
    try{$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan')}finally{$base.Dispose()}
}
function Registered([bool]$Machine) {$key=Registration $Machine;try {if($key){[pscustomobject]@{Version=$key.GetValue('DisplayVersion');Directory=$key.GetValue('InstallLocation')}}}finally{if($key){$key.Dispose()}}}
function PathValue([bool]$Machine) {
    $base=if($Machine){[Microsoft.Win32.Registry]::LocalMachine}else{[Microsoft.Win32.Registry]::CurrentUser}
    $key=$base.OpenSubKey($(if($Machine){'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'}else{'Environment'}))
    try{$key.GetValue('Path','',[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}finally{$key.Dispose()}
}
function MachineType([string]$File) {$stream=[IO.File]::OpenRead($File);$reader=[IO.BinaryReader]::new($stream);try{$stream.Position=0x3c;$offset=$reader.ReadInt32();$stream.Position=$offset+4;$reader.ReadUInt16()}finally{$reader.Dispose()}}
$expectedMachine=@{x86=0x14c;x64=0x8664;arm64=0xaa64}[$Architecture]
$data=Join-Path $env:LOCALAPPDATA 'PathMan/environment.json'
foreach($scope in $false,$true) {if(Registered $scope){throw 'A preexisting installation must not be overwritten by acceptance tests.'}}
try {
    foreach($machine in $false,$true) {foreach($kind in 'FrameworkDependent','SelfContained') {
        $label="$Architecture / $kind / $(if($machine){'AllUsers'}else{'CurrentUser'})"
        $scope=if($machine){'/AllUsers'}else{'/CurrentUser'}
        $directory=Join-Path $testRoot "$(if($machine){'system'}else{'user'})-$kind"
        $suffix=if($kind -eq 'SelfContained'){'-SelfContained'}else{''}
        $oldInstaller=Join-Path $PackageDirectory "PathMan-$BaselineVersion-$Architecture$suffix.exe"
        $installer=Join-Path $PackageDirectory "PathMan-$Version-$Architecture$suffix.exe"
        $uninstaller=Join-Path $directory 'internal/Uninstall.exe'
        $originalPath=PathValue $machine
        try {
            Run $oldInstaller "/S $scope /D=$directory"
            $oldHash=(Get-FileHash (Join-Path $directory 'PathMan.exe')).Hash
            Assert ((Registered $machine).Version -eq $BaselineVersion) "$label baseline version is registered"
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $data)|Out-Null
            $config=Get-Content (Join-Path $root 'src/Pathman.Core/EnvironmentVariables/Defaults/groups.json') -Raw|ConvertFrom-Json
            $config|Add-Member -NotePropertyName preferences -NotePropertyValue @{language='en-US';theme='system';autoCheckUpdates=$false} -Force
            $config|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $data -Encoding utf8
            $configHash=(Get-FileHash $data).Hash
            $pathBeforeUpgrade=PathValue $machine
            Run $installer "/S $scope /D=$directory"
            Assert ((PathValue $machine) -ceq $pathBeforeUpgrade) "$label installer never registers or migrates the CLI PATH entry"
            $registration=Registered $machine
            Assert ($registration.Version -eq $Version -and $registration.Directory.TrimEnd('\') -eq $directory.TrimEnd('\')) "$label upgrade updates registration and preserves the install directory"
            Assert ((Get-FileHash (Join-Path $directory 'PathMan.exe')).Hash -ne $oldHash) "$label upgrade replaces the older executable"
            Assert ((Get-FileHash $data).Hash -eq $configHash) "$label upgrade preserves workspace settings"
            Assert ((MachineType (Join-Path $directory 'PathMan.exe')) -eq $expectedMachine) "$label GUI has the requested PE architecture"
            Assert ((MachineType (Join-Path $directory 'bin/pathman.exe')) -eq $expectedMachine) "$label CLI has the requested PE architecture"
            Run (Join-Path $directory 'bin/pathman.exe') '--help'
            Run (Join-Path $directory 'bin/pathman.exe') 'list' 1
            $gui=Start-Process (Join-Path $directory 'PathMan.exe') -PassThru
            try {
                Assert ($gui.WaitForInputIdle(20000) -and !$gui.HasExited) "$label installed GUI starts successfully"
                Start-Sleep -Milliseconds 700
                Assert (!$gui.HasExited) "$label GUI stays running after initial page load"
            }finally{if(!$gui.HasExited){$gui.Kill();$gui.WaitForExit()};$gui.Dispose()}
            # The main window owns opt-in CLI PATH maintenance and its journal.
            # Subsequent installer operations must preserve that resulting state.
            $configHash=(Get-FileHash $data).Hash
            $hash=(Get-FileHash (Join-Path $directory 'PathMan.exe')).Hash
            Run $installer "/S $scope /D=$directory"
            Assert ((Get-FileHash (Join-Path $directory 'PathMan.exe')).Hash -eq $hash) "$label same-version reinstall retains the verified payload"
            $registrationKey=Registration $machine
            try{$installedNumber=$registrationKey.GetValue('VersionNumber','0.0.0')}finally{$registrationKey.Dispose()}
            $hive=if($machine){[Microsoft.Win32.RegistryHive]::LocalMachine}else{[Microsoft.Win32.RegistryHive]::CurrentUser}
            $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,[Microsoft.Win32.RegistryView]::Registry32)
            try{$key=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan',$true);try{$key.SetValue('VersionNumber','65535.65535.65535');Run $oldInstaller "/S $scope /D=$directory" 1638}finally{$key.SetValue('VersionNumber',$installedNumber);$key.Dispose()}}finally{$base.Dispose()}
            Assert ((Get-FileHash (Join-Path $directory 'PathMan.exe')).Hash -eq $hash) "$label downgrade is rejected before changing files"
            Run $uninstaller "/S $scope _?=$directory\internal"
            Assert (!(Registered $machine)) "$label uninstall removes registration"
            foreach($relative in 'PathMan.exe','bin/pathman.exe','internal/PathMan.Service.exe','internal/PathMan.Shim.exe','release.json','THIRD-PARTY-NOTICES.txt','licenses/dotnet-THIRD-PARTY-NOTICES.txt') {Assert (!(Test-Path (Join-Path $directory $relative))) "$label uninstall removes $relative"}
            Assert ((PathValue $machine) -ceq $originalPath) "$label uninstall restores the original PATH"
            Assert ((Get-FileHash $data).Hash -eq $configHash) "$label uninstall retains user configuration"
        }finally {
            if(Registered $machine) {if(Test-Path $uninstaller){Run $uninstaller "/S $scope _?=$directory\internal"}}
        }
    }}
    @{Passed=$true;Architecture=$Architecture;BaselineVersion=$BaselineVersion;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 5|Set-Content $resultFile
}catch{
    @{Passed=$false;Architecture=$Architecture;BaselineVersion=$BaselineVersion;Checks=$checks.ToArray();Error=$_.Exception.Message}|ConvertTo-Json -Depth 5|Set-Content $resultFile
    throw
}
