[CmdletBinding(PositionalBinding=$false)]
param(
    [Parameter(Mandatory,Position=0)][Alias('n')][string]$Name,
    [Parameter(Mandatory,Position=1)][Alias('v')][string]$Version,
    [Alias('s')][ValidateSet('reg','shell','all')][string]$Scope,
    [ValidateSet('PowerShell','Cmd','Bash')][string]$OutputFormat='PowerShell',
    [string]$OutputFile
)
$ErrorActionPreference='Stop'
$savedEncoding=[Console]::OutputEncoding
try {
    if(!$PSBoundParameters.ContainsKey('Scope')){throw 'An explicit -Scope is required.'}
    [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
    $cli=Join-Path $PSScriptRoot 'pathman.exe'
    if(!(Test-Path -LiteralPath $cli)){$cli=(Get-Command pathman.exe -ErrorAction Stop).Source}
    $json=& $cli switch $Name $Version --resolve
    if($LASTEXITCODE){throw 'PathMan could not resolve the requested version.'}
    $target=($json -join "`n")|ConvertFrom-Json
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
        $message=& $cli switch $Name $Version
        if($LASTEXITCODE){throw 'PathMan could not write the environment variable.'}
    }
    if($OutputFormat -eq 'Cmd'){
        $lines=@(foreach($key in $updates.Keys){$key+'='+$updates[$key]})
        [IO.File]::WriteAllLines($OutputFile,$lines,[Text.UTF8Encoding]::new($false))
    }elseif($OutputFormat -eq 'Bash'){
        foreach($key in $updates.Keys){
            $quoted="'"+([string]$updates[$key]).Replace("'","'\''")+"'"
            if($key -eq 'PATH'){[Console]::WriteLine('export PATH="$(cygpath -u -p -- '+$quoted+')"')}else{[Console]::WriteLine('export '+$key+'='+$quoted)}
        }
    }else{
        foreach($key in $updates.Keys){[Environment]::SetEnvironmentVariable($key,$updates[$key],'Process')}
    }
}catch{
    if($OutputFormat -eq 'PowerShell'){throw}
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}finally{[Console]::OutputEncoding=$savedEncoding}
