[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('x86','x64','arm64')][string]$Architecture,[switch]$DisposableVM,[switch]$ResolveOnly)
$ErrorActionPreference='Stop'
if(!$env:ACT -and $env:GITHUB_ACTIONS -ne 'true' -and !($DisposableVM -and (Get-CimInstance Win32_ComputerSystem).Model -match 'Virtual Machine')){throw 'Runtime installation requires a disposable VM or a GitHub-hosted runner.'}
$metadata=Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$release=$metadata.releases|Where-Object {$_.windowsdesktop.version -eq $metadata.'latest-runtime'}|Select-Object -First 1
if($ResolveOnly){
    if(!$release){throw 'Cannot resolve the Desktop Runtime release.'}
    if($env:GITHUB_OUTPUT){"version=$($release.windowsdesktop.version)"|Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8}
    Write-Host "Desktop Runtime $($release.windowsdesktop.version)"
    return
}
if($env:ACT){
    $runtimeRoot=Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts/tools/dotnet-$Architecture"
    New-Item -ItemType Directory -Force -Path $runtimeRoot|Out-Null
    foreach($component in 'runtime','windowsdesktop'){
        $file=$release.$component.files|Where-Object {$_.rid -eq "win-$Architecture" -and $_.url.EndsWith('.zip') -and $_.name -notlike '*apphost*'}|Select-Object -First 1
        if(!$file -or ([uri]$file.url).Host -ne 'builds.dotnet.microsoft.com'){throw 'A valid portable runtime archive was not found.'}
        $archive=Join-Path $runtimeRoot ($component+'.zip')
        if(!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ine $file.hash){Invoke-WebRequest -Uri $file.url -OutFile $archive}
        if((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ine $file.hash){throw 'Portable runtime checksum mismatch.'}
        Expand-Archive -LiteralPath $archive -DestinationPath $runtimeRoot -Force
    }
    "DOTNET_ROOT_$($Architecture.ToUpperInvariant())=$runtimeRoot"|Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
    Write-Host 'Local act uses a portable runtime; system registration is unchanged.'
    return
}
$file=$release.windowsdesktop.files|Where-Object {$_.rid -eq "win-$Architecture" -and $_.url.EndsWith('.exe')}|Select-Object -First 1
if(!$file -or ([uri]$file.url).Host -ne 'builds.dotnet.microsoft.com'){throw 'A valid .NET Desktop Runtime installer was not found.'}
$directory=Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/tools'
New-Item -ItemType Directory -Force -Path $directory|Out-Null
$installer=Join-Path $directory "windowsdesktop-runtime-$Architecture.exe"
if(!(Test-Path -LiteralPath $installer) -or (Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash -ine $file.hash){Invoke-WebRequest -Uri $file.url -OutFile $installer}
if((Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash -ine $file.hash){throw '.NET Desktop Runtime checksum mismatch.'}
$process=Start-Process -FilePath $installer -ArgumentList '/install /quiet /norestart' -PassThru -Wait -WindowStyle Hidden
if($process.ExitCode -notin 0,3010){throw ".NET Desktop Runtime installation failed: $($process.ExitCode)"}
$runtimeRoot=$null
foreach($view in [Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32) {
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
    $key=$base.OpenSubKey("SOFTWARE\dotnet\Setup\InstalledVersions\$Architecture")
    try{if($key){$runtimeRoot=$key.GetValue('InstallLocation')}}finally{if($key){$key.Dispose()};$base.Dispose()}
    if($runtimeRoot){break}
}
if(!$runtimeRoot){throw 'The runtime installation was not registered.'}
if($env:GITHUB_ENV){"DOTNET_ROOT_$($Architecture.ToUpperInvariant())=$runtimeRoot"|Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8}
