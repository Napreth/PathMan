using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public static class MonitoringDestinations
{
    public const string ExternalGroupId="external";
    public static VariableGroupDefinition DefaultExternalGroup()=>new() {Id=ExternalGroupId,Name="外部变量",Rule="external",RequiresMonitoring=true};
    public static IReadOnlyList<VariableGroupDefinition> Categories(EnvironmentWorkspace state,VariableScope scope)
    {
        var groups=state.Groups.Where(group=>group.Scope is null || group.Scope==scope).ToArray();
        var first=groups.FirstOrDefault(group=>group.Id==ExternalGroupId) ?? DefaultExternalGroup();
        return new[]{first}.Concat(state.VisibleGroups(scope)).Concat(groups).DistinctBy(group=>group.Id,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static IReadOnlyList<string> PathVariables(EnvironmentWorkspace state,IReadOnlyList<EnvironmentVariableInfo> variables,
        VariableScope scope,IReadOnlyDictionary<string,string>? runtime=null)
    {
        var first=ExternalPathOrganizer.GroupName(scope);
        var groups=state.VisibleGroups(scope).Where(group=>group.Rule=="paths").Select(group=>group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new[]{first}.Concat(variables.Where(variable=>variable.Scope==scope && ExternalPathOrganizer.IsValidTarget(variable.Name) &&
                groups.Contains(EnvironmentVariableClassifier.Classify(variable,variables,state,runtime))).Select(variable=>variable.Name)
            .Where(name=>!name.Equals(first,StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}
