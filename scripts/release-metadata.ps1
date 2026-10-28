[CmdletBinding()]
param([Parameter(Mandatory)][string]$Tag,[string]$VersionNumber='',[string]$ReleaseType='',[string]$RepositoryRoot=(Split-Path -Parent $PSScriptRoot),[string]$OutputDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'version.ps1')
function Read-Git([string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new('git');$start.UseShellExecute=$false;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.Encoding]::UTF8
    foreach($argument in (@('-C',$RepositoryRoot)+$Arguments)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{$output=$process.StandardOutput.ReadToEndAsync();$errorText=$process.StandardError.ReadToEndAsync();$process.WaitForExit();if($process.ExitCode -ne 0){throw $errorText.GetAwaiter().GetResult()};$output.GetAwaiter().GetResult()}finally{$process.Dispose()}
}
$ref='refs/tags/'+$Tag
$null=Read-Git @('check-ref-format',$ref)
$version=Get-BuildVersion ($Tag -creplace '^v','') $VersionNumber $ReleaseType
if((Read-Git @('cat-file','-t',$ref)).Trim() -ne 'tag'){throw 'A release requires an annotated tag (git tag -a or git tag -s) with a title and optional release notes.'}
$object=Read-Git @('cat-file','tag',$ref)
$separator=$object.IndexOf("`n`n",[StringComparison]::Ordinal)
if($separator -lt 0){throw 'Invalid annotated tag.'}
$message=$object.Substring($separator+2).Replace("`r`n","`n")
# Git recognizes OpenPGP, SSH and X.509 tag signatures. Do not parse a signature as release prose.
$signature=(Read-Git @('for-each-ref','--format=%(contents:signature)',$ref)).Replace("`r`n","`n").TrimEnd("`n")
if($signature){
    if(!$message.TrimEnd("`n").EndsWith($signature,[StringComparison]::Ordinal)){throw 'The tag signature could not be separated from the message.'}
    $message=$message.Substring(0,$message.LastIndexOf($signature,[StringComparison]::Ordinal))
}
$lines=$message.Split("`n")
if([string]::IsNullOrWhiteSpace($lines[0])){throw 'The first tag message line must contain a release title.'}
$start=1;while($start -lt $lines.Count -and [string]::IsNullOrWhiteSpace($lines[$start])){$start++}
$body=if($start -lt $lines.Count){[string]::Join("`n",$lines[$start..($lines.Count-1)])}else{''}
$metadata=[ordered]@{Tag=$Tag;Version=$version.VersionName;VersionName=$version.VersionName;VersionNumber=$version.VersionNumber;ReleaseType=$version.ReleaseType;Prerelease=$version.Prerelease;Title=($Tag+' - '+$lines[0]);Commit=(Read-Git @('rev-parse',($ref+'^{commit}'))).Trim()}
New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null
$metadata|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'metadata.json') -Encoding utf8
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'notes.md'),$body,[Text.UTF8Encoding]::new($false))
Write-ReleaseManifest $version (Join-Path $OutputDirectory 'release.json')
if($env:GITHUB_OUTPUT){"version=$($version.Version)"|Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8}
Write-Host "Prepared release metadata for $Tag"
