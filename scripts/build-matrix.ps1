[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'arm64')][string[]]$Arch = @('x86', 'x64', 'arm64'),
    [ValidateSet('FrameworkDependent', 'SelfContained')][string[]]$PackageType = @('FrameworkDependent', 'SelfContained'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [Alias('Version')][string]$VersionName = '',
    [string]$VersionNumber = '',
    [ValidateSet('official','beta')][string]$ReleaseType = '',
    [switch]$X,
    [switch]$Y,
    [switch]$Z,
    [switch]$SkipInstaller,
    [switch]$Test,
    [switch]$Plan
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'version.ps1')
. (Join-Path $PSScriptRoot 'local-build.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
if([int]$X.IsPresent+[int]$Y.IsPresent+[int]$Z.IsPresent -gt 1){throw 'Choose only one of -X, -Y or -Z.'}
if($VersionNumber -and ($X -or $Y -or $Z)){throw 'An explicit version number cannot be combined with -X, -Y or -Z.'}
$localBuild=$null;$buildLock=$null;$lockHeld=$false;$buildSucceeded=$false;$publicBefore=$null;$publicAfter=$null
try {
if(!$VersionNumber){
    $key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($projectRoot.ToUpperInvariant()))).Substring(0,24)
    $buildLock=[Threading.Mutex]::new($false,'Local\PathMan.Build.'+$key)
    try{if(!$buildLock.WaitOne(0)){throw 'Another local build is already running.'};$lockHeld=$true}catch [Threading.AbandonedMutexException]{$lockHeld=$true}
    $localBuild=Get-NextLocalBuild (Join-Path $projectRoot 'Directory.Build.props') (Join-Path $projectRoot '.pathman/build-state.json') $(if($X){'X'}elseif($Y){'Y'}else{'Z'})
    $VersionNumber=$localBuild.VersionNumber
}
$versionInfo=Get-BuildVersion $VersionName $VersionNumber $ReleaseType
$Version=$versionInfo.Version
$versionProperties=@("-p:PathManVersionName=$($versionInfo.VersionName)","-p:PathManVersionNumber=$($versionInfo.VersionNumber)","-p:PathManReleaseType=$($versionInfo.ReleaseType)")
$projectRoot = Split-Path -Parent $PSScriptRoot
$Arch = @($Arch | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
$PackageType = @($PackageType | ForEach-Object {
    if ($_ -ieq 'SelfContained') { 'SelfContained' } else { 'FrameworkDependent' }
} | Select-Object -Unique)
$matrix = foreach ($cpu in $Arch) {
    foreach ($kind in $PackageType) {
        $suffix = if ($kind -eq 'SelfContained') { '-SelfContained' } else { '' }
        [pscustomobject]@{ Arch = $cpu; Type = $kind; VersionNumber=$versionInfo.VersionNumber; File = "PathMan-$Version-$cpu$suffix.exe" }
    }
}
if ($Plan) { $matrix | Format-Table -AutoSize; return }
if($localBuild -and $localBuild.Part -in @('X','Y')){
    $publicBefore=[IO.File]::ReadAllText((Join-Path $projectRoot 'Directory.Build.props'))
    Set-PublicBuildVersion (Join-Path $projectRoot 'Directory.Build.props') $localBuild.X $localBuild.Y
    $publicAfter=[IO.File]::ReadAllText((Join-Path $projectRoot 'Directory.Build.props'))
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}
function Reset-PublishDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/publish')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid publish path: $resolved" }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $resolved 'bin'),(Join-Path $resolved 'internal') | Out-Null
}

Invoke-Checked 'dotnet' (@('build', (Join-Path $projectRoot 'PathMan.slnx'), '-c', $Configuration)+$versionProperties)
if ($Test) {
    Invoke-Checked 'dotnet' @('run', '--project', (Join-Path $projectRoot 'tests/Pathman.Core.Tests'), '-c', $Configuration, '--no-build')
}
foreach ($cpu in $Arch) {
    $runtime = "win-$cpu"
    $nativeBuild = Join-Path $projectRoot "artifacts/native-$runtime"
    $cmakeArch = switch ($cpu) { 'arm64' { 'ARM64' } 'x86' { 'Win32' } 'x64' { 'x64' } }
    Invoke-Checked 'cmake' @('-S', (Join-Path $projectRoot 'src/Pathman.Shim'), '-B', $nativeBuild, '-A', $cmakeArch,"-DPATHMAN_VERSION_NUMBER=$($versionInfo.VersionNumber)","-DPATHMAN_VERSION_NAME=$Version")
    Invoke-Checked 'cmake' @('--build', $nativeBuild, '--config', $Configuration)
    foreach ($kind in $PackageType) {
        $output = Join-Path $projectRoot "artifacts/publish/$runtime/$kind"
        Reset-PublishDirectory $output
        $selfContained = ($kind -eq 'SelfContained').ToString().ToLowerInvariant()
        foreach ($project in @(
            @{ Name = 'Gui'; Destination = $output },
            @{ Name = 'Cli'; Destination = (Join-Path $output 'bin') },
            @{ Name = 'Service'; Destination = (Join-Path $output 'internal') }
        )) {
            Invoke-Checked 'dotnet' (@('publish', (Join-Path $projectRoot "src/Pathman.$($project.Name)/Pathman.$($project.Name).csproj"),
                '-c', $Configuration, '-r', $runtime, '--self-contained', $selfContained,
                '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
                '-p:DebugType=None', '-o', $project.Destination)+$versionProperties)
        }
        Copy-Item -LiteralPath (Join-Path $nativeBuild "$Configuration/PathMan.Shim.exe") -Destination (Join-Path $output 'internal/PathMan.Shim.exe')
        Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $output
        Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') -Destination $output
        Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $output -Recurse
        Write-ReleaseManifest $versionInfo (Join-Path $output 'release.json')
        $hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        if ($Test -and ($cpu -eq $hostArch -or ($cpu -eq 'x86' -and $hostArch -eq 'x64'))) {
            & (Join-Path $PSScriptRoot 'test.ps1') -Architecture $cpu -PublishDirectory $output -CoreAssembly (Join-Path $projectRoot "src/Pathman.Core/bin/$Configuration/net10.0-windows/Pathman.Core.dll")
        } elseif ($Test) { Write-Host "Runtime tests for $cpu require a compatible test machine." }
        Write-Host "Published $cpu / $kind to $output"
    }
}
if (!$SkipInstaller) {
    & (Join-Path $PSScriptRoot 'package.ps1') -Arch $Arch -PackageType $PackageType -Configuration $Configuration -VersionName $Version -VersionNumber $versionInfo.VersionNumber -ReleaseType $versionInfo.ReleaseType
}
if($localBuild){Complete-LocalBuild (Join-Path $projectRoot '.pathman/build-state.json') $localBuild}
$buildSucceeded=$true
} finally {
    if(!$buildSucceeded -and $null -ne $publicBefore -and [IO.File]::ReadAllText((Join-Path $projectRoot 'Directory.Build.props')) -ceq $publicAfter){[IO.File]::WriteAllText((Join-Path $projectRoot 'Directory.Build.props'),$publicBefore,[Text.UTF8Encoding]::new($false))}
    if($buildLock){if($lockHeld){$buildLock.ReleaseMutex()};$buildLock.Dispose()}
}
