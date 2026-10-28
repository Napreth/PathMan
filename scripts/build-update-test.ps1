[CmdletBinding()]
param(
    [ValidateSet('x86','x64','arm64')][string]$Arch=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant(),
    [ValidateSet('FrameworkDependent','SelfContained')][string]$PackageType='SelfContained',
    [switch]$SkipReleaseBuild
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')
$version=(Get-BuildVersion '').Version
if(!$SkipReleaseBuild){& (Join-Path $PSScriptRoot 'build-matrix.ps1') -Arch $Arch -PackageType $PackageType -Version $version}
$suffix=if($PackageType -eq 'SelfContained'){'-SelfContained'}else{''}
$fixture=Join-Path $projectRoot "artifacts/setup/PathMan-$version-$Arch$suffix.exe"
$published=Join-Path $projectRoot "artifacts/publish/win-$Arch/$PackageType"
if(!(Test-Path $fixture) -or !(Test-Path (Join-Path $published 'PathMan.exe'))){throw 'Build the release package before building the update test application.'}
$testRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/update-test'))
$application=[IO.Path]::GetFullPath((Join-Path $testRoot 'app'))
if(!$application.StartsWith($testRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid update test output directory.'}
if(Test-Path $application){Remove-Item -LiteralPath $application -Recurse -Force}
New-Item -ItemType Directory $application -Force|Out-Null
Get-ChildItem -LiteralPath $published|Copy-Item -Destination $application -Recurse -Force
$selfContained=($PackageType -eq 'SelfContained').ToString().ToLowerInvariant()
& dotnet publish (Join-Path $projectRoot 'src/Pathman.Gui/Pathman.Gui.csproj') -c Release -r "win-$Arch" --self-contained $selfContained `
    --artifacts-path (Join-Path $testRoot 'build') -p:UpdateTestBuild=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None "-p:PathManVersionName=$version" -o $application
if($LASTEXITCODE -ne 0){throw 'Update test publish failed.'}
Copy-Item -LiteralPath $fixture -Destination (Join-Path $application 'UpdateTestInstaller.exe') -Force
$releaseDll=Join-Path $projectRoot "src/Pathman.Gui/bin/Release/net10.0-windows10.0.19041.0/win-$Arch/PathMan.dll"
$testDll=Get-ChildItem (Join-Path $testRoot 'build/bin') -Recurse -Filter PathMan.dll|Sort-Object LastWriteTime -Descending|Select-Object -First 1
$marker='UpdateTestReleaseSource'
if(!(Test-Path $releaseDll) -or [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($releaseDll)).Contains($marker)){throw 'The release assembly must not contain the update mock.'}
if(!$testDll -or ![Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($testDll.FullName)).Contains($marker)){throw 'The test assembly does not contain the update mock.'}
@{Architecture=$Arch;PackageType=$PackageType;ReleaseContainsMock=$false;TestContainsMock=$true;FixtureSHA256=(Get-FileHash $fixture).Hash}|ConvertTo-Json|Set-Content (Join-Path $testRoot 'build-verification.json') -Encoding UTF8
Write-Host "Update test application: $(Join-Path $application 'PathMan.exe')"
