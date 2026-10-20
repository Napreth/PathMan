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
& (Join-Path $PSScriptRoot 'scripts/build-matrix.ps1') @PSBoundParameters
