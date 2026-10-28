function ConvertFrom-PublicBuildText([string]$Text) {
    [xml]$xml=$Text.TrimStart([char]0xFEFF)
    $x=$xml.SelectSingleNode('//PathManVersionX').InnerText
    $y=$xml.SelectSingleNode('//PathManVersionY').InnerText
    if($x -notmatch '^(0|[1-9]\d*)$' -or $y -notmatch '^(0|[1-9]\d*)$' -or [decimal]$x -gt 65535 -or [decimal]$y -gt 65535){throw 'Public X and Y must be integers from 0 to 65535.'}
    [pscustomobject]@{X=[int]$x;Y=[int]$y}
}
function Read-PublicBuildVersion([string]$ConfigurationFile) {ConvertFrom-PublicBuildText ([IO.File]::ReadAllText($ConfigurationFile))}
function Update-PublicBuildText([string]$Text,[int]$X,[int]$Y) {
    if($X -lt 0 -or $X -gt 65535 -or $Y -lt 0 -or $Y -gt 65535){throw 'Public build component overflow.'}
    foreach($item in @(@{Name='X';Value=$X},@{Name='Y';Value=$Y})){
        $pattern='(<PathManVersion'+$item.Name+'>)\d+(</PathManVersion'+$item.Name+'>)'
        if([regex]::Matches($text,$pattern).Count -ne 1){throw 'The public build configuration must contain exactly one X and Y.'}
        $text=[regex]::Replace($text,$pattern,{param($match) $match.Groups[1].Value+[string]$item.Value+$match.Groups[2].Value})
    }
    $text
}
function Set-PublicBuildVersion([string]$ConfigurationFile,[int]$X,[int]$Y) {[IO.File]::WriteAllText($ConfigurationFile,(Update-PublicBuildText ([IO.File]::ReadAllText($ConfigurationFile)) $X $Y),[Text.UTF8Encoding]::new($false))}
function Get-LastLocalBuild([string]$StateFile) {
    if(!(Test-Path -LiteralPath $StateFile)){return $null}
    $state=Get-Content -LiteralPath $StateFile -Raw|ConvertFrom-Json
    if($state.versionNumber -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'){throw 'Invalid local build state.'}
    $parts=$state.versionNumber.Split('.')
    if(@($parts|Where-Object {[decimal]$_ -gt 65535}).Count){throw 'Local build component overflow.'}
    [pscustomobject]@{X=[int]$parts[0];Y=[int]$parts[1];Z=[int]$parts[2];VersionNumber=$state.versionNumber}
}
function Get-NextLocalBuild([string]$ConfigurationFile,[string]$StateFile,[ValidateSet('X','Y','Z')][string]$Part='Z') {
    $public=Read-PublicBuildVersion $ConfigurationFile
    $last=Get-LastLocalBuild $StateFile
    $x=$public.X;$y=$public.Y;$z=0
    if($Part -eq 'X'){$x++;$y=0}
    elseif($Part -eq 'Y'){if($last -and $last.X -ne $x){$y=0};$y++}
    else{if($last -and $last.X -eq $x -and $last.Y -eq $y){$z=$last.Z};$z++}
    if($x -gt 65535 -or $y -gt 65535 -or $z -gt 65535){throw 'Build component exceeds the Windows version resource limit.'}
    [pscustomobject]@{X=$x;Y=$y;Z=$z;Part=$Part;VersionNumber="$x.$y.$z"}
}
function Complete-LocalBuild([string]$StateFile,$Build) {
    $directory=Split-Path -Parent $StateFile
    New-Item -ItemType Directory -Force -Path $directory|Out-Null
    $temporary=Join-Path $directory ([guid]::NewGuid().ToString('N')+'.tmp')
    try{
        @{versionNumber=$Build.VersionNumber;builtAtUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath $temporary -Encoding utf8
        Move-Item -LiteralPath $temporary -Destination $StateFile -Force
    }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
}
