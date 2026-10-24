[CmdletBinding()]
param([string]$PublishDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish/win-x64'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot ('artifacts/tests/' + [guid]::NewGuid().ToString('N'))
$installation = Join-Path $testRoot '程序 目录'
$dataRoot = Join-Path $testRoot '用户 数据'
$cli = Join-Path $installation 'bin/pathman.exe'
$probe = Join-Path $projectRoot 'tests/shim-probe.ps1'
$pwsh = (Get-Process -Id $PID).Path
$crossVolumeRoot = $null

function Assert([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Invoke-Program([string]$Executable, [string[]]$Arguments, [string]$InputText = '') {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.WorkingDirectory = $testRoot
    $start.Environment['PATHMAN_TEST_SENTINEL'] = 'PathMan-测试'
    # Use the SDK's runtime for isolated framework-dependent tests, without modifying system registration.
    $sdkRuntime = Split-Path -Parent (Get-Command dotnet).Source
    $start.Environment['DOTNET_ROOT'] = $sdkRuntime
    $start.Environment['DOTNET_ROOT_X64'] = $sdkRuntime
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText)
        $process.StandardInput.Close()
        if (!$process.WaitForExit(15000)) {
            $process.Kill($true)
            throw "Process timed out: $Executable"
        }
        if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) {
            throw "Process exited but its output pipes stayed open: $Executable"
        }
        return [pscustomobject]@{ Code = $process.ExitCode; Out = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

function Invoke-Cli([string[]]$Command, [int]$ExpectedCode = 0) {
    $result = Invoke-Program $cli (@('--data-root', $dataRoot) + $Command)
    Assert ($result.Code -eq $ExpectedCode) "CLI failed ($($result.Code)): $Command $($result.Error)"
    return $result
}

try {
    New-Item -ItemType Directory -Path $installation -Force | Out-Null
    Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $installation -Recurse
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $installation 'bin')).Count -eq 1) 'bin must contain only the CLI EXE.'
    Assert (@(Get-ChildItem -LiteralPath $installation -Recurse -Filter '*.exe').Count -eq 4) 'Exactly four production EXEs are required.'
    $layout = Invoke-Cli @('layout')
    Assert ($layout.Out.Contains($installation)) 'CLI installation discovery failed.'
    Assert ($layout.Out.Contains($dataRoot)) 'CLI data directory override failed.'
    $name = "p'w'sh-中文"
    $null = Invoke-Cli @('shim', 'add', $name, $pwsh)
    $alias = Join-Path $dataRoot "shims/$name.exe"
    $template = Join-Path $installation 'internal/PathMan.Shim.exe'
    $links = & fsutil hardlink list $alias
    Assert ($LASTEXITCODE -eq 0 -and ($links -join "`n").Contains('internal\PathMan.Shim.exe')) 'Alias must be a hard link to the shim template.'
    $samples = @('含空格的 参数', '', 'quote"inside', 'C:\trailing space\', "single'quote", 'a\"b')
    $result = Invoke-Program $alias (@('-NoProfile', '-NonInteractive', '-File', $probe) + $samples) "stdin-data`n"
    Assert ($result.Code -eq 37) "Child exit code was lost: $($result.Error)"
    $observed = $result.Out | ConvertFrom-Json
    Assert (($observed.Arguments | ConvertTo-Json -Compress) -ceq ($samples | ConvertTo-Json -Compress)) 'Argument forwarding changed values.'
    Assert ($observed.WorkingDirectory -eq $testRoot) 'Working directory was not inherited.'
    Assert ($observed.Environment -ceq 'PathMan-测试') 'Environment was not inherited.'
    Assert ($observed.StandardInput -ceq "stdin-data`n") 'Redirected stdin was not inherited.'
    $null = Invoke-Cli @('shim', 'add', $name, $pwsh) 1
    foreach ($invalid in @('../escape', 'NUL', 'COM1.txt', 'LPT¹', 'trailing.')) {
        $null = Invoke-Cli @('shim', 'add', $invalid, $pwsh) 1
    }
    $null = Invoke-Cli @('shim', 'add', 'recursive', $alias) 1
    # Replacing the template does not update existing hard links until refresh.
    $replacement = Join-Path $installation 'internal/replacement.exe'
    Copy-Item -LiteralPath $template -Destination $replacement
    Move-Item -LiteralPath $replacement -Destination $template -Force
    $null = Invoke-Cli @('shim', 'refresh')
    $links = & fsutil hardlink list $alias
    Assert (($links -join "`n").Contains('internal\PathMan.Shim.exe')) 'Refresh must link aliases to the new template.'
    $listing = Invoke-Cli @('shim', 'list')
    Assert ($listing.Out.Contains($name)) 'Alias listing failed.'
    $null = Invoke-Cli @('shim', 'remove', $name)
    Assert (!(Test-Path -LiteralPath $alias)) 'Removing an alias left its EXE behind.'
    Assert (!(Test-Path -LiteralPath (Join-Path $dataRoot "shims/$name.shim"))) 'Removing an alias left its manifest behind.'
    $missing = Invoke-Program $template @()
    Assert ($missing.Code -eq 13) 'A missing manifest should fail clearly.'
    $null = Invoke-Cli @('startup', 'status')
    $null = Invoke-Cli @('background', 'start')
    $status = Invoke-Cli @('background', 'status')
    Assert ($status.Out.Contains('正在运行')) 'Background process did not become ready.'
    $duplicate = Invoke-Program (Join-Path $installation 'internal/PathMan.Service.exe') @()
    Assert ($duplicate.Code -eq 0) 'A duplicate background process should exit.'
    $null = Invoke-Cli @('background', 'stop')
    $status = Invoke-Cli @('background', 'status')
    Assert ($status.Out.Contains('未运行')) 'Background process did not stop.'
    $null = Invoke-Cli @('background', 'start')
    $null = Invoke-Cli @('background', 'stop')

    if ([IO.Path]::GetPathRoot($testRoot) -ne [IO.Path]::GetPathRoot([IO.Path]::GetTempPath())) {
        $crossVolumeRoot = Join-Path ([IO.Path]::GetTempPath()) ('PathMan-test-' + [guid]::NewGuid().ToString('N'))
        $crossVolume = Invoke-Program $cli @('--data-root', $crossVolumeRoot, 'shim', 'add', 'cross-volume', $pwsh)
        Assert ($crossVolume.Code -eq 0) "Cross-volume deployment failed: $($crossVolume.Error)"
        $cached = Join-Path $crossVolumeRoot 'internal/PathMan.Shim.exe'
        $crossAlias = Join-Path $crossVolumeRoot 'shims/cross-volume.exe'
        $firstLinks = @(& fsutil hardlink list $crossAlias)
        Assert (($firstLinks -join "`n").Contains('internal\PathMan.Shim.exe')) 'Cross-volume aliases must link to one local cached template.'
        Assert ((Get-FileHash $cached).Hash -eq (Get-FileHash $template).Hash) 'The cached template must match the installation.'
        $second = Invoke-Program $cli @('--data-root', $crossVolumeRoot, 'shim', 'add', 'cross-volume-two', $pwsh)
        Assert ($second.Code -eq 0) 'A second cross-volume alias must reuse the template.'
        $links = @(& fsutil hardlink list $crossAlias)
        Assert ($links.Count -eq 3 -and ($links -join "`n").Contains('cross-volume-two.exe')) 'Both aliases must share the same cached file.'
        $result = Invoke-Program $crossAlias (@('-NoProfile', '-NonInteractive', '-File', $probe) + $samples) "stdin-data`n"
        Assert ($result.Code -eq 37 -and (($result.Out|ConvertFrom-Json).Arguments|ConvertTo-Json -Compress) -ceq ($samples|ConvertTo-Json -Compress)) 'Copied shims must preserve arguments and exit codes.'
        [IO.File]::AppendAllText($template, 'PathMan-test-update')
        $refreshed = Invoke-Program $cli @('--data-root', $crossVolumeRoot, 'shim', 'refresh')
        $cachedHash=(Get-FileHash $cached).Hash; $templateHash=(Get-FileHash $template).Hash
        Assert ($refreshed.Code -eq 0 -and $cachedHash -eq $templateHash) "Refresh must update a changed cached template. Exit=$($refreshed.Code); cached=$cachedHash; template=$templateHash; stderr=$($refreshed.Error)"
        Assert (@(& fsutil hardlink list $crossAlias).Count -eq 3) 'Refresh must relink both aliases to the updated copy.'
        $result = Invoke-Program $crossAlias @('-NoProfile', '-NonInteractive', '-Command', 'exit 23')
        Assert ($result.Code -eq 23) 'A refreshed copied shim must remain executable.'
    }
    Write-Host 'PASS: layout, hard links, Unicode aliases, arguments, stdio, exit codes, validation, refresh, background lifecycle, cross-volume cache reuse and updates.'
}
finally {
    if (Test-Path -LiteralPath $cli) { $null = Invoke-Program $cli @('background', 'stop') }
    $allowedTests = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/tests')) + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($testRoot).StartsWith($allowedTests, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($crossVolumeRoot) {
        $allowedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ([IO.Path]::GetFullPath($crossVolumeRoot).StartsWith($allowedTemp, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $crossVolumeRoot).StartsWith('PathMan-test-')) {
            Remove-Item -LiteralPath $crossVolumeRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
