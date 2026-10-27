namespace Pathman.Core.EnvironmentVariables;

public sealed record EnvironmentMergeResult(string Value,bool HasConflicts);
public enum EnvironmentEditOrigin {None,ExternalAdded,ExternalRemoved,UserAdded,UserRemoved}
public sealed record EnvironmentReviewLine(string Text,EnvironmentEditOrigin Origin,int? WorkingIndex=null)
{
    public string Prefix=>Origin switch {EnvironmentEditOrigin.ExternalAdded or EnvironmentEditOrigin.UserAdded=>"+",EnvironmentEditOrigin.ExternalRemoved or EnvironmentEditOrigin.UserRemoved=>"-",_=>""};
}

public static class EnvironmentValueMerge
{
    private sealed record Hunk(int Start,int Count,string[] Added,bool Local);
    private static string[] Lines(string value)=>value.Length==0 ? [] : value.Split(';',StringSplitOptions.None);
    private static IReadOnlyList<Hunk> Hunks(string before,string after,bool local)
    {
        var lines=EnvironmentValueDiff.Create(before,after);var result=new List<Hunk>();var position=0;
        for(var i=0;i<lines.Count;)
        {
            if(lines[i].Kind==ValueDiffKind.Context) {position++;i++;continue;}
            var start=position;var removed=0;var added=new List<string>();
            while(i<lines.Count && lines[i].Kind!=ValueDiffKind.Context)
            {if(lines[i].Kind==ValueDiffKind.Removed) {removed++;position++;} else added.Add(lines[i].Text);i++;}
            result.Add(new(start,removed,added.ToArray(),local));
        }
        return result;
    }
    public static EnvironmentMergeResult Merge(string ancestor,string local,string external)
    {
        if(local==ancestor) return new(external,false);
        if(external==ancestor || local==external) return new(local,false);
        var mine=Hunks(ancestor,local,true);var theirs=Hunks(ancestor,external,false);var conflicts=false;
        bool Overlap(Hunk a,Hunk b)=>a.Count==0 && b.Count==0 ? false : a.Count==0 ? a.Start>b.Start && a.Start<b.Start+b.Count :
            b.Count==0 ? b.Start>a.Start && b.Start<a.Start+a.Count : a.Start<b.Start+b.Count && b.Start<a.Start+a.Count;
        var combined=new List<Hunk>(mine);
        foreach(var other in theirs)
        {
            if(mine.Any(item=>item.Start==other.Start && item.Count==other.Count && item.Added.SequenceEqual(other.Added))) continue;
            if(mine.Any(item=>Overlap(item,other))) {conflicts=true;continue;}
            combined.Add(other);
        }
        var source=Lines(ancestor);var result=new List<string>();var cursor=0;
        foreach(var hunk in combined.OrderBy(item=>item.Start).ThenBy(item=>item.Count==0 ? 0 : 1).ThenBy(item=>item.Local ? 1 : 0))
        {
            while(cursor<hunk.Start) result.Add(source[cursor++]);
            if(cursor>hunk.Start && hunk.Count>0) continue;
            result.AddRange(hunk.Added);cursor=Math.Max(cursor,hunk.Start+hunk.Count);
        }
        while(cursor<source.Length) result.Add(source[cursor++]);
        return new(string.Join(';',result),conflicts);
    }
}

