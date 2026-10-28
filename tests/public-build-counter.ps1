$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('artifacts/counter-tests/'+[guid]::NewGuid().ToString('N'))
. (Join-Path $root 'scripts/local-build.ps1')
New-Item -ItemType Directory -Force -Path $fixture|Out-Null
$source=Join-Path $fixture 'source'
New-Item -ItemType Directory -Path $source|Out-Null
Copy-Item (Join-Path $root 'Directory.Build.props') (Join-Path $source 'Directory.Build.props')
Set-PublicBuildVersion (Join-Path $source 'Directory.Build.props') 0 0
$savedToken=$env:PATHMAN_VARIABLES_TOKEN;$savedGh=$env:GH_TOKEN
$env:PATHMAN_VARIABLES_TOKEN='fixture-variable-token';$env:GH_TOKEN='fixture-content-token'
function Invoke-FixtureGit([string[]]$Arguments,[string]$WorkingDirectory=$source){
    $start=[Diagnostics.ProcessStartInfo]::new('git');$start.UseShellExecute=$false;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in (@('-C',$WorkingDirectory)+$Arguments)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{$output=$process.StandardOutput.ReadToEndAsync();$errorText=$process.StandardError.ReadToEndAsync();$process.WaitForExit();if($process.ExitCode){throw $errorText.GetAwaiter().GetResult()};$output.GetAwaiter().GetResult().Trim()}finally{$process.Dispose()}
}
function Assert($condition,[string]$message){if(!$condition){throw $message}}
function gh {param([Parameter(ValueFromRemainingArguments)][string[]]$Values)
    $global:LASTEXITCODE=0
    $method=$Values[2];$endpoint=$Values[3];$body=$null
    if($Values -contains '--input'){$body=Get-Content -LiteralPath $Values[($Values.IndexOf('--input')+1)] -Raw|ConvertFrom-Json -AsHashtable}
    $mock=$global:CounterFixture
    if($endpoint -like '*/actions/variables*'){
        Assert ($env:GH_TOKEN -eq 'fixture-variable-token') 'Variable requests require the dedicated Variables token'
        if($method -eq 'GET'){
            if(!$mock.state){$global:LASTEXITCODE=1;'HTTP 404';return}
            @{value=($mock.state|ConvertTo-Json -Depth 20 -Compress)}|ConvertTo-Json;return
        }
        $mock.state=$body.value|ConvertFrom-Json -AsHashtable
        return
    }
    Assert ($env:GH_TOKEN -eq 'fixture-content-token') 'Source writeback must use the normal repository token'
    if($endpoint -like '*/git/ref/heads/main'){@{object=@{sha=$mock.mainCommit}}|ConvertTo-Json;return}
    if($method -eq 'GET' -and $endpoint -like '*/contents/Directory.Build.props*'){
        $ref=[Uri]::UnescapeDataString(($endpoint -split '\?ref=')[1])
        $text=if($ref -eq $mock.mainCommit){$mock.mainText}else{$mock.branches[$ref].text}
        $sha=if($ref -eq $mock.mainCommit){'main-blob'}else{$mock.branches[$ref].sha}
        @{sha=$sha;content=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))}|ConvertTo-Json;return
    }
    if($method -eq 'PUT' -and $endpoint -like '*/contents/Directory.Build.props'){
        $branch=$mock.branches[$body.branch]
        Assert ($body.sha -eq $branch.sha) 'Writeback must use the current source blob revision'
        $branch.text=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($body.content))
        $directory=if($mock.remoteWrite){$mock.server}else{$source}
        [IO.File]::WriteAllText((Join-Path $directory 'Directory.Build.props'),$branch.text,[Text.UTF8Encoding]::new($false))
        $null=Invoke-FixtureGit @('add','Directory.Build.props') $directory;$null=Invoke-FixtureGit @('-c','commit.gpgsign=false','commit','-m',$body.message) $directory
        $branch.sha=[guid]::NewGuid().ToString('N');$commit=Invoke-FixtureGit @('rev-parse','HEAD') $directory;$mock.writes++
        if($mock.remoteWrite){
            $null=Invoke-FixtureGit @('push','origin',"HEAD:refs/heads/$($body.branch)") $directory
            & git -C $source cat-file -e "$commit^{commit}" 2>$null
            $mock.missingBeforeValidation=$LASTEXITCODE -ne 0
            $global:LASTEXITCODE=0
        }
        @{commit=@{sha=$commit}}|ConvertTo-Json;return
    }
    throw "Unexpected GitHub request: $method $endpoint"
}
try{
    $null=Invoke-FixtureGit @('init','-b','main');$null=Invoke-FixtureGit @('config','user.name','PathMan tests');$null=Invoke-FixtureGit @('config','user.email','tests@example.invalid')
    $null=Invoke-FixtureGit @('add','Directory.Build.props');$null=Invoke-FixtureGit @('-c','commit.gpgsign=false','commit','-m','initial')
    $initial=Invoke-FixtureGit @('rev-parse','HEAD');$text=[IO.File]::ReadAllText((Join-Path $source 'Directory.Build.props'))
    $global:CounterFixture=@{state=$null;mainCommit=$initial;mainText=$text;writes=0;branches=@{'feature/one'=@{text=$text;sha='one'};'feature/two'=@{text=$text;sha='two'}}}
    & (Join-Path $root 'scripts/public-build.ps1') -Repository Test/Repo -RepositoryRoot $source -Branch feature/one -OutputDirectory (Join-Path $fixture 'first')
    $first=Get-Content (Join-Path $fixture 'first/metadata.json') -Raw|ConvertFrom-Json
    Assert ($first.VersionNumber -eq '0.1.0' -and $first.Tag -eq 'test/0.1.0') 'The first public test receives a numeric identity and test tag'
    Assert ($first.Commit -eq (Invoke-FixtureGit @('rev-parse','HEAD')) -and (Read-PublicBuildVersion (Join-Path $source 'Directory.Build.props')).Y -eq 1) 'The public build uses the source commit after configuration writeback'
    $null=Invoke-FixtureGit @('checkout','--detach',$initial)
    & (Join-Path $root 'scripts/public-build.ps1') -Repository Test/Repo -RepositoryRoot $source -Branch feature/two -OutputDirectory (Join-Path $fixture 'second')
    $second=Get-Content (Join-Path $fixture 'second/release.json') -Raw|ConvertFrom-Json
    Assert ($second.versionNumber -eq '0.2.0' -and $second.type -eq 'development') 'Persistent repository state allocates the next public development identity'
    & (Join-Path $root 'scripts/public-build.ps1') -Operation Check -Repository Test/Repo -RepositoryRoot $source -OutputDirectory (Join-Path $fixture 'second')
    $rejected=$false;try{& (Join-Path $root 'scripts/public-build.ps1') -Operation Check -Repository Test/Repo -RepositoryRoot $source -OutputDirectory (Join-Path $fixture 'first')}catch{$rejected=$true}
    Assert $rejected 'A superseded build cannot publish'
    $global:CounterFixture.mainText=Update-PublicBuildText $text 1 0
    $rejected=$false;try{& (Join-Path $root 'scripts/public-build.ps1') -Repository Test/Repo -RepositoryRoot $source -Branch feature/two -OutputDirectory (Join-Path $fixture 'outdated')}catch{$rejected=$true}
    Assert ($rejected -and $global:CounterFixture.state.y -eq 2 -and $global:CounterFixture.writes -eq 2) 'An outdated branch is rejected before consuming Y or writing source'
    Set-PublicBuildVersion (Join-Path $source 'Directory.Build.props') 1 0
    $null=Invoke-FixtureGit @('add','Directory.Build.props');$null=Invoke-FixtureGit @('-c','commit.gpgsign=false','commit','-m','release epoch one')
    $releaseCommit=Invoke-FixtureGit @('rev-parse','HEAD')
    $global:CounterFixture.mainCommit=$releaseCommit
    $global:CounterFixture.mainText=[IO.File]::ReadAllText((Join-Path $source 'Directory.Build.props'))
    $null=Invoke-FixtureGit @('-c','tag.gpgsign=false','tag','-a','v1.0.0-beta.1','-m','Release one')
    & (Join-Path $root 'scripts/public-build.ps1') -Kind Release -Tag v1.0.0-beta.1 -Repository Test/Repo -RepositoryRoot $source -OutputDirectory (Join-Path $fixture 'release')
    Assert ($global:CounterFixture.state.latestNumber -eq '1.0.0') 'Tagged releases use mainline X without incrementing it again'
    $remote=Join-Path $fixture 'remote.git';$server=Join-Path $fixture 'server'
    $null=Invoke-FixtureGit @('clone','--bare',$source,$remote)
    $null=Invoke-FixtureGit @('remote','add','origin',$remote)
    $null=Invoke-FixtureGit @('clone',$remote,$server)
    $null=Invoke-FixtureGit @('config','user.name','PathMan tests') $server;$null=Invoke-FixtureGit @('config','user.email','tests@example.invalid') $server
    $global:CounterFixture.server=$server;$global:CounterFixture.remoteWrite=$true
    $global:CounterFixture.branches['feature/three']=@{text=$global:CounterFixture.mainText;sha='three'}
    & (Join-Path $root 'scripts/public-build.ps1') -Repository Test/Repo -RepositoryRoot $source -Branch feature/three -OutputDirectory (Join-Path $fixture 'third')
    $third=Get-Content (Join-Path $fixture 'third/metadata.json') -Raw|ConvertFrom-Json
    Assert ($third.VersionNumber -eq '1.1.0' -and $global:CounterFixture.missingBeforeValidation) 'Y restarts in the new X and fetches the remote writeback commit before checking ancestry'
    Assert ((Invoke-FixtureGit @('rev-parse',"$($third.Commit)^{commit}")) -eq $third.Commit) 'The source revision returned by the writeback API is available for validation'
    Assert ($env:GH_TOKEN -eq 'fixture-content-token') 'The variable token is not retained for later operations'
    Write-Host 'PASS: repository-variable Y, shared branch numbering, source writeback before build, isolated tokens and publication freshness.'
}finally{
    Remove-Item Function:\gh -ErrorAction SilentlyContinue
    Remove-Variable CounterFixture -Scope Global -ErrorAction SilentlyContinue
    $env:PATHMAN_VARIABLES_TOKEN=$savedToken;$env:GH_TOKEN=$savedGh
    $allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/counter-tests'))+[IO.Path]::DirectorySeparatorChar
    if([IO.Path]::GetFullPath($fixture).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path $fixture)){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
