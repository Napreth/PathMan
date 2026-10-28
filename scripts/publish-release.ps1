[CmdletBinding()]
param([string]$ReleaseDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release'),[string]$PackageDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release-packages'),[switch]$Plan)
$ErrorActionPreference='Stop'
$metadata=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'metadata.json') -Raw|ConvertFrom-Json
. (Join-Path $PSScriptRoot 'version.ps1')
$version=Get-BuildVersion $metadata.VersionName $metadata.VersionNumber $metadata.ReleaseType
$assets=@()
$installers=@()
foreach($arch in 'x86','x64','arm64') {foreach($suffix in '','-SelfContained') {
    $name="PathMan-$($version.Version)-$arch$suffix.exe";$file=Join-Path $PackageDirectory $name
    if(!(Test-Path -LiteralPath $file)){throw "Release installer is missing: $name"}
    $manifest=Get-Content -LiteralPath ($file+'.json') -Raw|ConvertFrom-Json
    $kind=if($suffix){'SelfContained'}else{'FrameworkDependent'}
    if($manifest.VersionName -ne $version.VersionName -or $manifest.VersionNumber -ne $version.VersionNumber -or $manifest.Architecture -ne $arch -or $manifest.PackageType -ne $kind){throw "Release installer identity mismatch: $name"}
    $installer=Get-ReleaseInstaller $file $arch ([bool]$suffix)
    if($installer.sha256 -ine $manifest.SHA256){throw "Release installer checksum mismatch: $name"}
    $installers+=$installer
    $assets+=$file
}}
$releaseFile=Join-Path $ReleaseDirectory 'release.json'
$release=Get-Content -LiteralPath $releaseFile -Raw|ConvertFrom-Json
if($release.versionName -ne $version.VersionName -or $release.versionNumber -ne $version.VersionNumber -or $release.type -ne $version.Type){throw 'Release metadata identity mismatch.'}
if($version.IsLocal){throw 'Local development builds cannot be published.'}
Write-ReleaseManifest $version $releaseFile $installers
$assets+=$releaseFile
if($Plan){$assets;return}
function Invoke-Gh([string[]]$Arguments) {& gh @Arguments;if($LASTEXITCODE -ne 0){throw 'GitHub release operation failed.'}}
$existing=& gh release view $metadata.Tag --json isDraft 2>$null
if($LASTEXITCODE -eq 0) {
    if(!(($existing -join "`n"|ConvertFrom-Json).isDraft)){throw 'This release is already published. Refusing to replace its installers.'}
}else{
    $create=@('release','create',$metadata.Tag,'--draft','--title',$metadata.Title,'--notes-file',(Join-Path $ReleaseDirectory 'notes.md'))
    if([int]$version.VersionNumber.Split('.')[1] -gt 0){$create+=@('--target',$metadata.Commit)}else{$create+='--verify-tag'}
    Invoke-Gh $create
}
# release.json is public; per-installer JSON manifests remain internal.
Invoke-Gh (@('release','upload',$metadata.Tag,'--clobber')+$assets)
$prerelease=if($version.Prerelease){'true'}else{'false'}
Invoke-Gh @('release','edit',$metadata.Tag,'--title',$metadata.Title,'--notes-file',(Join-Path $ReleaseDirectory 'notes.md'),"--prerelease=$prerelease",'--draft=false')
