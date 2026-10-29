[CmdletBinding(PositionalBinding=$false)]
param(
    [Parameter(Mandatory,Position=0)][Alias('n')][string]$Name,
    [Parameter(Mandatory,Position=1)][Alias('v')][string]$Version,
    [Alias('s')][ValidateSet('reg','shell','all')][string]$Scope='shell',
    [ValidateSet('PowerShell','Cmd','Bash')][string]$OutputFormat='PowerShell',
    [string]$OutputFile
)
$ErrorActionPreference='Stop'
function Invoke-PathMan([string[]]$Arguments,[string]$FailureMessage){
    $start=[Diagnostics.ProcessStartInfo]::new($cli)
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
    $start.Arguments=(@(foreach($argument in $Arguments){
        '"'+[regex]::Replace($argument,'(\\*)("|$)',{param($match)
            $slashes=$match.Groups[1].Value
            $slashes+$slashes+$(if($match.Groups[2].Value){'\"'})
        })+'"'
    }) -join ' ')
    $process=[Diagnostics.Process]::Start($start)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync()
        $stderr=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output=$stdout.GetAwaiter().GetResult()
        $errorText=$stderr.GetAwaiter().GetResult()
        if($process.ExitCode){throw ($FailureMessage+' '+$errorText).Trim()}
        return $output
    }finally{$process.Dispose()}
}
try {
    $cli=Join-Path $PSScriptRoot 'pathman.exe'
    if(!(Test-Path -LiteralPath $cli)){$cli=(Get-Command pathman.exe -ErrorAction Stop).Source}
    $json=Invoke-PathMan @('switch',$Name,$Version,'--resolve') 'PathMan could not resolve the requested version.'
    $target=$json|ConvertFrom-Json
    $updates=[ordered]@{}
    if($Scope -ne 'reg'){
        $old=[Environment]::GetEnvironmentVariable($target.Name,'Process')
        $values=@{}
        foreach($entry in [Environment]::GetEnvironmentVariables().GetEnumerator()){$values[$entry.Key]=$entry.Value}
        $values[$target.Name]=[string]$target.Value
        $expand={param([string]$text,[string[]]$seen)
            [regex]::Replace($text,'%([^%]+)%',{param($match)
                $key=$match.Groups[1].Value
                if($seen -contains $key -or $seen.Count -ge 32){throw 'Circular or excessively nested environment reference.'}
                if(!$values.ContainsKey($key)){throw "Undefined environment variable: $key"}
                & $expand ([string]$values[$key]) @($seen+$key)
            })
        }
        $value=& $expand ([string]$target.Value) @($target.Name)
        $updates[$target.Name]=$value
        $oldRoot=if($old){$old.TrimEnd('\','/')}else{''}
        $paths=foreach($part in ([string][Environment]::GetEnvironmentVariable('Path','Process')).Split(';')){
            if($oldRoot -and ($part.Equals($oldRoot,[StringComparison]::OrdinalIgnoreCase) -or $part.StartsWith($oldRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $part.StartsWith($oldRoot+'/',[StringComparison]::OrdinalIgnoreCase))){$value.TrimEnd('\','/')+$part.Substring($oldRoot.Length)}
            elseif($part -match ('(?i)%'+[regex]::Escape($target.Name)+'%')){& $expand $part @()}
            else{$part}
        }
        $updates['PATH']=$paths -join ';'
    }
    foreach($key in $updates.Keys){
        if(([string]$updates[$key]).Length -gt 32767){throw "Expanded value is too long: $key"}
        if($OutputFormat -eq 'Bash' -and $key -cnotmatch '^[A-Za-z_][A-Za-z0-9_]*$'){throw "Not a Bash variable name: $key"}
        if($OutputFormat -eq 'Cmd' -and (($key+'='+$updates[$key]).Length -gt 8000 -or ($key+'='+$updates[$key]) -match "[`r`n]")){throw 'CMD cannot represent this value without truncation.'}
    }
    if($OutputFormat -eq 'Cmd' -and !$OutputFile){throw 'An output file is required for CMD.'}
    if($Scope -ne 'shell'){
        $null=Invoke-PathMan @('switch',$Name,$Version) 'PathMan could not write the environment variable.'
    }
    if($OutputFormat -eq 'Cmd'){
        $lines=@(foreach($key in $updates.Keys){$key+'='+$updates[$key]})
        $originalCodePage=[Console]::OutputEncoding.CodePage
        $fileCodePage=$originalCodePage
        $encoding=if($fileCodePage -eq 65001){[Text.UTF8Encoding]::new($false,$true)}else{[Text.Encoding]::GetEncoding($fileCodePage,[Text.EncoderFallback]::ExceptionFallback,[Text.DecoderFallback]::ExceptionFallback)}
        $useUtf8=$false
        try{foreach($line in $lines){if($encoding.GetString($encoding.GetBytes($line)) -cne $line){$useUtf8=$true;break}}}catch [Text.EncoderFallbackException]{$useUtf8=$true}
        if($useUtf8){
            $fileCodePage=65001
            $encoding=[Text.UTF8Encoding]::new($false)
        }
        [IO.File]::WriteAllLines($OutputFile,$lines,$encoding)
        [IO.File]::WriteAllText($OutputFile+'.cp',"$originalCodePage $fileCodePage",[Text.Encoding]::ASCII)
    }elseif($OutputFormat -eq 'Bash'){
        $writer=[IO.StreamWriter]::new([Console]::OpenStandardOutput(),[Text.UTF8Encoding]::new($false),1024,$true)
        try{foreach($key in $updates.Keys){
            $quoted="'"+([string]$updates[$key]).Replace("'","'\''")+"'"
            if($key -eq 'PATH'){$writer.WriteLine('export PATH="$(cygpath -u -p -- '+$quoted+')"')}else{$writer.WriteLine('export '+$key+'='+$quoted)}
        }}finally{$writer.Dispose()}
    }else{
        foreach($key in $updates.Keys){[Environment]::SetEnvironmentVariable($key,$updates[$key],'Process')}
    }
}catch{
    if($OutputFormat -eq 'PowerShell'){throw}
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
