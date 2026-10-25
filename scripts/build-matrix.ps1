[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'arm64')][string[]]$Arch = @('x86', 'x64', 'arm64'),
    [ValidateSet('FrameworkDependent', 'SelfContained')][string[]]$PackageType = @('FrameworkDependent', 'SelfContained'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [switch]$SkipInstaller,
    [switch]$Test,
    [switch]$Plan
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$Arch = @($Arch | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
$PackageType = @($PackageType | ForEach-Object {
    if ($_ -ieq 'SelfContained') { 'SelfContained' } else { 'FrameworkDependent' }
} | Select-Object -Unique)
$matrix = foreach ($cpu in $Arch) {
    foreach ($kind in $PackageType) {
        $suffix = if ($kind -eq 'SelfContained') { '-SelfContained' } else { '' }
        [pscustomobject]@{ Arch = $cpu; Type = $kind; File = "PathMan-$Version-$cpu$suffix.exe" }
    }
}
if ($Plan) { $matrix | Format-Table -AutoSize; return }

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}
function Reset-PublishDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/publish')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid publish path: $resolved" }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $resolved 'bin'),(Join-Path $resolved 'internal') | Out-Null
}

Invoke-Checked 'dotnet' @('build', (Join-Path $projectRoot 'PathMan.slnx'), '-c', $Configuration, "-p:Version=$Version")
if ($Test) {
    Invoke-Checked 'dotnet' @('run', '--project', (Join-Path $projectRoot 'tests/Pathman.Core.Tests'), '-c', $Configuration, '--no-build')
}
foreach ($cpu in $Arch) {
    $runtime = "win-$cpu"
    $nativeBuild = Join-Path $projectRoot "artifacts/native-$runtime"
    $cmakeArch = switch ($cpu) { 'arm64' { 'ARM64' } 'x86' { 'Win32' } 'x64' { 'x64' } }
    Invoke-Checked 'cmake' @('-S', (Join-Path $projectRoot 'src/Pathman.Shim'), '-B', $nativeBuild, '-A', $cmakeArch)
    Invoke-Checked 'cmake' @('--build', $nativeBuild, '--config', $Configuration)
    foreach ($kind in $PackageType) {
        $output = Join-Path $projectRoot "artifacts/publish/$runtime/$kind"
        Reset-PublishDirectory $output
        $selfContained = ($kind -eq 'SelfContained').ToString().ToLowerInvariant()
        foreach ($project in @(
            @{ Name = 'Gui'; Destination = $output },
            @{ Name = 'Cli'; Destination = (Join-Path $output 'bin') },
            @{ Name = 'Service'; Destination = (Join-Path $output 'internal') }
        )) {
            Invoke-Checked 'dotnet' @('publish', (Join-Path $projectRoot "src/Pathman.$($project.Name)/Pathman.$($project.Name).csproj"),
                '-c', $Configuration, '-r', $runtime, '--self-contained', $selfContained,
                '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
                '-p:DebugType=None', "-p:Version=$Version", '-o', $project.Destination)
        }
        Copy-Item -LiteralPath (Join-Path $nativeBuild "$Configuration/PathMan.Shim.exe") -Destination (Join-Path $output 'internal/PathMan.Shim.exe')
        Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $output
        Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') -Destination $output
        Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $output -Recurse
        $hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        if ($Test -and $cpu -eq $hostArch) {
            & (Join-Path $PSScriptRoot 'test.ps1') -PublishDirectory $output
        } elseif ($Test) { Write-Host "Runtime tests for $cpu require a compatible test machine." }
        Write-Host "Published $cpu / $kind to $output"
    }
}
if (!$SkipInstaller) {
    & (Join-Path $PSScriptRoot 'package.ps1') -Arch $Arch -PackageType $PackageType -Configuration $Configuration -Version $Version
}
