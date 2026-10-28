[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $projectRoot 'scripts/version.ps1')
. (Join-Path $projectRoot 'scripts/build-identity.ps1')
$fixture=Join-Path $projectRoot ('artifacts/release-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
function Assert($value,[string]$message){if(!$value){throw $message}}
function Git([string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new('git');$start.UseShellExecute=$false;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.Encoding]::UTF8
    foreach($value in (@('-C',$fixture)+$Arguments)){$start.ArgumentList.Add($value)}
    $process=[Diagnostics.Process]::Start($start)
    try{$result=$process.StandardOutput.ReadToEndAsync();$errorText=$process.StandardError.ReadToEndAsync();$process.WaitForExit();if($process.ExitCode){throw $errorText.GetAwaiter().GetResult()};$result.GetAwaiter().GetResult().TrimEnd("`r","`n")}finally{$process.Dispose()}
}
try {
    foreach($version in '1.0.0-beta.1','2.3.4-rc.10+build.42','0.0.0') {Assert ((Get-BuildVersion $version).Version -eq $version) 'SemVer build version was changed'}
    foreach($version in '1.0','1.0.0-01','1.0.0+','v1.0.0') {$rejected=$false;try{$null=Get-BuildVersion $version}catch{$rejected=$true};Assert $rejected "Malformed version accepted: $version"}
    foreach($number in '1.0','01.0.0','1.0.65536','1.-1.0','1.2.3-beta'){$rejected=$false;try{$null=Get-BuildVersion '1.0.0' $number}catch{$rejected=$true};Assert $rejected "Malformed build number accepted: $number"}
    Assert ((Get-BuildVersion '1.0.0-beta.1' '1.0.0').Prerelease -and (Get-BuildVersion '1.0.0' '2.3.0' 'official').Prerelease -and !(Get-BuildVersion '1.0.0' '3.0.0' 'official').Prerelease) 'Beta and Y builds are prereleases, official X builds are not'
    Assert ((Get-BuildVersion '1.0.0' '2.3.7').Type -eq 'development' -and (Get-BuildVersion).VersionNumber.EndsWith('.0')) 'Local Z overrides must not change checked-in configuration'
    $counter=New-BuildCounter 'Test/Repo'
    Assert ((Reserve-BuildIdentity $counter Test '' 'commit-a' 0) -eq '0.1.0') 'The first public test is 0.1.0'
    Assert ((Reserve-BuildIdentity $counter Release 'v1.0.0-beta.1' 'commit-b' 1) -eq '1.0.0') 'The first tagged release uses mainline X'
    Assert ((Reserve-BuildIdentity $counter Release 'v1.0.0-beta.1' 'commit-b' 1) -eq '1.0.0') 'An immediate retry retains the reservation'
    Assert ((Reserve-BuildIdentity $counter Test '' 'branch-a' 1) -eq '1.1.0' -and (Reserve-BuildIdentity $counter Test '' 'branch-b' 1) -eq '1.2.0') 'Branches share the public build counter'
    $rejected=$false;try{$null=Reserve-BuildIdentity $counter Release 'v1.0.0-beta.1' 'commit-b' 1}catch{$rejected=$true};Assert $rejected 'Superseded release reservations cannot publish again'
    Assert ((Reserve-BuildIdentity $counter Release 'v1.0.0' 'commit-c' 7) -eq '7.0.0' -and $counter.y -eq 0) 'Mainline X is authoritative and resets Y without a second increment'
    $null=Git @('init');$null=Git @('config','user.name','PathMan tests');$null=Git @('config','user.email','tests@example.invalid');$null=Git @('-c','commit.gpgsign=false','commit','--allow-empty','-m','fixture')
    $tag='v1.0.0-beta.1+build.7';$notes="版本标题`n`n`nFirst body line`n`nLiteral `$(Write-Output unsafe) and quotes ' `"`nLast line`n"
    [IO.File]::WriteAllText((Join-Path $fixture 'message.txt'),$notes,[Text.UTF8Encoding]::new($false))
    $null=Git @('-c','tag.gpgsign=false','tag','-a',$tag,'-F',(Join-Path $fixture 'message.txt'))
    & (Join-Path $projectRoot 'scripts/release-metadata.ps1') -Tag $tag -RepositoryRoot $fixture -OutputDirectory (Join-Path $fixture 'output')
    $metadata=Get-Content (Join-Path $fixture 'output/metadata.json') -Raw|ConvertFrom-Json
    Assert ($metadata.Title -eq "$tag - 版本标题" -and $metadata.Prerelease -and $metadata.Version -eq '1.0.0-beta.1+build.7') 'Annotated tag title or SemVer metadata was not preserved'
    $body=[IO.File]::ReadAllText((Join-Path $fixture 'output/notes.md'))
    Assert ($body -eq ($notes.Substring($notes.IndexOf('First body line')))) 'Body must start at its first nonempty line and retain all following text literally'
    $commit=Git @('rev-parse','HEAD')
    foreach($kind in 'PGP SIGNATURE','SIGNED MESSAGE') {
        $object="object $commit`ntype commit`ntag v1.0.1`ntagger Test <tests@example.invalid> 1700000000 +0000`n`nTitle`n`nBody`n-----BEGIN $kind-----`nfixture-only`n-----END $kind-----`n"
        [IO.File]::WriteAllText((Join-Path $fixture 'tag.txt'),$object,[Text.UTF8Encoding]::new($false))
        $hash=Git @('hash-object','-t','tag','-w',(Join-Path $fixture 'tag.txt'));$null=Git @('update-ref','refs/tags/v1.0.1',$hash)
        & (Join-Path $projectRoot 'scripts/release-metadata.ps1') -Tag 'v1.0.1' -RepositoryRoot $fixture -OutputDirectory (Join-Path $fixture 'signature')
        Assert (([IO.File]::ReadAllText((Join-Path $fixture 'signature/notes.md'))) -eq "Body`n") "Signature leaked into release notes: $kind"
    }
    $key=Join-Path $fixture 'signing-key'
    & ssh-keygen -q -t ed25519 -N '' -f $key
    if($LASTEXITCODE){throw 'Test SSH key generation failed.'}
    $null=Git @('-c','gpg.format=ssh','-c',"user.signingkey=$key",'tag','-s','v1.0.2','-m',"SSH release`n`nSSH body")
    & (Join-Path $projectRoot 'scripts/release-metadata.ps1') -Tag 'v1.0.2' -RepositoryRoot $fixture -OutputDirectory (Join-Path $fixture 'ssh')
    Assert (([IO.File]::ReadAllText((Join-Path $fixture 'ssh/notes.md'))) -eq "SSH body`n") 'A genuine SSH tag signature leaked into release notes'
    $null=Git @('-c','tag.gpgsign=false','tag','v1.0.3');$rejected=$false
    try{& (Join-Path $projectRoot 'scripts/release-metadata.ps1') -Tag 'v1.0.3' -RepositoryRoot $fixture -OutputDirectory (Join-Path $fixture 'lightweight')}catch{$rejected=$true}
    Assert $rejected 'A lightweight tag without a message cannot provide a release title'
    foreach($arch in 'x86','x64','arm64'){foreach($suffix in '','-SelfContained') {
        $file=Join-Path $fixture "PathMan-1.0.0-beta.1+build.7-$arch$suffix.exe"
        [IO.File]::WriteAllText($file,'fixture');@{Architecture=$arch;PackageType=$(if($suffix){'SelfContained'}else{'FrameworkDependent'});SHA256=(Get-FileHash $file).Hash;VersionName=$metadata.VersionName;VersionNumber=$metadata.VersionNumber}|ConvertTo-Json|Set-Content ($file+'.json')
    }}
    $assets=@(& (Join-Path $projectRoot 'scripts/publish-release.ps1') -ReleaseDirectory (Join-Path $fixture 'output') -PackageDirectory $fixture -Plan)
    Assert ($assets.Count -eq 7 -and @($assets|Where-Object {$_.EndsWith('.exe')}).Count -eq 6 -and @($assets|Where-Object {[IO.Path]::GetFileName($_) -eq 'release.json'}).Count -eq 1) 'A release contains six EXEs and one public release.json'
    $release=Get-Content (Join-Path $fixture 'output/release.json') -Raw|ConvertFrom-Json
    Assert ($release.type -eq 'beta' -and $release.installers.Count -eq 6 -and @($release.PSObject.Properties.Name|Where-Object {$_ -in 'channel','releaseType','commit','repository'}).Count -eq 0) 'Public release metadata includes a type and all six installers without build provenance'
    foreach($entry in $release.installers){
        $file=Join-Path $fixture $entry.fileName
        Assert ($entry.architecture -in 'x86','x64','arm64' -and $entry.selfContained -is [bool] -and $entry.size -eq (Get-Item $file).Length -and $entry.sha256 -ieq (Get-FileHash $file).Hash) 'Each installer records architecture, runtime type, actual size and checksum'
    }
    $first=Get-Item (Join-Path $fixture $release.installers[0].fileName)
    [IO.File]::WriteAllText($first.FullName,'tampered')
    $rejected=$false;try{& (Join-Path $projectRoot 'scripts/publish-release.ps1') -ReleaseDirectory (Join-Path $fixture 'output') -PackageDirectory $fixture -Plan}catch{$rejected=$true}
    Assert $rejected 'Changed installers must be rejected before assembling the release'
    [IO.File]::WriteAllText($first.FullName,'fixture')
    $ghCalls=[Collections.Generic.List[string[]]]::new()
    function gh {param([Parameter(ValueFromRemainingArguments)][string[]]$Values)
        $ghCalls.Add($Values)
        $global:LASTEXITCODE=if($Values[1] -eq 'view'){1}else{0}
    }
    & (Join-Path $projectRoot 'scripts/publish-release.ps1') -ReleaseDirectory (Join-Path $fixture 'output') -PackageDirectory $fixture
    Assert ($ghCalls.Count -eq 4 -and $ghCalls[1] -contains '--verify-tag' -and $ghCalls[1] -contains '--draft') 'Publishing must verify the remote tag and prepare a draft first'
    $uploaded=@($ghCalls[2]|Where-Object {$_.EndsWith('.exe')})
    Assert ($uploaded.Count -eq 6 -and @($ghCalls[2]|Where-Object {$_.EndsWith('.json')}).Count -eq 1 -and @($ghCalls[2]|Where-Object {$_.EndsWith('.exe.json')}).Count -eq 0) 'Only release.json is published alongside the six installers'
    Assert ($ghCalls[3] -contains '--prerelease=true' -and $ghCalls[3] -contains '--draft=false') 'Only a completed upload is published, with the prerelease flag'
    $public=Get-BuildVersion '1.0.0' '4.1.0' 'official'
    foreach($arch in 'x86','x64','arm64'){foreach($suffix in '','-SelfContained'){
        $file=Join-Path $fixture "PathMan-1.0.0-$arch$suffix.exe"
        [IO.File]::WriteAllText($file,'fixture');@{Architecture=$arch;PackageType=$(if($suffix){'SelfContained'}else{'FrameworkDependent'});SHA256=(Get-FileHash $file).Hash;VersionName=$public.VersionName;VersionNumber=$public.VersionNumber}|ConvertTo-Json|Set-Content ($file+'.json')
    }}
    @{Tag='test/4.1.0';VersionName=$public.VersionName;VersionNumber=$public.VersionNumber;ReleaseType=$public.ReleaseType;Prerelease=$public.Prerelease;Title='Public development build';Commit=$commit}|ConvertTo-Json|Set-Content (Join-Path $fixture 'output/metadata.json')
    Write-ReleaseManifest $public (Join-Path $fixture 'output/release.json')
    $ghCalls.Clear()
    & (Join-Path $projectRoot 'scripts/publish-release.ps1') -ReleaseDirectory (Join-Path $fixture 'output') -PackageDirectory $fixture
    $development=Get-Content (Join-Path $fixture 'output/release.json') -Raw|ConvertFrom-Json
    Assert ($development.type -eq 'development' -and $development.installers.Count -eq 6 -and $ghCalls[1] -contains '--target' -and $ghCalls[3] -contains '--prerelease=true') 'Public Y builds publish the development type and six installers as a GitHub prerelease'
    $local=Get-BuildVersion '1.0.0' '4.1.1' 'official'
    foreach($entry in $development.installers){
        $sidecar=Join-Path $fixture ($entry.fileName+'.json');$identity=Get-Content $sidecar -Raw|ConvertFrom-Json
        $identity.VersionNumber=$local.VersionNumber;$identity|ConvertTo-Json|Set-Content $sidecar
    }
    @{Tag='test/4.1.1';VersionName=$local.VersionName;VersionNumber=$local.VersionNumber;ReleaseType=$local.ReleaseType;Prerelease=$local.Prerelease;Title='Local build';Commit=$commit}|ConvertTo-Json|Set-Content (Join-Path $fixture 'output/metadata.json')
    Write-ReleaseManifest $local (Join-Path $fixture 'output/release.json')
    $rejected=$false;try{& (Join-Path $projectRoot 'scripts/publish-release.ps1') -ReleaseDirectory (Join-Path $fixture 'output') -PackageDirectory $fixture}catch{$rejected=$_.Exception.Message -eq 'Local development builds cannot be published.'}
    Assert ($rejected -and $ghCalls.Count -eq 4) 'Local Z builds remain unpublished even though their type is also development'
    Write-Host 'PASS: release types, shared counters, signed-tag prose, six-installer metadata, checksum validation and draft publication.'
}finally{
    $allowed=[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/release-tests'))+[IO.Path]::DirectorySeparatorChar
    if([IO.Path]::GetFullPath($fixture).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
