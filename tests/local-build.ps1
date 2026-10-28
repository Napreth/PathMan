$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts/local-build.ps1')
$fixture=Join-Path $root ('artifacts/local-build-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixture|Out-Null
$configuration=Join-Path $fixture 'Directory.Build.props'
$state=Join-Path $fixture 'build-state.json'
Copy-Item (Join-Path $root 'Directory.Build.props') $configuration
function Assert($value,[string]$message){if(!$value){throw $message}}
try{
    Set-PublicBuildVersion $configuration 0 0
    $first=Get-NextLocalBuild $configuration $state
    Assert ($first.VersionNumber -eq '0.0.1' -and !(Test-Path $state)) 'The default Z build starts at 0.0.1 without writing a reservation'
    Assert ((Get-NextLocalBuild $configuration $state).VersionNumber -eq '0.0.1') 'A failed build or build plan must not consume a number'
    Complete-LocalBuild $state $first
    Assert ((Get-NextLocalBuild $configuration $state).VersionNumber -eq '0.0.2') 'A successful build increments Z'
    Set-PublicBuildVersion $configuration 0 3
    Assert ((Get-NextLocalBuild $configuration $state).VersionNumber -eq '0.3.1') 'Changing public Y resets Z before incrementing'
    $next=Get-NextLocalBuild $configuration $state Y
    Assert ($next.VersionNumber -eq '0.4.0') 'Y increments and clears Z'
    Set-PublicBuildVersion $configuration $next.X $next.Y;Complete-LocalBuild $state $next
    Assert ((Get-NextLocalBuild $configuration $state).VersionNumber -eq '0.4.1') 'A Z build continues the selected public Y'
    Set-PublicBuildVersion $configuration 2 9
    Assert ((Get-NextLocalBuild $configuration $state Y).VersionNumber -eq '2.1.0') 'Changing X starts Y at one'
    $next=Get-NextLocalBuild $configuration $state X
    Assert ($next.VersionNumber -eq '3.0.0') 'X increments and clears Y/Z'
    Set-PublicBuildVersion $configuration $next.X $next.Y;Complete-LocalBuild $state $next
    Assert ((Get-NextLocalBuild $configuration $state).VersionNumber -eq '3.0.1') 'Public X changes reset Z'
    foreach($part in 'X','Y','Z'){
        Set-PublicBuildVersion $configuration 65535 65535
        Complete-LocalBuild $state ([pscustomobject]@{VersionNumber='65535.65535.65535'})
        $rejected=$false;try{$null=Get-NextLocalBuild $configuration $state $part}catch{$rejected=$true}
        Assert $rejected "Overflow in $part must not wrap"
    }
    New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts')|Out-Null
    foreach($name in 'build-matrix.ps1','version.ps1','local-build.ps1'){Copy-Item (Join-Path $root "scripts/$name") (Join-Path $fixture "scripts/$name")}
    Copy-Item (Join-Path $root 'build.ps1') (Join-Path $fixture 'build.ps1')
    Set-PublicBuildVersion $configuration 0 0
    $buildState=Join-Path $fixture '.pathman/build-state.json'
    Complete-LocalBuild $buildState ([pscustomobject]@{VersionNumber='0.0.5'})
    $before=[IO.File]::ReadAllText($buildState)
    $beforeConfiguration=[IO.File]::ReadAllText($configuration)
    $failed=$false
    try{& {function dotnet {throw 'Intentional build failure'};& (Join-Path $fixture 'build.ps1') -X -SkipInstaller}}catch{if($_ -notmatch 'Intentional build failure'){throw};$failed=$true}
    Assert ($failed -and [IO.File]::ReadAllText($configuration) -ceq $beforeConfiguration -and [IO.File]::ReadAllText($buildState) -ceq $before) 'A failed X build restores public configuration and preserves the last successful number'
    $null=& (Join-Path $fixture 'build.ps1') -Plan -Arch x64
    $after=[IO.File]::ReadAllText($buildState)
    Assert ($before -ceq $after) 'Build plans do not change local state'
    $rejected=$false;try{& (Join-Path $fixture 'build.ps1') -X -Y -Plan}catch{$rejected=$true}
    Assert $rejected 'X/Y/Z switches are mutually exclusive'
    [xml]$public=[IO.File]::ReadAllText($configuration)
    Assert (!$public.SelectSingleNode('//PathManVersionZ')) 'Public configuration stores no Z field'
    Write-Host 'PASS: local X/Y/Z increments, successful-build persistence, public epoch resets, overflow and read-only plans.'
}finally{
    $allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/local-build-tests'))+[IO.Path]::DirectorySeparatorChar
    if([IO.Path]::GetFullPath($fixture).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
