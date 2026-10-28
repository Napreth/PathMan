[CmdletBinding()]
param([ValidateSet('Reserve','Check')][string]$Operation='Reserve',[ValidateSet('Release','Test')][string]$Kind='Test',[string]$Tag='',[string]$Branch=$env:GITHUB_REF_NAME,[string]$Repository=$env:GITHUB_REPOSITORY,[string]$RepositoryRoot=(Split-Path -Parent $PSScriptRoot),[string]$OutputDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'version.ps1')
. (Join-Path $PSScriptRoot 'build-identity.ps1')
. (Join-Path $PSScriptRoot 'local-build.ps1')
if($Repository -notmatch '^[\w.-]+/[\w.-]+$'){throw 'A repository namespace is required.'}
$root=$RepositoryRoot
if(!$env:PATHMAN_VARIABLES_TOKEN){throw 'Configure PATHMAN_VARIABLES_TOKEN with repository Variables read/write permission.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
function Api([string]$Method,[string]$Endpoint,$Body=$null,[switch]$AllowMissing,[switch]$Variable) {
    $arguments=@('api','--method',$Method,$Endpoint)
    $temporary=Join-Path $OutputDirectory ('request-'+[guid]::NewGuid().ToString('N')+'.json')
    $previousToken=$env:GH_TOKEN
    try{
        if($Variable){$env:GH_TOKEN=$env:PATHMAN_VARIABLES_TOKEN}
        if($null -ne $Body){$Body|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $temporary -Encoding utf8;$arguments+=@('--input',$temporary)}
        $result=& gh @arguments 2>&1
        if($LASTEXITCODE){if($AllowMissing -and ($result -join "`n") -match '\b404\b'){return $null};throw ($result -join "`n")}
        if(($result -join '').Trim()){($result -join "`n")|ConvertFrom-Json -AsHashtable}
    }finally{$env:GH_TOKEN=$previousToken;if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
}
function Includes([string]$Ancestor,[string]$Commit) {
    if(!$Ancestor){return}
    foreach($revision in @($Ancestor,$Commit)|Select-Object -Unique){
        & git -C $root cat-file -e "$revision^{commit}" 2>$null
        if($LASTEXITCODE){
            & git -C $root -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch origin $revision
            if($LASTEXITCODE){throw 'Cannot fetch a public build commit.'}
        }
    }
    & git -C $root merge-base --is-ancestor $Ancestor $Commit
    if($LASTEXITCODE){throw 'Public builds must contain the latest release commit. Rebase or merge the current release before publishing.'}
}
$endpoint="repos/$Repository"
function Get-SourceConfiguration([string]$Ref) {
    $response=Api GET "$endpoint/contents/Directory.Build.props?ref=$([Uri]::EscapeDataString($Ref))"
    $text=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($response.content)).TrimStart([char]0xFEFF)
    [pscustomobject]@{Text=$text;Sha=$response.sha;Public=(ConvertFrom-PublicBuildText $text)}
}
$main=Api GET "$endpoint/git/ref/heads/main"
$mainCommit=$main.object.sha
$mainConfiguration=Get-SourceConfiguration $mainCommit
$variableName='PATHMAN_BUILD_STATE'
$variable=Api GET "$endpoint/actions/variables/$variableName" -AllowMissing -Variable
$state=if($variable){$variable.value|ConvertFrom-Json -AsHashtable}else{New-BuildCounter $Repository}
if($state.schemaVersion -ne 2 -or $state.repository -ne $Repository){throw 'Invalid repository build variable.'}
if($Operation -eq 'Check'){
    $metadata=Get-Content (Join-Path $OutputDirectory 'metadata.json') -Raw|ConvertFrom-Json
    if($state.latestNumber -ne $metadata.VersionNumber -or $state.latestTag -ne $metadata.Tag -or [int]$metadata.VersionNumber.Split('.')[0] -ne $mainConfiguration.Public.X){throw 'This public build was superseded and cannot be published.'}
    Includes $state.releaseCommit $metadata.Commit
    return
}
$commit=(& git -C $root rev-parse HEAD).Trim();if($LASTEXITCODE){throw 'Cannot identify the build commit.'}
Includes $state.releaseCommit $commit
if($Kind -eq 'Release'){
    if(!$Tag){throw 'A release tag is required.'}
    $source=Read-PublicBuildVersion (Join-Path $root 'Directory.Build.props')
    if($source.X -ne $mainConfiguration.Public.X -or $source.Y -ne 0){throw 'A release tag must use mainline X with public Y reset to zero.'}
    Includes $commit $mainCommit
    & (Join-Path $PSScriptRoot 'release-metadata.ps1') -Tag $Tag -VersionNumber "$($source.X).0.0" -RepositoryRoot $root -OutputDirectory $OutputDirectory
    $tagMetadata=Get-Content (Join-Path $OutputDirectory 'metadata.json') -Raw|ConvertFrom-Json
    if($tagMetadata.Commit -ne $commit){throw 'The release checkout must match the annotated tag commit.'}
}else{
    if(!$Branch){throw 'Select a build branch for a public Y build.'}
    & git -C $root check-ref-format "refs/heads/$Branch"
    if($LASTEXITCODE){throw 'Invalid build branch.'}
    $branchConfiguration=Get-SourceConfiguration $Branch
    if($branchConfiguration.Public.X -ne $mainConfiguration.Public.X){throw 'The build branch has an older X. Rebase or merge main before publishing.'}
    if(!$variable){$state.x=$mainConfiguration.Public.X;$state.y=[Math]::Max($mainConfiguration.Public.Y,$branchConfiguration.Public.Y)}
}
$number=Reserve-BuildIdentity $state $Kind $Tag $commit $mainConfiguration.Public.X
if($Kind -eq 'Test'){$Tag="test/$number"}
$body=@{name=$variableName;value=($state|ConvertTo-Json -Depth 20 -Compress)}
if($variable){$null=Api PATCH "$endpoint/actions/variables/$variableName" $body -Variable}
else{$null=Api POST "$endpoint/actions/variables" $body -Variable}
if($Kind -eq 'Test'){
    $text=Update-PublicBuildText $branchConfiguration.Text $state.x $state.y
    $write=Api PUT "$endpoint/contents/Directory.Build.props" @{branch=$Branch;sha=$branchConfiguration.Sha;message="build: set version $number [skip ci]";content=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))}
    $commit=$write.commit.sha
    Includes $state.releaseCommit $commit
    [xml]$properties=$text
    $version=Get-BuildVersion ($properties.SelectSingleNode('//PathManVersionName').InnerText) $number ($properties.SelectSingleNode('//PathManReleaseType').InnerText)
    @{Tag=$Tag;Version=$version.VersionName;VersionName=$version.VersionName;VersionNumber=$number;ReleaseType=$version.ReleaseType;Prerelease=$true;Title="PathMan $($version.VersionName) ($number)";Commit=$commit;Branch=$Branch}|ConvertTo-Json|Set-Content (Join-Path $OutputDirectory 'metadata.json') -Encoding utf8
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'notes.md'),"Public test build $number`n`nCommit: $commit`n",[Text.UTF8Encoding]::new($false))
    Write-ReleaseManifest $version (Join-Path $OutputDirectory 'release.json')
}
$metadata=Get-Content (Join-Path $OutputDirectory 'metadata.json') -Raw|ConvertFrom-Json
if($env:GITHUB_OUTPUT){@("version=$($metadata.VersionName)","version_number=$number","release_type=$($metadata.ReleaseType)","source_ref=$($metadata.Commit)")|Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8}
Write-Host "Reserved $($metadata.VersionName) / $number at $($metadata.Commit)"
