using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed record EnvironmentChangeOrigin(VariableScope Scope,string Name,int ProcessId,string ImagePath,DateTime Time,string CommandLine="")
{
    public string ProcessName=>Path.GetFileName(ImagePath);
    public bool IsIdentified=>ImagePath.Length>0 && (!ProcessName.Equals("rundll32.exe",StringComparison.OrdinalIgnoreCase) || CommandLine.Length>0);
    public bool IsSystemEditor=>ProcessName.Equals("SystemPropertiesAdvanced.exe",StringComparison.OrdinalIgnoreCase) ||
        ProcessName.Equals("rundll32.exe",StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(CommandLine,@"(?:^|\s|"")(?:[^""\r\n]*[\\/])?sysdm\.cpl""?\s*,\s*EditEnvironmentVariables(?=\s|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public bool IsPowerToys=>ProcessName.Equals("PowerToys.EnvironmentVariables.exe",StringComparison.OrdinalIgnoreCase);
    public bool Excluded(EnvironmentWorkspace state)=>state.TrackChangeOrigins &&
        (state.ShouldIgnoreSystemEditor(Scope) && IsSystemEditor || state.ShouldIgnorePowerToys(Scope) && IsPowerToys);
}

public interface IEnvironmentChangeOrigins
{
    bool IsReady {get;}
    // A set can contain several writers. Exclude the whole observed batch if any
    // excluded writer touched it, rather than attributing their edit to another PID.
    IReadOnlyList<EnvironmentChangeOrigin> Take(VariableScope scope,string name);
}
