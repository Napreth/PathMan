namespace Pathman.Core.EnvironmentVariables;

public enum ValueDiffKind { Context, Removed, Added }
public sealed record ValueDiffLine(ValueDiffKind Kind,string Text,int? OldLine,int? NewLine)
{
    public string Prefix=>Kind switch {ValueDiffKind.Added=>"+",ValueDiffKind.Removed=>"−",_=>" "};
}
public sealed record ValueDiffPair(ValueDiffLine? Before,ValueDiffLine? After);
public static class EnvironmentValueDiff
{
    public static IReadOnlyList<ValueDiffPair> Pair(IReadOnlyList<ValueDiffLine> lines)
    {
        var result=new List<ValueDiffPair>();
        for(var index=0;index<lines.Count;)
        {
            if(lines[index].Kind==ValueDiffKind.Context) {result.Add(new(lines[index],lines[index]));index++;continue;}
            var removed=new List<ValueDiffLine>(); var added=new List<ValueDiffLine>();
            while(index<lines.Count && lines[index].Kind!=ValueDiffKind.Context)
            {var line=lines[index++]; if(line.Kind==ValueDiffKind.Removed) removed.Add(line); else added.Add(line);}
            for(var row=0;row<Math.Max(removed.Count,added.Count);row++)
                result.Add(new(row<removed.Count ? removed[row] : null,row<added.Count ? added[row] : null));
        }
        return result;
    }
    public static IReadOnlyList<ValueDiffLine> Create(string? before,string after)
    {
        var old=before is null || before.Length==0 ? [] : before.Split(';',StringSplitOptions.None);
        var next=after.Length==0 ? [] : after.Split(';',StringSplitOptions.None);
        var result=new List<ValueDiffLine>();
        // Bounded LCS keeps previews responsive even for pathological registry values.
        if((long)old.Length*next.Length>2_000_000)
        {
            result.AddRange(old.Select((text,index)=>new ValueDiffLine(ValueDiffKind.Removed,text,index+1,null)));
            result.AddRange(next.Select((text,index)=>new ValueDiffLine(ValueDiffKind.Added,text,null,index+1)));
            return result;
        }
        var lengths=new int[old.Length+1,next.Length+1];
        for(var i=old.Length-1;i>=0;i--) for(var j=next.Length-1;j>=0;j--)
            lengths[i,j]=old[i]==next[j] ? lengths[i+1,j+1]+1 : Math.Max(lengths[i+1,j],lengths[i,j+1]);
        var a=0; var b=0;
        while(a<old.Length || b<next.Length)
        {
            if(a<old.Length && b<next.Length && old[a]==next[b]) {result.Add(new(ValueDiffKind.Context,old[a],++a,++b));}
            else if(a<old.Length && (b==next.Length || lengths[a+1,b]>=lengths[a,b+1])) {result.Add(new(ValueDiffKind.Removed,old[a],++a,null));}
            else {result.Add(new(ValueDiffKind.Added,next[b],null,++b));}
        }
        return result;
    }
}
