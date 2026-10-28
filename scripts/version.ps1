function Get-BuildVersion([string]$VersionName='',[string]$VersionNumber='',[string]$ReleaseType='',[string]$ConfigurationFile=(Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props')) {
    [xml]$properties=Get-Content -LiteralPath $ConfigurationFile -Raw
    $configuredName=$properties.SelectSingleNode('//PathManVersionName').InnerText
    if(!$VersionName){$VersionName=$configuredName}
    if(!$VersionNumber){$VersionNumber=$properties.SelectSingleNode('//PathManVersionX').InnerText+'.'+$properties.SelectSingleNode('//PathManVersionY').InnerText+'.0'}
    if($VersionName -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z'){throw 'VersionName must be a SemVer name, such as 1.0.0-beta.1.'}
    $prereleaseName=$Matches[4]
    if($prereleaseName -and @($prereleaseName.Split('.')|Where-Object {$_ -match '^0\d+$'}).Count){throw 'Numeric prerelease name identifiers cannot have leading zeroes.'}
    if($VersionNumber -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\z'){throw 'VersionNumber must contain X.Y.Z.'}
    $numbers=@($Matches[1],$Matches[2],$Matches[3])
    if(@($numbers|Where-Object {[decimal]$_ -gt 65535}).Count){throw 'VersionNumber components cannot exceed 65535 (Windows version resource limit).'}
    if(!$ReleaseType){$ReleaseType=if($VersionName -eq $configuredName){$properties.SelectSingleNode('//PathManReleaseType').InnerText}elseif($prereleaseName){'beta'}else{'official'}}
    if($ReleaseType -cnotin @('official','beta')){throw 'ReleaseType must be official or beta.'}
    $type=if([int]$numbers[2] -gt 0 -or [int]$numbers[1] -gt 0){'development'}else{$ReleaseType}
    [pscustomobject]@{Version=$VersionName;VersionName=$VersionName;VersionNumber=$VersionNumber;Numeric=$VersionNumber;ReleaseType=$ReleaseType;Type=$type;IsLocal=([int]$numbers[2] -gt 0);Prerelease=([int]$numbers[2] -eq 0 -and $type -in @('beta','development'))}
}

function Get-ReleaseInstaller([string]$Path,[ValidateSet('x86','x64','arm64')][string]$Architecture,[bool]$SelfContained) {
    $file=Get-Item -LiteralPath $Path
    if($file.PSIsContainer -or $file.Extension -ine '.exe' -or $file.Length -le 0){throw 'A nonempty installer EXE is required.'}
    [ordered]@{fileName=$file.Name;architecture=$Architecture.ToLowerInvariant();selfContained=$SelfContained;size=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
function Write-ReleaseManifest($Version,[string]$Path,[object[]]$Installers=@()) {
    [ordered]@{versionName=$Version.VersionName;versionNumber=$Version.VersionNumber;type=$Version.Type;installers=@($Installers)} |
        ConvertTo-Json -Depth 5|Set-Content -LiteralPath $Path -Encoding utf8
}
