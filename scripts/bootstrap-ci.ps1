[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if(!$IsWindows){throw 'Windows is required.'}
if($PSVersionTable.PSVersion -ge [version]'7.6' -and [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.RuntimeInformation]::OSArchitecture){return}
$arch=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$hashes=@{x64='9e725837af682b87bb212cd1efe3657c06c540404203810857ec2516ae2ca322';arm64='c2ac26385210ebff5b0696bfb26bc74a9867fd9d561a75ea52635e553974f0cb'}
if(!$hashes.ContainsKey($arch)){throw 'Unsupported PowerShell host architecture.'}
$directory=Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/tools/pwsh-7.6.0'
$archive=$directory+'.zip'
New-Item -ItemType Directory -Force -Path $directory|Out-Null
Invoke-WebRequest -Uri "https://github.com/PowerShell/PowerShell/releases/download/v7.6.0/PowerShell-7.6.0-win-$arch.zip" -OutFile $archive
if((Get-FileHash -LiteralPath $archive).Hash -ine $hashes[$arch]){throw 'PowerShell archive checksum mismatch.'}
Expand-Archive -LiteralPath $archive -DestinationPath $directory -Force
if(!$env:GITHUB_PATH){throw 'Run the remaining steps with the prepared PowerShell executable.'}
$directory|Out-File -LiteralPath $env:GITHUB_PATH -Append -Encoding utf8
