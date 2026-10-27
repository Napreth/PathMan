# Fixture helpers for disposable-VM tests; these are not application command-line entries.
function Read-TestWorkspace([string]$DataFile) {
 if(Test-Path $DataFile){return Get-Content $DataFile -Raw -Encoding UTF8|ConvertFrom-Json}
 $defaults=Join-Path $PSScriptRoot 'groups.json'
 if(!(Test-Path $defaults)){$defaults=Join-Path $PSScriptRoot '../src/Pathman.Core/EnvironmentVariables/Defaults/groups.json'}
 New-Item -ItemType Directory -Path (Split-Path $DataFile) -Force|Out-Null
 Get-Content $defaults -Raw -Encoding UTF8|ConvertFrom-Json
}
function New-TestShim([string]$Installation,[string]$DataDirectory,[string]$Name,[string]$Target) {
 if([IO.Path]::GetFileName($Name) -ne $Name -or $Name -in '.','..'){throw 'Invalid fixture name'}
 $directory=Join-Path $DataDirectory 'shims';New-Item -ItemType Directory -Path $directory -Force|Out-Null
 $template=Join-Path $Installation 'internal\PathMan.Shim.exe';$executable=Join-Path $directory ($Name+'.exe')
 if([IO.Path]::GetPathRoot($template) -eq [IO.Path]::GetPathRoot($executable)){New-Item -ItemType HardLink -Path $executable -Target $template|Out-Null}else{Copy-Item -LiteralPath $template -Destination $executable}
 [IO.File]::WriteAllText((Join-Path $directory ($Name+'.shim')),"[PathMan.Shim]`r`nFormat=2`r`nTargetBase64=$([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Target)))`r`n",[Text.Encoding]::Unicode)
 $executable
}
function Remove-TestShim([string]$DataDirectory,[string]$Name) {
 if([IO.Path]::GetFileName($Name) -ne $Name -or $Name -in '.','..'){throw 'Invalid fixture name'}
  if(!(Test-Path -LiteralPath (Join-Path $DataDirectory 'shims'))){return}
 [IO.File]::Delete((Join-Path $DataDirectory ('shims\'+$Name+'.exe')));[IO.File]::Delete((Join-Path $DataDirectory ('shims\'+$Name+'.shim')))
}
function Test-BackgroundEventName([string]$Installation,[string]$Event) {
 $hash=[Security.Cryptography.SHA256]::Create()
 # The Framework test host preserves final sigma; the .NET 10 application uses ICU uppercase.
 $upper=[IO.Path]::GetFullPath($Installation).TrimEnd('\').ToUpperInvariant().Replace([char]0x3C2,[char]0x3A3)
 try{$digest=[BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($upper))).Replace('-','').Substring(0,16)}finally{$hash.Dispose()}
 'Local\PathMan.'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value+'.'+$digest+'.'+$Event
}
function Stop-TestBackground([string]$Installation) {
 try{$stop=[Threading.EventWaitHandle]::OpenExisting((Test-BackgroundEventName $Installation 'Stop'))}catch [Threading.WaitHandleCannotBeOpenedException]{return}
 try{$null=$stop.Set()}finally{$stop.Dispose()}
}
