[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer, [string]$InstallDirectory='C:\PathManTestApp', [string]$OtherInstaller)
# Run only in the disposable test VM: this installs into HKCU and tests ownership/version checks.
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine') { throw 'Run maintenance tests only in the disposable Windows VM.' }
$checks=[Collections.Generic.List[string]]::new()
function Assert($condition,[string]$message) { if(!$condition) { throw $message }; $checks.Add($message) }
function Install([int]$expected=0,[string]$directory=$InstallDirectory) {
    $process=Start-Process -FilePath $Installer -ArgumentList "/S /CurrentUser /LANG=1033 /D=$directory" -PassThru -Wait -WindowStyle Hidden
    Assert ($process.ExitCode -eq $expected) "Installer exit $($process.ExitCode), expected $expected"
}
function CheckSetupMutex {
    $wizard=Start-Process -FilePath $Installer -ArgumentList '/CurrentUser /LANG=1033' -PassThru
    try {
        for($i=0;$i -lt 100;$i++) { $wizard.Refresh(); if($wizard.MainWindowHandle -ne 0 -or $wizard.HasExited){break}; Start-Sleep -Milliseconds 100 }
        Assert (!$wizard.HasExited -and $wizard.MainWindowHandle -ne 0) 'The first installer reaches its wizard before copying files'
        Install 1618
        if($OtherInstaller) {
            $second=Start-Process $OtherInstaller -ArgumentList '/S /CurrentUser /LANG=1033' -PassThru -Wait -WindowStyle Hidden
            Assert ($second.ExitCode -eq 1618) 'Different package variants share the same setup mutex'
        }
        $uninstaller=Join-Path $InstallDirectory 'internal\Uninstall.exe'
        if(Test-Path $uninstaller) {
            # _?= prevents NSIS's temporary bootstrap from hiding the real exit code.
            $second=Start-Process $uninstaller -ArgumentList "/S /CurrentUser _?=$InstallDirectory\internal" -PassThru -Wait -WindowStyle Hidden
            Assert ($second.ExitCode -eq 1618) 'Install and uninstall are mutually exclusive from their first page'
        }
    } finally { if(!$wizard.HasExited) { [void]$wizard.CloseMainWindow(); if(!$wizard.WaitForExit(10000)){$wizard.Kill();$wizard.WaitForExit()} }; $wizard.Dispose() }
}
try {
    CheckSetupMutex
    Install
    CheckSetupMutex
    $exe=Join-Path $InstallDirectory 'PathMan.exe'
    $hash=(Get-FileHash -LiteralPath $exe).Hash
    $data=Join-Path $env:LOCALAPPDATA 'PathMan\environment.json'
    $dataHash=if(Test-Path $data) {(Get-FileHash $data).Hash} else {$null}
    Install
    Assert ((Get-FileHash $exe).Hash -eq $hash) 'Same-version reinstall keeps the expected payload'
    if($dataHash) { Assert ((Get-FileHash $data).Hash -eq $dataHash) 'Reinstall preserves workspace data byte for byte' }
    $registration=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan',$true)
    $version=[string]$registration.GetValue('DisplayVersion')
    try {
        $registration.SetValue('DisplayVersion','99.0.0'); Install 1638
        Assert ((Get-FileHash $exe).Hash -eq $hash) 'Downgrade is rejected before replacing files'
        $registration.SetValue('DisplayVersion','0.0.1'); Install
        Assert ([Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan').GetValue('DisplayVersion') -eq $version) 'Upgrade updates the registered version'
    } finally { $registration.SetValue('DisplayVersion',$version); $registration.Dispose() }
    $app=[Diagnostics.Process]::Start($exe)
    try {
        Start-Sleep -Seconds 2; Install 1602
        Assert (!$app.HasExited) 'Silent maintenance detects a running GUI and leaves it running'
        Assert ((Get-FileHash $exe).Hash -eq $hash) 'Running-application cancellation leaves files unchanged'
    } finally { if(!$app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose() }
    $cli=Join-Path $InstallDirectory 'bin\pathman.exe'
    $detached=Join-Path $env:TEMP 'PathMan-detached-cli'
    New-Item -ItemType Directory -Path $detached -Force|Out-Null
    Copy-Item $cli (Join-Path $detached 'pathman.exe') -Force
    $layout=& (Join-Path $detached 'pathman.exe') layout
    Assert ($LASTEXITCODE -eq 0 -and ($layout -join "`n").Contains($InstallDirectory)) 'A relocated CLI discovers InstallLocation from the uninstall registration'
    & $cli shim add pm-registry-self $env:ComSpec | Out-Null
    $manifest=Join-Path $env:LOCALAPPDATA 'PathMan\shims\pm-registry-self.shim'
    $original=Join-Path $InstallDirectory 'internal\PathMan.Shim.exe'
    [IO.File]::WriteAllText($manifest,"[PathMan.Shim]`r`nFormat=1`r`nTarget=$original`r`n",[Text.Encoding]::Unicode)
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $env:LOCALAPPDATA 'PathMan\shims\pm-registry-self.exe'))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardError=$true
    $self=[Diagnostics.Process]::Start($start)
    try { $null=$self.StandardError.ReadToEnd(); $self.WaitForExit(); Assert ($self.ExitCode -eq 1059) 'A copied shim resolves its original through uninstall information and rejects it as a target' }
    finally { $self.Dispose(); & $cli shim remove pm-registry-self|Out-Null }
    & $cli background start | Out-Null
    try {
        Install 1602
        $currentSession=(Get-Process -Id $PID).SessionId
        $running=@(Get-Process PathMan.Service -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $InstallDirectory 'internal\PathMan.Service.exe') -and $_.SessionId -eq $currentSession })
        Assert ($running.Count -eq 1) 'Silent maintenance preserves a running background process'
    } finally { & $cli background stop | Out-Null }
    & $cli shim add pm-maintenance-lock $env:ComSpec | Out-Null
    $alias=Join-Path $env:LOCALAPPDATA 'PathMan\shims\pm-maintenance-lock.exe'
    $command=Start-Process $alias -ArgumentList '/d /c ping -n 12 127.0.0.1 >nul' -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Milliseconds 300; Install 1602
        Assert (!$command.HasExited) 'A running hard-link alias is detected under its own executable name'
        Assert ($command.WaitForExit(20000)) 'The alias command finishes normally after installer cancellation'
    } finally { $command.Dispose(); & $cli shim remove pm-maintenance-lock | Out-Null }
    Install 87 ($InstallDirectory+'-different')
    Assert (!(Test-Path ($InstallDirectory+'-different\PathMan.exe'))) 'Upgrade cannot silently change its owned directory'
    @{Passed=$true;Checks=$checks} | ConvertTo-Json -Depth 5
} catch { @{Passed=$false;Checks=$checks;Error=$_.Exception.Message} | ConvertTo-Json -Depth 5; exit 1 }
