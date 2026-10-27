[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(!$IsWindows -or $PSVersionTable.PSVersion -lt [version]'7.6'){throw 'CI requires Windows and PowerShell 7.6 or later.'}
foreach($command in 'dotnet','cmake'){if(!(Get-Command $command -ErrorAction SilentlyContinue)){throw "Required build tool is missing: $command"}}
$sdk=& dotnet --version
if($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.'){throw 'The selected SDK must be .NET 10.'}
$compiler=$env:NSIS_COMPILER
if(!$compiler){$compiler=Join-Path $projectRoot 'artifacts/tools/nsis-3.12/makensis.exe'}
if(!(Test-Path -LiteralPath $compiler)){
    $tools=Join-Path $projectRoot 'artifacts/tools'
    New-Item -ItemType Directory -Force -Path $tools|Out-Null
    $archive=Join-Path $tools 'nsis-3.12.zip'
    $sha256='56581F90DB321581C5381193D796FFFCF2D24B2F8FED2160A6C6A3BAA67F2C4F'
    if(!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $sha256){
        Invoke-WebRequest -Uri 'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.12/nsis-3.12.zip' -OutFile $archive
        if((Get-FileHash -LiteralPath $archive).Hash -ne $sha256){
            # SourceForge can return a download page containing a signed mirror URL.
            $response=[IO.File]::ReadAllBytes($archive)
            if($response.Length -lt 1MB -and $response.Length -gt 0 -and $response[0] -eq 60){
                $page=[Text.Encoding]::UTF8.GetString($response)
                $url=[Net.WebUtility]::HtmlDecode([regex]::Match($page,'https://downloads\.sourceforge\.net/project/nsis/NSIS%203/3\.12/nsis-3\.12\.zip\?[^"\s<>]+').Value)
                if($url){Invoke-WebRequest -Uri $url -OutFile $archive}
            }
        }
    }
    if((Get-FileHash -LiteralPath $archive).Hash -ne $sha256){throw 'NSIS archive checksum mismatch.'}
    Expand-Archive -LiteralPath $archive -DestinationPath $tools -Force
    $compiler=Join-Path $tools 'nsis-3.12/makensis.exe'
}
$version=& $compiler /VERSION
if($LASTEXITCODE -ne 0 -or $version -notmatch '^v3\.(\d+)' -or [int]$Matches[1] -lt 12){throw 'NSIS 3.12 or later is required.'}
if($env:GITHUB_ENV){"NSIS_COMPILER=$compiler"|Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8}
Write-Host "Build tools: .NET $sdk, PowerShell $($PSVersionTable.PSVersion), NSIS $version"
