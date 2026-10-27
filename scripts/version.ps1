function Get-BuildVersion([string]$Version) {
    if(!$Version) {
        [xml]$properties=Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props') -Raw
        $Version=[string]$properties.Project.PropertyGroup.Version
    }
    if($Version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z') {throw 'Version must be a SemVer value, such as 1.0.0-beta.1 or 1.0.0+build.42.'}
    $components=@($Matches[1],$Matches[2],$Matches[3]);$prerelease=$Matches[4]
    if(@($components|Where-Object {[decimal]$_ -gt 65535}).Count) {throw 'Version components cannot exceed 65535 (Windows version resource limit).'}
    if($prerelease -and @($prerelease.Split('.')|Where-Object {$_ -match '^0\d+$'}).Count) {throw 'Numeric prerelease identifiers cannot have leading zeroes.'}
    [pscustomobject]@{Version=$Version;Numeric=($components -join '.');Prerelease=[bool]$prerelease}
}
