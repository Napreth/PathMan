using System.Text.RegularExpressions;
using Pathman.Core.Models;

namespace Pathman.Core.Notifications;

public sealed record Osc8Span(string Text, string? Link);
public static partial class Osc8Text
{
    [GeneratedRegex("\u001b\\]8;[^;\\u0007\\u001b]*;([^\\u0007\\u001b]*)(?:\\u0007|\u001b\\\\)",RegexOptions.CultureInvariant)]
    private static partial Regex Control();
    public static IReadOnlyList<Osc8Span> Parse(string text)
    {
        var spans = new List<Osc8Span>();
        var index=0; string? link=null;
        foreach(Match match in Control().Matches(text))
        {
            if(match.Index>index) spans.Add(new(text[index..match.Index],link));
            link=match.Groups[1].Value.Length==0 ? null : match.Groups[1].Value;
            index=match.Index+match.Length;
        }
        if(index<text.Length) spans.Add(new(text[index..],link));
        return spans;
    }
    public static string Link(string text,string target) => $"\u001b]8;;{target}\u001b\\{text}\u001b]8;;\u001b\\";
}

public sealed record AppPageLink(string Page,VariableScope? Scope=null,string? Name=null)
{
    private static readonly HashSet<string> Pages=new(["variables","health","aliases","versions","monitor","settings","about"],StringComparer.OrdinalIgnoreCase);
    public override string ToString()=>"apppage:"+Page+(Scope is null || Name is null ? "" : $"?scope={Scope}&name={Uri.EscapeDataString(Name)}");
    public static AppPageLink? Parse(string value)
    {
        if(!value.StartsWith("apppage:",StringComparison.OrdinalIgnoreCase) || value.Length>4096) return null;
        var parts=value[8..].TrimStart('/').Split('?',2);
        if(!Pages.Contains(parts[0])) return null;
        if(parts.Length==1) return new(parts[0].ToLowerInvariant());
        var query=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach(var field in parts[1].Split('&'))
            {
                var pair=field.Split('=',2);
                if(pair.Length!=2 || !query.TryAdd(pair[0],Uri.UnescapeDataString(pair[1]))) return null;
            }
        }
        catch(UriFormatException) { return null; }
        if(query.Count!=2 || !query.TryGetValue("scope",out var scopeText) || !Enum.TryParse<VariableScope>(scopeText,true,out var scope) ||
            scope is not (VariableScope.User or VariableScope.Machine) || !query.TryGetValue("name",out var name) || name.Length is 0 or >255 || name.Any(char.IsControl)) return null;
        return parts[0].Equals("variables",StringComparison.OrdinalIgnoreCase) ? new("variables",scope,name) : null;
    }
}