// Keep the user's branch separate from incoming changes. In particular, a user
// deletion that coincides with an external deletion must not disappear on undo.
public sealed class EnvironmentLiveValue(string ancestor)
{
    private readonly Dictionary<string,int> rejectedAdditions=new(StringComparer.Ordinal);
    public string Ancestor {get;private set;}=ancestor;
    public string Local {get;private set;}=ancestor;
    public string External {get;private set;}=ancestor;
    public string Working {get;private set;}=ancestor;
    public bool HasConflicts {get;private set;}
    public bool HasExternalChanges=>External!=Ancestor;
    public bool HasLocalChanges=>Local!=Ancestor || rejectedAdditions.Count>0;
    internal EnvironmentLiveValue Copy()
    {
        var copy=new EnvironmentLiveValue(Ancestor) {Local=Local,External=External,Working=Working,HasConflicts=HasConflicts};
        foreach(var pair in rejectedAdditions) copy.rejectedAdditions.Add(pair.Key,pair.Value);
        return copy;
    }
    public void Edit(string value)
    {
        if(value==Working) return;
        var removedIndices=EnvironmentValueDiff.Create(Working,value).Where(line=>line.Kind==ValueDiffKind.Removed).Select(line=>line.OldLine-1).ToHashSet();
        var externalAddedIndices=EnvironmentValueDiff.Create(Ancestor,External).Where(line=>line.Kind==ValueDiffKind.Added).Select(line=>line.NewLine-1).ToHashSet();
        var removed=EnvironmentValueDiff.Create(External,Working).Where(line=>line.Kind==ValueDiffKind.Context && removedIndices.Contains(line.NewLine-1) && externalAddedIndices.Contains(line.OldLine-1)).GroupBy(line=>line.Text);
        var externalAdded=EnvironmentValueDiff.Create(Ancestor,External).Where(line=>line.Kind==ValueDiffKind.Added).GroupBy(line=>line.Text).ToDictionary(group=>group.Key,group=>group.Count(),StringComparer.Ordinal);
        foreach(var group in removed)
            if(externalAdded.TryGetValue(group.Key,out var count)) rejectedAdditions[group.Key]=Math.Min(count,rejectedAdditions.GetValueOrDefault(group.Key)+group.Count());
        Local=EnvironmentValueMerge.Merge(Working,value,Local).Value;Working=value;
    }
    public void Observe(string external)
    {
        External=external;
        var rejection=new Dictionary<string,int>(rejectedAdditions,StringComparer.Ordinal);
        var filtered=string.Join(';',EnvironmentValueDiff.Create(Ancestor,external).Where(line=>line.Kind!=ValueDiffKind.Removed).Where(line=>
        {if(line.Kind!=ValueDiffKind.Added || !rejection.TryGetValue(line.Text,out var count) || count==0) return true;rejection[line.Text]=count-1;return false;}).Select(line=>line.Text));
        var merged=EnvironmentValueMerge.Merge(Ancestor,Local,filtered);Working=merged.Value;HasConflicts=merged.HasConflicts;
    }
    public void UndoExternal() {Working=Local;rejectedAdditions.Clear();Ancestor=External;Local=Working;HasConflicts=false;}
    public void Accept() {Ancestor=External=Local=Working;rejectedAdditions.Clear();HasConflicts=false;}
    public IReadOnlyList<EnvironmentReviewLine> Review()
    {
        var external=EnvironmentValueDiff.Create(Ancestor,External);
        var removedBefore=new Dictionary<int,List<string>>();var externalKinds=new Dictionary<int,ValueDiffKind>();var position=0;
        foreach(var line in external)
        {
            if(line.Kind==ValueDiffKind.Removed)
            {if(!removedBefore.TryGetValue(position,out var group)) removedBefore[position]=group=[];group.Add(line.Text);}
            else {externalKinds[position++]=line.Kind;}
        }
        var userDiff=EnvironmentValueDiff.Create(Ancestor,Local);
        var userRemoved=userDiff.Where(line=>line.Kind==ValueDiffKind.Removed).GroupBy(line=>line.Text).ToDictionary(group=>group.Key,group=>group.Count(),StringComparer.Ordinal);
        var userAdded=userDiff.Where(line=>line.Kind==ValueDiffKind.Added).GroupBy(line=>line.Text).ToDictionary(group=>group.Key,group=>group.Count(),StringComparer.Ordinal);
        var result=new List<EnvironmentReviewLine>();var emitted=new HashSet<int>();
        void Before(int index)
        {
            if(!emitted.Add(index) || !removedBefore.TryGetValue(index,out var old)) return;
            foreach(var text in old)
            {
                result.Add(new(text,EnvironmentEditOrigin.ExternalRemoved));
                if(userRemoved.TryGetValue(text,out var count) && count>0) {result.Add(new(text,EnvironmentEditOrigin.UserRemoved));userRemoved[text]=count-1;}
            }
        }
        position=0;
        foreach(var line in EnvironmentValueDiff.Create(External,Working))
        {
            Before(position);
            if(line.Kind==ValueDiffKind.Added) result.Add(new(line.Text,EnvironmentEditOrigin.UserAdded,line.NewLine-1));
            else
            {
                var added=externalKinds.GetValueOrDefault(position)==ValueDiffKind.Added;
                if(line.Kind==ValueDiffKind.Removed)
                {if(added) result.Add(new(line.Text,EnvironmentEditOrigin.ExternalAdded));result.Add(new(line.Text,EnvironmentEditOrigin.UserRemoved));}
                else
                {
                    var mine=added && userAdded.GetValueOrDefault(line.Text)>0;
                    if(mine) userAdded[line.Text]--;
                    result.Add(new(line.Text,mine ? EnvironmentEditOrigin.UserAdded : added ? EnvironmentEditOrigin.ExternalAdded : EnvironmentEditOrigin.None,line.NewLine-1));
                }
                position++;
            }
        }
        Before(position);return result;
    }
}
