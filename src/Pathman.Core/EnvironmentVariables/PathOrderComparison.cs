using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed record PathOrderComparison(bool Changed,bool IsResolved)
{
    public static PathOrderComparison Compare(string before,string after,VariableScope scope,
        IReadOnlyList<EnvironmentVariableInfo> original,IReadOnlyList<EnvironmentVariableInfo> updated,
        IReadOnlyDictionary<string,string>? runtime=null)
    {
        var oldPaths=new EnvironmentPathResolver(original,runtime).Expand(before,scope);
        var newPaths=new EnvironmentPathResolver(updated,runtime).Expand(after,scope);
        static string[] Order(EnvironmentPathExpansion paths)=>paths.Paths.Select(path=>EnvironmentPathResolver.Normalize(path) ?? path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Repeated copies do not add search locations. Removing the first copy
        // can still change precedence relative to the remaining locations.
        return new(!Order(oldPaths).SequenceEqual(Order(newPaths),StringComparer.OrdinalIgnoreCase),oldPaths.IsResolved && newPaths.IsResolved);
    }
}
