[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $projectRoot 'scripts/version.ps1')
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
    foreach($version in '1.0','1.0.0-01','1.2.65536','1.0.0+','v1.0.0') {$rejected=$false;try{$null=Get-BuildVersion $version}catch{$rejected=$true};Assert $rejected "Malformed version accepted: $version"}
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
        [IO.File]::WriteAllText($file,'fixture');@{SHA256=(Get-FileHash $file).Hash}|ConvertTo-Json|Set-Content ($file+'.json')
    }}
    $assets=@(& (Join-Path $projectRoot 'scripts/publish-release.ps1') -ReleaseDirectory (Join-Path $fixture 'output') -PackageDirectory $fixture -Plan)
    Assert ($assets.Count -eq 6 -and @($assets|Where-Object {!$_.EndsWith('.exe')}).Count -eq 0) 'Only six EXE installers may be attached to a release'
    $ghCalls=[Collections.Generic.List[string[]]]::new()
    function gh {param([Parameter(ValueFromRemainingArguments)][string[]]$Values)
        $ghCalls.Add($Values)
        $global:LASTEXITCODE=if($Values[1] -eq 'view'){1}else{0}
    }
    & (Join-Path $projectRoot 'scripts/publish-release.ps1') -ReleaseDirectory (Join-Path $fixture 'output') -PackageDirectory $fixture
    Assert ($ghCalls.Count -eq 4 -and $ghCalls[1] -contains '--verify-tag' -and $ghCalls[1] -contains '--draft') 'Publishing must verify the remote tag and prepare a draft first'
    $uploaded=@($ghCalls[2]|Where-Object {$_.EndsWith('.exe')})
    Assert ($uploaded.Count -eq 6 -and @($ghCalls[2]|Where-Object {$_.EndsWith('.json')}).Count -eq 0) 'GitHub upload arguments must contain six EXEs and no JSON'
    Assert ($ghCalls[3] -contains '--prerelease=true' -and $ghCalls[3] -contains '--draft=false') 'Only a completed upload is published, with the prerelease flag'
    Write-Host 'PASS: SemVer inputs, exact tag prose, OpenPGP/X.509 and genuine SSH signatures, lightweight rejection, six EXE-only assets and draft-to-published arguments.'
}finally{
    $allowed=[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/release-tests'))+[IO.Path]::DirectorySeparatorChar
    if([IO.Path]::GetFullPath($fixture).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
