[CmdletBinding()]
param([string]$Cli='C:\PathManCommandLineTest\final-cli\bin\pathman.exe',[string]$ResultPath='C:\PathManCommandLineTest\gate-results.json')
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine'){throw 'Disposable VM only.'}
$data=Join-Path $env:LOCALAPPDATA 'PathMan\environment.json'
$checks=[Collections.Generic.List[string]]::new()
function Assert($value,[string]$message){if(!$value){throw $message};$checks.Add($message)}
function Enable([bool]$enabled){$state=Get-Content $data -Raw -Encoding UTF8|ConvertFrom-Json;$state|Add-Member -NotePropertyName commandLineVersionSwitchingEnabled -NotePropertyValue $enabled -Force;$state|ConvertTo-Json -Depth 40|Set-Content $data -Encoding UTF8}
function Run([string[]]$arguments,[int]$code){
 $start=[Diagnostics.ProcessStartInfo]::new($Cli,($arguments -join ' '));$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.StandardOutputEncoding=[Text.Encoding]::UTF8;$start.StandardErrorEncoding=[Text.Encoding]::UTF8
 $process=[Diagnostics.Process]::Start($start)
 try{$output=$process.StandardOutput.ReadToEndAsync();$errorText=$process.StandardError.ReadToEndAsync();if(!$process.WaitForExit(15000)){$process.Kill();throw 'CLI did not exit'};Assert ($process.ExitCode -eq $code) "$($arguments -join ' ') exits with $code";return @{output=$output.GetAwaiter().GetResult();error=$errorText.GetAwaiter().GetResult()}}finally{$process.Dispose()}
}
try {
 Enable $false
 $before=(Get-Content $data -Raw -Encoding UTF8|ConvertFrom-Json).versionProjects|ConvertTo-Json -Depth 20 -Compress
 foreach($arguments in @(@('list'),@('add','blocked','1','x','--variable','PATHMAN_CLI_BLOCKED','--type','User'),@('delete','test'),@('switch','test','1'))){$result=Run $arguments 1;Assert ($result.error -match 'disabled|未启用') "$($arguments[0]) reports that command-line switching is disabled"}
 $after=(Get-Content $data -Raw -Encoding UTF8|ConvertFrom-Json).versionProjects|ConvertTo-Json -Depth 20 -Compress
 Assert ($before -ceq $after) 'Disabled version commands leave the catalog unchanged'
 Assert ($null -eq [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Environment','PATHMAN_CLI_BLOCKED',$null)) 'Disabled commands do not create environment variables'
 Enable $true
 $null=Run @('list') 0
 Enable $false
 $hash=(Get-FileHash $data).Hash
 $help=Run @('--help') 0
 Assert ($help.output -match 'pathman list' -and $help.output -notmatch '--data-root') 'Help remains available and contains only the supported version CLI'
 $null=Run @('--data-root','C:\unused','list') 2
 Assert ((Get-FileHash $data).Hash -eq $hash) 'Help and syntax errors never rewrite application configuration'
 @{passed=$true;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8
}catch{@{passed=$false;error=$_.Exception.Message;checks=$checks.ToArray()}|ConvertTo-Json -Depth 8|Set-Content $ResultPath -Encoding UTF8;throw}
