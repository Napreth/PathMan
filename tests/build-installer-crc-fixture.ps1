[CmdletBinding()]
param([ValidateRange(128,1024)][int]$Megabytes=512)
$ErrorActionPreference='Stop'
# A large, incompressible, skipped payload makes the startup CRC dialog
# reproducible on fast machines. This package is never a release artifact.
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root 'artifacts/installer-crc-fixture'
New-Item -ItemType Directory -Path $output -Force|Out-Null
$payload=Join-Path $output 'random.bin'
$stream=[IO.File]::Create($payload)
try {
    $buffer=[byte[]]::new(1MB)
    for($i=0;$i -lt $Megabytes;$i++){
        [Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
        $stream.Write($buffer,0,$buffer.Length)
    }
}finally{$stream.Dispose()}
. (Join-Path $root 'scripts/version.ps1')
$version=Get-BuildVersion ''
$compiler=Join-Path $root 'artifacts/tools/nsis-3.12/makensis.exe'
$publish=Join-Path $root 'artifacts/publish/win-x64/SelfContained'
$helper=Join-Path $root 'artifacts/native-setup/Release/PathMan.SetupActions.dll'
$icon=Join-Path $root 'src/Pathman.Gui/Assets/PathMan.ico'
$source=[IO.File]::ReadAllText((Join-Path $root 'packaging/PathMan.nsi')).Replace('${__FILEDIR__}\..\src\Pathman.Gui\Assets\PathMan.ico',$icon)
$source+="`nSection`n  Goto SkipCrcFixture`n  File `"$payload`"`n  SkipCrcFixture:`nSectionEnd`n"
$script=Join-Path $output 'fixture.nsi'
[IO.File]::WriteAllText($script,$source)
& $compiler /V2 /INPUTCHARSET UTF8 /DARCH=x64 /DPACKAGE_TYPE=SelfContained "/DVERSION=$($version.Version)" "/DVERSION_NUMERIC=$($version.Numeric)" "/DPUBLISH_DIR=$publish" "/DHELPER_DLL=$helper" "/DOUTPUT_FILE=$output/PathMan-crc-test.exe" $script
if($LASTEXITCODE -ne 0){throw 'CRC fixture compilation failed. Build the x64 SelfContained package first.'}
Write-Host "Run tests/installer-uac-window.ps1 with $output/PathMan-crc-test.exe in a disposable interactive VM."
