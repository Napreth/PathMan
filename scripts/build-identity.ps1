function New-BuildCounter([string]$Repository) {
    @{schemaVersion=2;repository=$Repository;x=0;y=0;releasedX=0;releaseCommit='';latestNumber='';latestTag='';releases=@{}}
}
function Reserve-BuildIdentity([hashtable]$State,[ValidateSet('Release','Test')][string]$Kind,[string]$Tag,[string]$Commit,[int]$MainX) {
    if($MainX -lt $State.x -or $MainX -gt 65535){throw 'Mainline X cannot go backwards or exceed 65535.'}
    if($State.x -ne $MainX){$State.x=$MainX;$State.y=0}
    if($Kind -eq 'Release'){
        if($State.releases.ContainsKey($Tag)){
            $previous=$State.releases[$Tag]
            if($previous.commit -ne $Commit -or $previous.number -ne $State.latestNumber){throw 'An older or moved release tag cannot reuse its build number.'}
            return $previous.number
        }
        if($MainX -le 0 -or $MainX -le $State.releasedX -or $State.y -gt 0){throw 'Increase mainline X and reset Y before publishing a new release tag.'}
        $State.releasedX=$MainX;$State.y=0;$State.releaseCommit=$Commit
        $number="$($State.x).0.0"
        $State.releases[$Tag]=@{number=$number;commit=$Commit}
    }else{
        if($State.y -ge 65535){throw 'Publish a new X release before the public build counter exceeds 65535.'}
        $State.y++;$number="$($State.x).$($State.y).0";$Tag="test/$number"
    }
    $State.latestNumber=$number;$State.latestTag=$Tag
    $number
}
