using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed class VariableGroupDefinition
{
    public override string ToString()=>Name;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Rule { get; set; } = "manual";
    public VariableScope? Scope { get; set; }
    public bool RequiresMonitoring { get; set; }
    public bool Collapsed { get; set; }
}

public sealed record VariableSnapshot(VariableScope Scope, string Name, string Value, int Kind);
public sealed record VariableChange(DateTime Time, VariableScope Scope, string Name, string Action, string? Before, string? After, string Source);

public sealed class EnvironmentWorkspace
{
    public List<VariableGroupDefinition> Groups { get; set; } = [];
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<VariableScope, List<string>> GroupOrder { get; set; } = [];
    public Dictionary<VariableScope, List<string>> PinnedGroups { get; set; } = [];
    public List<string> PinnedVariables { get; set; } = [];
    public bool MonitoringEnabled { get; set; }
    public bool GroupExternalUserVariables {get;set;}=true;
    public bool GroupExternalMachineVariables {get;set;}=true;
    public bool AutoArrangeUserPath {get;set;}
    public bool AutoArrangeMachinePath {get;set;}
    [JsonIgnore] public bool AnyPathOrganizationEnabled=>AutoArrangeUserPath || AutoArrangeMachinePath;
    public bool IsPathOrganizationEnabled(VariableScope scope)=>scope switch
    {VariableScope.User=>AutoArrangeUserPath,VariableScope.Machine=>AutoArrangeMachinePath,_=>false};
    [JsonPropertyName("autoArrangePath"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyAutoArrangePath {get;set;}
    public bool RemoveUserPathDuplicates {get;set;}=true;
    public bool RemoveMachinePathDuplicates {get;set;}=true;
    public bool MoveExternalUserPathEntries {get;set;}=true;
    public bool MoveExternalMachinePathEntries {get;set;}=true;
    public bool ShouldGroupExternalVariables(VariableScope scope)=>scope==VariableScope.User ? GroupExternalUserVariables : GroupExternalMachineVariables;
    public bool ShouldRemovePathDuplicates(VariableScope scope)=>scope==VariableScope.User ? RemoveUserPathDuplicates : RemoveMachinePathDuplicates;
    public bool ShouldMovePathEntries(VariableScope scope)=>scope==VariableScope.User ? MoveExternalUserPathEntries : MoveExternalMachinePathEntries;
    [JsonPropertyName("groupExternalNewVariables"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyGroupExternalVariables {get;set;}
    [JsonPropertyName("removeExternalPathDuplicates"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyRemovePathDuplicates {get;set;}
    [JsonPropertyName("moveExternalPathEntries"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyMovePathEntries {get;set;}
    public List<ExternalPathOrganizationPlan> PendingPathOrganizations {get;set;}=[];
    public string? MonitorMessage {get;set;}
    public DateTime? MonitorMessageTime {get;set;}
    public DateTime? RegistryWriteInProgressUntil {get;set;}
    public List<VariableSnapshot> Snapshot { get; set; } = [];
    public HashSet<string> ExternalVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VariableChange> Changes { get; set; } = [];
    public HashSet<string> IgnoredHealthIssues {get;set;}=[];
    public string Revision { get; set; } = "";
    public static string Key(VariableScope scope, string name) => $"{scope}:{name}";
    public IEnumerable<VariableGroupDefinition> VisibleGroups(VariableScope scope)
    {
        var pins = PinnedGroups.GetValueOrDefault(scope) ?? [];
        var order = GroupOrder.GetValueOrDefault(scope) ?? [];
        return Groups.Where(group => (group.Scope is null || group.Scope == scope) &&
                (scope != VariableScope.User || group.Rule != "builtin") && (!group.RequiresMonitoring || MonitoringEnabled && ShouldGroupExternalVariables(scope)))
            .OrderBy(group => IsGroupPinned(scope, group.Id) ? 0 : 1)
            .ThenBy(group => Position(IsGroupPinned(scope, group.Id) ? pins : order, group.Id));
    }
    private static int Position(List<string> items, string key)
    { var index = items.FindIndex(item => item.Equals(key, StringComparison.OrdinalIgnoreCase)); return index < 0 ? int.MaxValue : index; }
    public bool IsGroupPinned(VariableScope scope, string id) => PinnedGroups.GetValueOrDefault(scope)?.Contains(id, StringComparer.OrdinalIgnoreCase) == true;
    public bool IsVariablePinned(VariableScope scope, string name) => PinnedVariables.Contains(Key(scope, name), StringComparer.OrdinalIgnoreCase);
    public IEnumerable<EnvironmentVariableInfo> OrderVariables(IEnumerable<EnvironmentVariableInfo> variables) => variables
        .OrderBy(variable => IsVariablePinned(variable.Scope, variable.Name) ? 0 : 1)
        .ThenBy(variable => Position(PinnedVariables, Key(variable.Scope, variable.Name)))
        .ThenBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase);
    public void PinGroup(VariableScope scope, string id, bool pinned)
    {
        if (!VisibleGroups(scope).Any(group => group.Id == id)) throw new ArgumentException("找不到分组。");
        if (!PinnedGroups.TryGetValue(scope, out var pins)) PinnedGroups[scope] = pins = [];
        pins.RemoveAll(item => item.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (pinned) pins.Add(id);
    }
    public void PinVariable(VariableScope scope, string name, bool pinned)
    {
        var key = Key(scope, name);
        PinnedVariables.RemoveAll(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (pinned) PinnedVariables.Add(key);
    }
    public void MoveGroup(VariableScope scope, string id, string? beforeId)
    {
        var groups = VisibleGroups(scope).ToList();
        var source = groups.Find(group => group.Id == id) ?? throw new ArgumentException("找不到分组。");
        if (beforeId == id) return;
        var pinned = IsGroupPinned(scope, id);
        var remaining = groups.Where(group => group != source).ToList();
        var index = beforeId is null ? remaining.Count : remaining.FindIndex(group => group.Id == beforeId);
        if (index < 0 || (index > 0 && !pinned && IsGroupPinned(scope, remaining[index - 1].Id) &&
                index < remaining.Count && IsGroupPinned(scope, remaining[index].Id)) ||
            (!pinned && index == 0 && remaining.Count > 0 && IsGroupPinned(scope, remaining[0].Id)) ||
            (pinned && index > 0 && !IsGroupPinned(scope, remaining[index - 1].Id)))
            throw new ArgumentException("置顶分组与普通分组只能在各自区域内排序。");
        remaining.Insert(index, source);
        var hiddenOrder = (GroupOrder.GetValueOrDefault(scope) ?? []).Where(id => groups.All(group => group.Id != id));
        var hiddenPins = (PinnedGroups.GetValueOrDefault(scope) ?? []).Where(id => groups.All(group => group.Id != id));
        GroupOrder[scope] = remaining.Select(group => group.Id).Concat(hiddenOrder).ToList();
        PinnedGroups[scope] = remaining.Where(group => IsGroupPinned(scope, group.Id)).Select(group => group.Id).Concat(hiddenPins).ToList();
    }
    public void MoveVariable(VariableScope scope, string name, string sourceGroupId, string targetGroupId, string? beforePinnedName = null)
    {
        if (!VisibleGroups(scope).Any(group => group.Id == targetGroupId)) throw new ArgumentException("找不到目标分组。");
        var pinned = IsVariablePinned(scope, name);
        if (sourceGroupId == targetGroupId && !pinned) throw new ArgumentException("普通变量按名称排序，只能拖到其他分组。");
        if (beforePinnedName is not null && (!pinned || !IsVariablePinned(scope, beforePinnedName))) throw new ArgumentException("只能调整置顶变量之间的顺序。");
        Assignments[Key(scope, name)] = targetGroupId;
        if (!pinned) return;
        var key = Key(scope, name);
        if (beforePinnedName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true) return;
        PinnedVariables.RemoveAll(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
        var index = beforePinnedName is null ? PinnedVariables.Count : Position(PinnedVariables, Key(scope, beforePinnedName));
        PinnedVariables.Insert(Math.Min(index, PinnedVariables.Count), key);
    }
}

public sealed class EnvironmentWorkspaceStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string FilePath { get; }
    private readonly string mutexName;
    public EnvironmentWorkspaceStore(string dataDirectory)
    {
        FilePath = Path.Combine(Path.GetFullPath(dataDirectory), "environment.json");
        mutexName = @"Local\PathMan.Environment." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())))[..24];
    }
    internal static T ReadDefault<T>(string name) => JsonSerializer.Deserialize<T>(typeof(EnvironmentWorkspaceStore).Assembly
        .GetManifestResourceStream($"Pathman.Core.EnvironmentVariables.Defaults.{name}.json")!, JsonOptions)!;
    private EnvironmentWorkspace Load()
    {
        var state = File.Exists(FilePath) ? JsonSerializer.Deserialize<EnvironmentWorkspace>(File.ReadAllText(FilePath), JsonOptions)
            ?? throw new InvalidDataException("环境变量配置为空。") : ReadDefault<EnvironmentWorkspace>("groups");
        state.Assignments = new(state.Assignments, StringComparer.OrdinalIgnoreCase);
        state.ExternalVariables = new(state.ExternalVariables, StringComparer.OrdinalIgnoreCase);
        if(state.LegacyAutoArrangePath is {} enabled)
        {
            state.AutoArrangeUserPath=state.AutoArrangeMachinePath=enabled;
            state.LegacyAutoArrangePath=null;
        }
        if(state.LegacyGroupExternalVariables is {} group)
        {state.GroupExternalUserVariables=state.GroupExternalMachineVariables=group;state.LegacyGroupExternalVariables=null;}
        if(state.LegacyRemovePathDuplicates is {} remove)
        {state.RemoveUserPathDuplicates=state.RemoveMachinePathDuplicates=remove;state.LegacyRemovePathDuplicates=null;}
        if(state.LegacyMovePathEntries is {} move)
        {state.MoveExternalUserPathEntries=state.MoveExternalMachinePathEntries=move;state.LegacyMovePathEntries=null;}
        if (state.Groups.Select(group => group.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Groups.Count ||
            state.Groups.Any(group => string.IsNullOrWhiteSpace(group.Id) || string.IsNullOrWhiteSpace(group.Name)) ||
            !state.Groups.Any(group => group.Rule == "fallback" && group.Scope is null && !group.RequiresMonitoring))
            throw new InvalidDataException("分组配置必须包含唯一 ID、有效名称和一个通用默认分组。");
        return state;
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) throw new IOException("环境变量配置正在使用中，请稍后重试。"); }
        catch (AbandonedMutexException) { }
        try { return action(); } finally { mutex.ReleaseMutex(); }
    }
    public EnvironmentWorkspace Read() => Locked(Load);
    public void Update(Action<EnvironmentWorkspace> change) => Locked(() =>
    {
        var state = Load(); change(state); Save(state); return true;
    });
    internal void Inspect(Func<EnvironmentWorkspace, bool> change) => Locked(() =>
    {
        var state = Load(); if (change(state)) Save(state); return true;
    });
    private void Save(EnvironmentWorkspace state)
    {
        state.Revision = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false)); File.Move(temporary, FilePath, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
