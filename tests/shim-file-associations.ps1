[CmdletBinding()]
param([string]$ResultPath='C:\PathManFeaturesTest\file-associations.json')
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Use only the disposable Windows VM and restore its initial checkpoint.'}
$root=Join-Path 'C:\PathManFeaturesTest' ('associated-files-'+[guid]::NewGuid().ToString('N'))
$data=Join-Path $root 'data'
. (Join-Path $PSScriptRoot 'shim-fixtures.ps1')
$id=[guid]::NewGuid().ToString('N');$extension='.pathman-'+$id;$class='PathManTest.'+$id
$checks=[Collections.Generic.List[string]]::new()
function Assert($condition,[string]$message){if(!$condition){throw $message};$checks.Add($message)}
function Run([string]$exe,[string[]]$arguments){
 $start=@{FilePath=$exe;PassThru=$true;WindowStyle='Hidden'};if($arguments.Count -gt 0){$start.ArgumentList=$arguments}
 $process=Start-Process @start;$null=$process.Handle
 try{if(!$process.WaitForExit(20000)){Stop-Process -Id $process.Id -Force;throw 'Associated target timed out'};$process.ExitCode}finally{$process.Dispose()}
}
function Alias([string]$name,[string]$target){New-TestShim 'C:\PathManTestApp' $data $name $target}
try{
 New-Item -ItemType Directory -Path $root -Force|Out-Null
 foreach($suffix in '.cmd','.bat'){
  $script=Join-Path $root ('batch target'+$suffix);$marker=Join-Path $root ('batch'+$suffix+'.observed')
  [IO.File]::WriteAllText($script,"@echo off`r`n> `"$marker`" echo associated-file-opened`r`nexit /b 39`r`n",[Text.Encoding]::ASCII)
  $alias=Alias ('batch-'+$suffix.Substring(1)) $script;$code=Run $alias @()
  Assert ($code -eq 39 -and (Get-Content $marker -Raw).Trim() -eq 'associated-file-opened') "$suffix opens as an ordinary associated file and returns the launched process exit code (actual $code)"
 }
 $handler=Join-Path $root 'document-handler.cmd';$marker=Join-Path $root 'document.observed'
 [IO.File]::WriteAllText($handler,"@echo off`r`n> `"$marker`" echo %~1`r`n>> `"$marker`" echo %~2`r`nexit /b 47`r`n",[Text.Encoding]::ASCII)
 $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes\'+$extension);try{$key.SetValue('',$class)}finally{$key.Dispose()}
 $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes\'+$class+'\shell\open\command')
 try{$key.SetValue('',('"'+$env:WINDIR+'\System32\cmd.exe" /d /s /c ""'+$handler+'" "%1" %*"'))}finally{$key.Dispose()}
 $document=Join-Path $root ('ordinary document'+$extension);[IO.File]::WriteAllText($document,'ordinary associated file')
 $alias=Alias 'document' $document;$code=Run $alias @('"document argument"');$observed=Get-Content $marker
 Assert ($code -eq 47 -and $observed[0] -eq $document -and $observed[1] -eq 'document argument') 'An arbitrary non-executable extension opens its registered handler with the file and alias arguments'
 @{Passed=$true;Checks=$checks}|ConvertTo-Json -Depth 4|Set-Content $ResultPath -Encoding UTF8
}catch{@{Passed=$false;Error=$_.Exception.Message;Stack=$_.ScriptStackTrace;Checks=$checks}|ConvertTo-Json -Depth 4|Set-Content $ResultPath -Encoding UTF8;throw}
finally{
 [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\Classes\'+$extension,$false)
 [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\Classes\'+$class,$false)
}
