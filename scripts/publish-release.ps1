[CmdletBinding()]
param([string]$ReleaseDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release'),[string]$PackageDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release-packages'),[switch]$Plan)
$ErrorActionPreference='Stop'
$metadata=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'metadata.json') -Raw|ConvertFrom-Json
. (Join-Path $PSScriptRoot 'version.ps1')
$version=Get-BuildVersion $metadata.Version
$assets=@()
foreach($arch in 'x86','x64','arm64') {foreach($suffix in '','-SelfContained') {
    $name="PathMan-$($version.Version)-$arch$suffix.exe";$file=Join-Path $PackageDirectory $name
    if(!(Test-Path -LiteralPath $file)){throw "Release installer is missing: $name"}
    $manifest=Get-Content -LiteralPath ($file+'.json') -Raw|ConvertFrom-Json
    if((Get-FileHash -LiteralPath $file).Hash -ne $manifest.SHA256){throw "Release installer checksum mismatch: $name"}
    $assets+=$file
}}
if($Plan){$assets;return}
function Invoke-Gh([string[]]$Arguments) {& gh @Arguments;if($LASTEXITCODE -ne 0){throw 'GitHub release operation failed.'}}
$existing=& gh release view $metadata.Tag --json isDraft 2>$null
if($LASTEXITCODE -eq 0) {
    if(!(($existing -join "`n"|ConvertFrom-Json).isDraft)){throw 'This release is already published. Refusing to replace its installers.'}
}else{
    Invoke-Gh @('release','create',$metadata.Tag,'--verify-tag','--draft','--title',$metadata.Title,'--notes-file',(Join-Path $ReleaseDirectory 'notes.md'))
}
# Publish only installers; JSON manifests are internal verification inputs.
Invoke-Gh (@('release','upload',$metadata.Tag,'--clobber')+$assets)
$prerelease=if($metadata.Prerelease){'true'}else{'false'}
Invoke-Gh @('release','edit',$metadata.Tag,'--title',$metadata.Title,'--notes-file',(Join-Path $ReleaseDirectory 'notes.md'),"--prerelease=$prerelease",'--draft=false')
