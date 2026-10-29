$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if(!(Test-Path -LiteralPath $csc)){throw "Windows .NET Framework C# compiler is required: $csc"}
$git=Get-Command git.exe -ErrorAction Stop
$bash=Join-Path (Split-Path (Split-Path $git.Source)) 'bin/bash.exe'
if(!(Test-Path -LiteralPath $bash)){throw "Git Bash is required for shell integration tests: $bash"}
$fixture=Join-Path $root ('artifacts/shell-tests/space '+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixture|Out-Null
Copy-Item (Join-Path $root 'scripts/shell/*') $fixture
$source=@'
using System;using System.IO;using System.Text;
class Fixture {static int Main(string[] args){try{Console.OutputEncoding=new UTF8Encoding(false);}catch(IOException){Console.SetOut(new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true});}if(Environment.GetEnvironmentVariable("PATHMAN_TEST_DELAY")=="1")System.Threading.Thread.Sleep(200);if(args.Length==4&&args[3]=="--resolve"){Console.WriteLine(Environment.GetEnvironmentVariable("PATHMAN_TEST_JSON"));return 0;}if(Environment.GetEnvironmentVariable("PATHMAN_TEST_FAIL")=="1")return 1;File.AppendAllText(Environment.GetEnvironmentVariable("PATHMAN_TEST_RECORD"),"reg\n");return 0;}}
'@
[IO.File]::WriteAllText((Join-Path $fixture 'fixture.cs'),$source)
& $csc /nologo "/out:$fixture\pathman.exe" (Join-Path $fixture 'fixture.cs')
if($LASTEXITCODE){throw 'Fixture compilation failed'}
& $csc /nologo "/out:$fixture\console-probe.exe" (Join-Path $PSScriptRoot 'shell-console-probe.cs')
if($LASTEXITCODE){throw 'Console probe compilation failed'}
$saved=@{}
foreach($name in 'PATH','PATHMAN_TEST_HOME','PATHMAN_TEST_JSON','PATHMAN_TEST_RECORD','PATHMAN_TEST_FAIL','PATHMAN_TEST_DELAY'){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
function Assert($value,[string]$message){if(!$value){throw $message}}
try{
    $env:PATHMAN_TEST_RECORD=Join-Path $fixture 'writes.txt'
    $value='D:\工具 & !literal% ^ "quoted" ''apostrophe'' $(printf INJECTED)'
    $env:PATHMAN_TEST_JSON=@{Name='PATHMAN_TEST_HOME';Value=$value}|ConvertTo-Json -Compress
    foreach($invokeSwitch in @(
        {& (Join-Path $fixture 'Switch-PathMan.ps1') java 25},
        {& (Join-Path $fixture 'Switch-PathMan.ps1') -Name java -Version 25},
        {& (Join-Path $fixture 'Switch-PathMan.ps1') java 25 -Scope shell}
    )){
        $env:PATHMAN_TEST_HOME='C:\old';$env:PATH='C:\old\bin;'+$saved.PATH
        & $invokeSwitch
        Assert ($env:PATHMAN_TEST_HOME -ceq $value -and $env:PATH.StartsWith($value+'\bin;')) 'PowerShell default and explicit shell scope update the current process and PATH'
        Assert (!(Test-Path $env:PATHMAN_TEST_RECORD)) 'PowerShell default and explicit shell scope never persist the variable'
    }
    $env:PATHMAN_TEST_HOME='C:\old';$env:PATH='C:\old\bin;'+$saved.PATH
    & (Join-Path $fixture 'Switch-PathMan.ps1') -Name java -Version 25 -Scope reg
    Assert ((Get-Content $env:PATHMAN_TEST_RECORD).Count -eq 1 -and $env:PATHMAN_TEST_HOME -eq 'C:\old') 'Registry scope delegates exactly once and leaves the caller unchanged'
    $env:PATHMAN_TEST_HOME='C:\old';$env:PATH='C:\old\bin;'+$saved.PATH
    $env:PATHMAN_TEST_FAIL='1'
    $failed=$false;try{& (Join-Path $fixture 'Switch-PathMan.ps1') java 25 -Scope all}catch{$failed=$true}
    Assert ($failed -and $env:PATHMAN_TEST_HOME -eq 'C:\old') 'Failed persistent writes leave the current shell unchanged'
    $env:PATHMAN_TEST_FAIL=''
    & (Join-Path $fixture 'Switch-PathMan.ps1') java 25 -Scope all
    Assert ($env:PATHMAN_TEST_HOME -ceq $value -and (Get-Content $env:PATHMAN_TEST_RECORD).Count -eq 2) 'All scope changes both targets'
    $env:PATHMAN_TEST_OUTPUT=Join-Path $fixture 'cmd.txt'
    foreach($cmdArguments in 'java 25','/n java /v 25','/n java /v 25 /s shell'){
        $env:PATHMAN_TEST_HOME='C:\old';$env:PATH='C:\old\bin;'+$saved.PATH
        $cmd="@echo off`r`ncall `"$fixture\switch-pathman.cmd`" $cmdArguments`r`nif errorlevel 1 exit /b 1`r`npowershell.exe -NoProfile -Command `"[IO.File]::WriteAllText(`$env:PATHMAN_TEST_OUTPUT,(`$env:PATHMAN_TEST_HOME+[char]10+`$env:PATH))`"`r`n"
        [IO.File]::WriteAllText((Join-Path $fixture 'run.cmd'),$cmd,[Text.Encoding]::ASCII)
        & $env:ComSpec /d /v:off /c (Join-Path $fixture 'run.cmd')
        $observed=[IO.File]::ReadAllText($env:PATHMAN_TEST_OUTPUT).Split("`n",2)
        Assert ($LASTEXITCODE -eq 0 -and $observed[0] -ceq $value -and $observed[1].StartsWith($value+'\bin;')) 'CMD default and explicit shell scope update the caller and PATH with literal special characters'
        Assert ((Get-Content $env:PATHMAN_TEST_RECORD).Count -eq 2) 'CMD default and explicit shell scope never persist the variable'
    }
    $env:PATHMAN_TEST_DELAY='1'
    foreach($consoleCodePages in @(@(936,936),@(65001,65001),@(437,437),@(936,65001),@(65001,936))){
        $env:PATHMAN_TEST_HOME='C:\old';$env:PATH='C:\old\bin;'+$saved.PATH
        $consoleCase=$consoleCodePages -join '-'
        $consoleResult=Join-Path $fixture "console-result-$consoleCase.json"
        $console=Start-Process -FilePath (Join-Path $fixture 'console-probe.exe') -ArgumentList ('"'+(Join-Path $fixture 'run.cmd')+'" "'+$consoleResult+'" '+($consoleCodePages -join ' ')) -WindowStyle Hidden -PassThru -Wait
        try{Assert ($console.ExitCode -eq 0) 'CMD console probe succeeds'}finally{$console.Dispose()}
        $observedConsole=Get-Content -LiteralPath $consoleResult -Raw|ConvertFrom-Json
        $expectedCodePages=$consoleCodePages -join '/'
        Assert ($observedConsole.HistoryPreserved -and $observedConsole.CodePages[-1] -eq $expectedCodePages) "CMD preserves screen history and returns the $expectedCodePages code pages"
        if($consoleCodePages[1] -ne 437){Assert ($observedConsole.CodePages.Count -eq 1) "CMD never changes the shared $expectedCodePages code pages when they can represent the value"}
        $observed=[IO.File]::ReadAllText($env:PATHMAN_TEST_OUTPUT).Split("`n",2)
        Assert ($observed[0] -ceq $value -and $observed[1].StartsWith($value+'\bin;')) "CMD preserves Unicode and literal values with code pages $expectedCodePages"
    }
    $env:PATHMAN_TEST_DELAY=''
    $env:PATHMAN_TEST_SCRIPT=Join-Path $fixture 'switch-pathman'
    $env:PATHMAN_TEST_OUTPUT=Join-Path $fixture 'bash.txt'
    foreach($bashArguments in 'java 25','--name java --version 25','--name java --version 25 --scope shell'){
        $env:PATHMAN_TEST_HOME='C:\old';$env:PATH='C:\old\bin;'+$saved.PATH
        $bashCommand='. "$(cygpath -u "$PATHMAN_TEST_SCRIPT")" '+$bashArguments+' || exit 1; expected=$(cygpath -u -- "$PATHMAN_TEST_HOME")/bin; [[ ":$PATH:" == *":$expected:"* && ":$PATH:" != *":/c/old/bin:"* ]] || exit 1; printf "%s" "$PATHMAN_TEST_HOME" > "$(cygpath -u "$PATHMAN_TEST_OUTPUT")"'
        & $bash -c $bashCommand
        Assert ($LASTEXITCODE -eq 0 -and [IO.File]::ReadAllText($env:PATHMAN_TEST_OUTPUT) -ceq $value) 'Sourced Git Bash default and explicit shell scope update the caller and PATH with literal special characters'
        Assert ((Get-Content $env:PATHMAN_TEST_RECORD).Count -eq 2) 'Git Bash default and explicit shell scope never persist the variable'
    }
    foreach($scope in 'reg','all'){
        $env:PATHMAN_TEST_HOME='C:\old';$env:PATHMAN_TEST_SCOPE=$scope
        $command="@echo off`r`ncall `"$fixture\switch-pathman.cmd`" java 25 /s $scope`r`nif errorlevel 1 exit /b 1`r`npowershell.exe -NoProfile -Command `"[IO.File]::WriteAllText(`$env:PATHMAN_TEST_OUTPUT,`$env:PATHMAN_TEST_HOME)`"`r`n"
        [IO.File]::WriteAllText((Join-Path $fixture 'run.cmd'),$command,[Text.Encoding]::ASCII)
        & $env:ComSpec /d /v:off /c (Join-Path $fixture 'run.cmd')
        $expected=if($scope -eq 'reg'){'C:\old'}else{$value}
        Assert ($LASTEXITCODE -eq 0 -and [IO.File]::ReadAllText($env:PATHMAN_TEST_OUTPUT) -ceq $expected) "CMD positional arguments and $scope scope"
        & $bash -c '. "$(cygpath -u "$PATHMAN_TEST_SCRIPT")" java 25 -s "$PATHMAN_TEST_SCOPE" || exit; printf "%s" "$PATHMAN_TEST_HOME" > "$(cygpath -u "$PATHMAN_TEST_OUTPUT")"'
        Assert ($LASTEXITCODE -eq 0 -and [IO.File]::ReadAllText($env:PATHMAN_TEST_OUTPUT) -ceq $expected) "Bash positional arguments and $scope scope"
    }
    Write-Host 'PASS: PowerShell, CMD and sourced Git Bash default/explicit scopes, positional/named arguments, PATH updates, literal values and CMD console history/code-page preservation.'
}finally{
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    Remove-Item Env:PATHMAN_TEST_OUTPUT,Env:PATHMAN_TEST_SCRIPT,Env:PATHMAN_TEST_SCOPE -ErrorAction SilentlyContinue
}
