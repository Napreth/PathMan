$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('artifacts/shell-tests/space '+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixture|Out-Null
Copy-Item (Join-Path $root 'scripts/shell/*') $fixture
$source=@'
using System;using System.IO;using System.Text;
class Fixture {static int Main(string[] args){Console.OutputEncoding=new UTF8Encoding(false);if(args.Length==4&&args[3]=="--resolve"){Console.WriteLine(Environment.GetEnvironmentVariable("PATHMAN_TEST_JSON"));return 0;}if(Environment.GetEnvironmentVariable("PATHMAN_TEST_FAIL")=="1")return 1;File.AppendAllText(Environment.GetEnvironmentVariable("PATHMAN_TEST_RECORD"),"reg\n");return 0;}}
'@
[IO.File]::WriteAllText((Join-Path $fixture 'fixture.cs'),$source)
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo "/out:$fixture\pathman.exe" (Join-Path $fixture 'fixture.cs')
if($LASTEXITCODE){throw 'Fixture compilation failed'}
$saved=@{}
foreach($name in 'PATH','PATHMAN_TEST_HOME','PATHMAN_TEST_JSON','PATHMAN_TEST_RECORD','PATHMAN_TEST_FAIL'){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
function Assert($value,[string]$message){if(!$value){throw $message}}
try{
    $env:PATHMAN_TEST_RECORD=Join-Path $fixture 'writes.txt'
    $value='D:\工具 & !literal% ^ "quoted" ''apostrophe'' $(printf INJECTED)'
    $env:PATHMAN_TEST_JSON=@{Name='PATHMAN_TEST_HOME';Value=$value}|ConvertTo-Json -Compress
    $env:PATHMAN_TEST_HOME='C:\old'
    $env:PATH='C:\old\bin;'+$saved.PATH
    & (Join-Path $fixture 'Switch-PathMan.ps1') java 25 -Scope shell
    Assert ($env:PATHMAN_TEST_HOME -ceq $value -and $env:PATH.StartsWith($value+'\bin;')) 'PowerShell shell scope updates the current process and PATH'
    Assert (!(Test-Path $env:PATHMAN_TEST_RECORD)) 'Shell scope never persists the variable'
    $env:PATHMAN_TEST_HOME='C:\old';$env:PATH='C:\old\bin;'+$saved.PATH
    & (Join-Path $fixture 'Switch-PathMan.ps1') -Name java -Version 25 -Scope reg
    Assert ((Get-Content $env:PATHMAN_TEST_RECORD).Count -eq 1 -and $env:PATHMAN_TEST_HOME -eq 'C:\old') 'Registry scope delegates exactly once and leaves the caller unchanged'
    $missing=$false;try{& (Join-Path $fixture 'Switch-PathMan.ps1') java 25}catch{$missing=$true}
    Assert $missing 'Scope must be explicitly named without prompting'
    $env:PATHMAN_TEST_HOME='C:\old';$env:PATH='C:\old\bin;'+$saved.PATH
    $env:PATHMAN_TEST_FAIL='1'
    $failed=$false;try{& (Join-Path $fixture 'Switch-PathMan.ps1') java 25 -Scope all}catch{$failed=$true}
    Assert ($failed -and $env:PATHMAN_TEST_HOME -eq 'C:\old') 'Failed persistent writes leave the current shell unchanged'
    $env:PATHMAN_TEST_FAIL=''
    & (Join-Path $fixture 'Switch-PathMan.ps1') java 25 -Scope all
    Assert ($env:PATHMAN_TEST_HOME -ceq $value -and (Get-Content $env:PATHMAN_TEST_RECORD).Count -eq 2) 'All scope changes both targets'
    $env:PATHMAN_TEST_HOME='C:\old';$env:PATH='C:\old\bin;'+$saved.PATH
    $env:PATHMAN_TEST_OUTPUT=Join-Path $fixture 'cmd.txt'
    $cmd="@echo off`r`ncall `"$fixture\switch-pathman.cmd`" /n java /v 25 /s shell`r`nif errorlevel 1 exit /b 1`r`npowershell.exe -NoProfile -Command `"[IO.File]::WriteAllText(`$env:PATHMAN_TEST_OUTPUT,`$env:PATHMAN_TEST_HOME)`"`r`n"
    [IO.File]::WriteAllText((Join-Path $fixture 'run.cmd'),$cmd,[Text.Encoding]::ASCII)
    & $env:ComSpec /d /v:off /c (Join-Path $fixture 'run.cmd')
    Assert ($LASTEXITCODE -eq 0 -and [IO.File]::ReadAllText($env:PATHMAN_TEST_OUTPUT) -ceq $value) 'CMD preserves Unicode, quotes, percent, caret, ampersand and exclamation characters'
    $bash=Join-Path (Split-Path (Split-Path (Get-Command git.exe).Source)) 'bin/bash.exe'
    if(!(Test-Path $bash)){$bash='C:\Software\Develop\Git\bin\bash.exe'}
    if(!(Test-Path $bash)){throw 'Git Bash is required for shell integration tests'}
    $env:PATHMAN_TEST_SCRIPT=Join-Path $fixture 'switch-pathman'
    $env:PATHMAN_TEST_OUTPUT=Join-Path $fixture 'bash.txt'
    & $bash -c '. "$(cygpath -u "$PATHMAN_TEST_SCRIPT")" --name java --version 25 --scope shell || exit; printf "%s" "$PATHMAN_TEST_HOME" > "$(cygpath -u "$PATHMAN_TEST_OUTPUT")"'
    Assert ($LASTEXITCODE -eq 0 -and [IO.File]::ReadAllText($env:PATHMAN_TEST_OUTPUT) -ceq $value) 'Sourced Git Bash updates the caller with literal special characters'
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
    Write-Host 'PASS: PowerShell, CMD and sourced Git Bash scopes, positional/named arguments, PATH updates and literal values.'
}finally{
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    Remove-Item Env:PATHMAN_TEST_OUTPUT,Env:PATHMAN_TEST_SCRIPT,Env:PATHMAN_TEST_SCOPE -ErrorAction SilentlyContinue
}
