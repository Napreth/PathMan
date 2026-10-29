[CmdletBinding()]
param([ValidateSet('x86','x64','arm64')][string]$Architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant(),[string]$PublishDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish/win-x64'),[string]$CoreAssembly = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Pathman.Core/bin/Release/net10.0-windows/Pathman.Core.dll'))
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
    $start.Environment['PATH'] = (Join-Path $testRoot 'search path') + ';' + $env:PATH
    # Use the SDK's runtime for isolated framework-dependent tests, without modifying system registration.
    $sdkRuntime = Split-Path -Parent (Get-Command dotnet).Source
    $start.Environment['DOTNET_ROOT'] = $sdkRuntime
    $hostArchitecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToUpperInvariant()
    $start.Environment['DOTNET_ROOT_'+$hostArchitecture] = $sdkRuntime
    if($Architecture -eq 'x86' -and $hostArchitecture -ne 'X86') {
        $x86Root=[Environment]::GetEnvironmentVariable('DOTNET_ROOT_X86')
        if(!$x86Root){$x86Root=Join-Path ${env:ProgramFiles(x86)} 'dotnet'}
        $start.Environment['DOTNET_ROOT_X86']=$x86Root
    }
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
    $result = Invoke-Program $cli $Command
    Assert ($result.Code -eq $ExpectedCode) "CLI failed ($($result.Code)): $Command $($result.Error)"
    return $result
}
function Assert-Rejected([scriptblock]$Action) {
    try {& $Action} catch {if($_.Exception.GetBaseException() -is [ArgumentException] -or $_.Exception.GetBaseException() -is [IO.IOException]) {return};throw}
    throw 'The operation should have been rejected.'
}

