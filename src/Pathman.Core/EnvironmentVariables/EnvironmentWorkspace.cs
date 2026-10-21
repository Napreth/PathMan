using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed class VariableGroupDefinition
{
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
    public bool MonitoringEnabled { get; set; }
    public List<VariableSnapshot> Snapshot { get; set; } = [];
    public HashSet<string> ExternalVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VariableChange> Changes { get; set; } = [];
    public string Revision { get; set; } = "";
    public static string Key(VariableScope scope, string name) => $"{scope}:{name}";
    public IEnumerable<VariableGroupDefinition> VisibleGroups(VariableScope scope) => Groups.Where(group =>
        (group.Scope is null || group.Scope == scope) && (!group.RequiresMonitoring || MonitoringEnabled));
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
