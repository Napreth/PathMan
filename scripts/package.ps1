[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'arm64')][string[]]$Arch = @('x86', 'x64', 'arm64'),
    [ValidateSet('FrameworkDependent', 'SelfContained')][string[]]$PackageType = @('FrameworkDependent', 'SelfContained'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'version.ps1')
$versionInfo=Get-BuildVersion $Version
$Version=$versionInfo.Version
$projectRoot = Split-Path -Parent $PSScriptRoot
$Arch = @($Arch | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
$PackageType = @($PackageType | ForEach-Object {
    if ($_ -ieq 'SelfContained') { 'SelfContained' } else { 'FrameworkDependent' }
} | Select-Object -Unique)
$compiler = $env:NSIS_COMPILER
if (!$compiler) {
    $candidates = @(
        (Join-Path $projectRoot 'artifacts/tools/nsis-3.12/makensis.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe'),
        (Join-Path $env:ProgramFiles 'NSIS/makensis.exe')
    )
    $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$compiler) { $compiler = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source }
}
if (!$compiler -or !(Test-Path -LiteralPath $compiler)) { throw 'Install NSIS 3.12 or set NSIS_COMPILER to makensis.exe.' }
$compilerVersion = & $compiler /VERSION
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^v3\.(\d+)' -or [int]$Matches[1] -lt 12) {
    throw "NSIS 3.12 or newer is required; found $compilerVersion"
}
$nativeBuild = Join-Path $projectRoot 'artifacts/native-setup'
& cmake -S (Join-Path $projectRoot 'packaging/native') -B $nativeBuild -A Win32
if ($LASTEXITCODE -ne 0) { throw 'Setup helper CMake configuration failed.' }
& cmake --build $nativeBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Setup helper build failed.' }
$helper = Join-Path $nativeBuild "$Configuration/PathMan.SetupActions.dll"
$destination = Join-Path $projectRoot 'artifacts/setup'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($cpu in @($Arch | Select-Object -Unique)) {
    foreach ($kind in @($PackageType | Select-Object -Unique)) {
        $publish = Join-Path $projectRoot "artifacts/publish/win-$cpu/$kind"
        $suffix = if ($kind -eq 'SelfContained') { '-SelfContained' } else { '' }
        $output = Join-Path $destination "PathMan-$Version-$cpu$suffix.exe"
        foreach ($file in @('PathMan.exe','bin/pathman.exe','internal/PathMan.Service.exe','internal/PathMan.Shim.exe','LICENSE')) {
            if (!(Test-Path -LiteralPath (Join-Path $publish $file))) { throw "Missing payload: $publish/$file. Run build.ps1 first." }
        }
        if (@(Get-ChildItem -LiteralPath (Join-Path $publish 'bin') -File).Count -ne 1) { throw 'bin must contain only pathman.exe.' }
        & $compiler /V2 /INPUTCHARSET UTF8 "/DARCH=$cpu" "/DPACKAGE_TYPE=$kind" "/DVERSION=$Version" "/DVERSION_NUMERIC=$($versionInfo.Numeric)" "/DPUBLISH_DIR=$publish" "/DHELPER_DLL=$helper" "/DOUTPUT_FILE=$output" (Join-Path $projectRoot 'packaging/PathMan.nsi')
        if ($LASTEXITCODE -ne 0) { throw "NSIS packaging failed: $cpu / $kind" }
        $payload = @(Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
            [ordered]@{ Path = [IO.Path]::GetRelativePath($publish, $_.FullName); SHA256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
        })
        [ordered]@{ Architecture=$cpu; PackageType=$kind; Version=$Version; Compiler=$compilerVersion; SHA256=(Get-FileHash -LiteralPath $output).Hash; Payload=$payload } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$output.json" -Encoding utf8
        Write-Host "Packaged $output"
    }
}