try {
    New-Item -ItemType Directory -Path $installation -Force | Out-Null
    Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $installation -Recurse
    Assert (@(Compare-Object @('pathman.exe','Switch-PathMan.ps1','switch-pathman.cmd','switch-pathman') @(Get-ChildItem -LiteralPath (Join-Path $installation 'bin')|ForEach-Object Name)).Count -eq 0) 'bin contains the CLI and three shell scripts.'
    Assert (@(Get-ChildItem -LiteralPath $installation -Recurse -Filter '*.exe').Count -eq 4) 'Exactly four production EXEs are required.'
    $help=Invoke-Cli @('--help')
    $commands=@([regex]::Matches($help.Out,'(?m)^\s+pathman (\w+)') | ForEach-Object {$_.Groups[1].Value})
    Assert (($commands -join ',') -ceq 'list,add,switch,delete') 'CLI help must contain only version commands.'
    Assert (!$help.Out.Contains('--data-root') -and !$help.Out.Contains('首次 add') -and !$help.Out.Contains('硬链接') -and !$help.Out.Contains('管理员授权')) 'Removed help prose must not remain.'
    foreach($removed in @(@('layout'),@('shim','list'),@('shim','add','sample','x'),@('shim','remove','sample'),@('shim','refresh'),@('startup','status'),@('startup','enable'),@('startup','disable'),@('background','start'),@('background','stop'),@('background','status'),@('--data-root',$dataRoot,'list'),@('--environment-session','invalid'))) {$null=Invoke-Cli $removed 2}
    $null=Invoke-Cli @('add') 1
    $null=Invoke-Cli @('add','sample','1','x','--type','invalid') 1
    $null=Invoke-Cli @('list','one','two') 1
    # Exercise shared APIs directly with temporary directories; production CLI has no test overrides.
    if([Environment]::Version.Major -lt 10){throw 'Runtime tests require PowerShell 7.6 or later (.NET 10).'}
    $null=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CoreAssembly))
    $layout=[Pathman.Core.PathmanLayout]::new($installation,$dataRoot)
    $manager=[Pathman.Core.Shims.ShimManager]::new($layout,$false)
    $background=[Pathman.Core.BackgroundProcess]::new($layout)
    $name = "p'w'sh-中文"
    $null = $manager.Add($name,$pwsh)
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
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'search path') -Force | Out-Null
    Copy-Item -LiteralPath $env:ComSpec -Destination (Join-Path $testRoot 'search path/renamed tool.exe')
    $null=$manager.Add('path-name','renamed tool.exe')
    $resolved=Invoke-Program (Join-Path $dataRoot 'shims/path-name.exe') @('/d','/c','exit 19')
    Assert ($resolved.Code -eq 19) "A bare filename must resolve through the caller's PATH, including paths and filenames with spaces: $($resolved.Error)"
    # A hard link allows identity-based recursion detection after PATH resolution.
    New-Item -ItemType HardLink -Path (Join-Path $testRoot 'search path/loop.exe') -Target $template | Out-Null
    $null=$manager.Add('path-loop','loop.exe')
    $resolved=Invoke-Program (Join-Path $dataRoot 'shims/path-loop.exe') @()
    Assert ($resolved.Code -eq 1059) 'PATH lookup must not bypass shim recursion protection.'
    Assert-Rejected {$manager.Add($name,$pwsh)}
    foreach ($invalid in @('../escape', 'NUL', 'COM1.txt', 'LPT¹', 'trailing.')) {
        Assert-Rejected {$manager.Add($invalid,$pwsh)}
    }
    $null = $manager.Add('recursive',$alias)
    $recursive = Invoke-Program (Join-Path $dataRoot 'shims/recursive.exe') @()
    Assert ($recursive.Code -eq 1059) 'A recursive target must be accepted for storage and rejected when launched.'
    foreach ($rawTarget in @('', 'C:\missing\PathMan-no-target.exe', 'invalid?:<>|path', 'relative-target', '"C:\quoted.exe"')) {
        $null = $manager.Add('invalid-target',$rawTarget)
        $invalidTarget = Invoke-Program (Join-Path $dataRoot 'shims/invalid-target.exe') @()
        Assert ($invalidTarget.Code -ne 0 -and $invalidTarget.Error.Contains('PathMan.Shim:')) 'Invalid targets must fail at invocation, without preventing alias storage.'
        $manager.Remove('invalid-target')
    }
    # Replacing the template does not update existing hard links until refresh.
    $replacement = Join-Path $installation 'internal/replacement.exe'
    Copy-Item -LiteralPath $template -Destination $replacement
    Move-Item -LiteralPath $replacement -Destination $template -Force
    $maintenance = Invoke-Program $layout.BackgroundExecutable @('--installer-refresh-shims',$dataRoot)
    Assert ($maintenance.Code -eq 0) 'Installer shim maintenance must work independently of monitoring preferences.'
    $links = & fsutil hardlink list $alias
    Assert (($links -join "`n").Contains('internal\PathMan.Shim.exe')) "Refresh must link aliases to the new template: $($links -join '; ') $($maintenance.Error)"
    Assert (@($manager.List() | Where-Object Name -eq $name).Count -eq 1) 'Alias listing failed.'
    $active=Start-Process -FilePath $alias -ArgumentList '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 6; exit 29"' -WindowStyle Hidden -PassThru
    try {
        Start-Sleep -Milliseconds 500
        $replacement=$template+'.new'
        Copy-Item -LiteralPath $template -Destination $replacement
        [IO.File]::AppendAllText($replacement,'PathMan-live-update')
        $replace=[Pathman.Core.Shims.ShimManager].Assembly.GetType('Pathman.Core.Shims.ShimFiles').GetMethod('Replace')
        $null=$replace.Invoke($null,[object[]]@([string]$replacement,[string]$template))
        $null=$manager.Refresh()
        Assert (!$active.HasExited) 'Updating and refreshing shims must leave running alias commands alive.'
        Assert ((Get-FileHash $alias).Hash -eq (Get-FileHash $template).Hash) 'New alias launches must use the updated template.'
        Assert ($active.WaitForExit(15000) -and $active.ExitCode -eq 29) 'The old shim must finish normally and retain the target exit code.'
    } finally {if(!$active.HasExited){$active.Kill($true)};$active.Dispose()}
    $manager.Remove($name)
    Assert (!(Test-Path -LiteralPath $alias)) 'Removing an alias left its EXE behind.'
    Assert (!(Test-Path -LiteralPath (Join-Path $dataRoot "shims/$name.shim"))) 'Removing an alias left its manifest behind.'
    $missing = Invoke-Program $template @()
    Assert ($missing.Code -eq 13) 'A missing manifest should fail clearly.'
    Assert-Rejected {$background.Start()}
    Assert (!$background.IsRunning) 'Disabled background must remain stopped.'
    $duplicate = Invoke-Program (Join-Path $installation 'internal/PathMan.Service.exe') @('--data-root', $dataRoot)
    Assert ($duplicate.Code -eq 5) 'A direct disabled background launch must also be refused.'
    $background.Stop()
    Assert (!$background.IsRunning) 'Background process did not stop.'
    Assert-Rejected {$background.Start()}
    $background.Stop()

    if ([IO.Path]::GetPathRoot($testRoot) -ne [IO.Path]::GetPathRoot([IO.Path]::GetTempPath())) {
        $crossVolumeRoot = Join-Path ([IO.Path]::GetTempPath()) ('PathMan-test-' + [guid]::NewGuid().ToString('N'))
        $crossManager=[Pathman.Core.Shims.ShimManager]::new([Pathman.Core.PathmanLayout]::new($installation,$crossVolumeRoot),$false)
        $null=$crossManager.Add('cross-volume',$pwsh)
        $cached = Join-Path $crossVolumeRoot 'internal/PathMan.Shim.exe'
        $crossAlias = Join-Path $crossVolumeRoot 'shims/cross-volume.exe'
        $firstLinks = @(& fsutil hardlink list $crossAlias)
        Assert (($firstLinks -join "`n").Contains('internal\PathMan.Shim.exe')) 'Cross-volume aliases must link to one local cached template.'
        Assert ((Get-FileHash $cached).Hash -eq (Get-FileHash $template).Hash) 'The cached template must match the installation.'
        $null=$crossManager.Add('cross-volume-two',$pwsh)
        $links = @(& fsutil hardlink list $crossAlias)
        Assert ($links.Count -eq 3 -and ($links -join "`n").Contains('cross-volume-two.exe')) 'Both aliases must share the same cached file.'
        $result = Invoke-Program $crossAlias (@('-NoProfile', '-NonInteractive', '-File', $probe) + $samples) "stdin-data`n"
        Assert ($result.Code -eq 37 -and (($result.Out|ConvertFrom-Json).Arguments|ConvertTo-Json -Compress) -ceq ($samples|ConvertTo-Json -Compress)) 'Copied shims must preserve arguments and exit codes.'
        $activeCross=Start-Process -FilePath $crossAlias -ArgumentList '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 6; exit 27"' -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 500
        $updatedTemplate=$template+'.updated'
        Copy-Item -LiteralPath $template -Destination $updatedTemplate
        [IO.File]::AppendAllText($updatedTemplate, 'PathMan-test-update')
        Move-Item -LiteralPath $updatedTemplate -Destination $template -Force
        try {
            $refreshed=$crossManager.Refresh()
            Assert (!$activeCross.HasExited) 'A cross-volume cache refresh must leave the running alias alive.'
            Assert ($activeCross.WaitForExit(15000) -and $activeCross.ExitCode -eq 27) 'A retired copied shim must preserve the target exit code.'
        } finally {if(!$activeCross.HasExited){$activeCross.Kill($true)};$activeCross.Dispose()}
        $cachedHash=(Get-FileHash $cached).Hash; $templateHash=(Get-FileHash $template).Hash
        Assert ($refreshed -eq 2 -and $cachedHash -eq $templateHash) "Refresh must update a changed cached template. Count=$refreshed; cached=$cachedHash; template=$templateHash"
        Assert (@(& fsutil hardlink list $crossAlias).Count -eq 3) 'Refresh must relink both aliases to the updated copy.'
        $result = Invoke-Program $crossAlias @('-NoProfile', '-NonInteractive', '-Command', 'exit 23')
        Assert ($result.Code -eq 23) 'A refreshed copied shim must remain executable.'
    }
    Write-Host 'PASS: version-only CLI, direct Core alias APIs, PATH filename lookup, recursion guards, arguments, stdio, exit codes, live same/cross-volume shim replacement, installer maintenance and disabled background.'
}
finally {
    if ($background) {$background.Stop()}
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
