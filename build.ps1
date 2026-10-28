[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'arm64')][string[]]$Arch = @('x86', 'x64', 'arm64'),
    [ValidateSet('FrameworkDependent', 'SelfContained')][string[]]$PackageType = @('FrameworkDependent', 'SelfContained'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [Alias('Version')][string]$VersionName = '',
    [string]$VersionNumber = '',
    [ValidateSet('official','beta')][string]$ReleaseType = '',
    [switch]$X,
    [switch]$Y,
    [switch]$Z,
    [switch]$SkipInstaller,
    [switch]$Test,
    [switch]$Plan
)
& (Join-Path $PSScriptRoot 'scripts/build-matrix.ps1') @PSBoundParameters
