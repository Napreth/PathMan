using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed class EnvironmentMonitor(EnvironmentWorkspaceStore store)
{
    public static IReadOnlyList<EnvironmentVariableInfo> ReadAll()
    {
        var reader = new WindowsEnvironmentVariableReader();
        return reader.Read(VariableScope.User).Concat(reader.Read(VariableScope.Machine)).ToArray();
    }
    public void SetEnabled(bool enabled) => store.Update(state =>
    {
        state.MonitoringEnabled = enabled;
        if (enabled) state.Snapshot = ReadAll().Select(Snapshot).ToList();
    });
    public void Poll() => store.Inspect(state => state.MonitoringEnabled && Compare(state, ReadAll()));
    private static VariableSnapshot Snapshot(EnvironmentVariableInfo item) => new(item.Scope, item.Name, item.RawValue, (int)item.Kind);
    internal static bool Compare(EnvironmentWorkspace state, IReadOnlyList<EnvironmentVariableInfo> current)
    {
        var before = state.Snapshot.ToDictionary(item => EnvironmentWorkspace.Key(item.Scope, item.Name), StringComparer.OrdinalIgnoreCase);
        var after = current.ToDictionary(item => EnvironmentWorkspace.Key(item.Scope, item.Name), StringComparer.OrdinalIgnoreCase);
        var changes = new List<VariableChange>();
        foreach (var item in current)
        {
            var key = EnvironmentWorkspace.Key(item.Scope, item.Name);
            if (!before.TryGetValue(key, out var old) || old.Value != item.RawValue || old.Kind != (int)item.Kind)
            {
                changes.Add(new(DateTime.Now, item.Scope, item.Name, old is null ? "create" : "set", old?.Value, item.RawValue, "外部程序"));
                state.ExternalVariables.Add(key);
            }
        }
        foreach (var old in state.Snapshot.Where(item => !after.ContainsKey(EnvironmentWorkspace.Key(item.Scope, item.Name))))
        {
            changes.Add(new(DateTime.Now, old.Scope, old.Name, "delete", old.Value, null, "外部程序"));
            state.ExternalVariables.Remove(EnvironmentWorkspace.Key(old.Scope, old.Name));
        }
        if (changes.Count == 0) return false;
        state.Snapshot = current.Select(Snapshot).ToList();
        state.Changes.InsertRange(0, changes);
        if (state.Changes.Count > 200) state.Changes.RemoveRange(200, state.Changes.Count - 200);
        return true;
    }
}

public sealed class EnvironmentRegistrySignal : IDisposable
{
    private readonly RegistryKey? user = Registry.CurrentUser.OpenSubKey("Environment");
    private readonly RegistryKey? machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
    public AutoResetEvent UserChanged { get; } = new(false);
    public AutoResetEvent MachineChanged { get; } = new(false);
    public EnvironmentRegistrySignal() { Arm(0); Arm(1); }
    public void Arm(int scope)
    {
        var key=scope==0 ? user : machine;
        var changed=scope==0 ? UserChanged : MachineChanged;
        if (key is not null) RegNotifyChangeKeyValue(key.Handle, false, 4u | 0x10000000u, changed.SafeWaitHandle, true);
    }
    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool subtree,
        uint filter, SafeWaitHandle signal, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
    public void Dispose() { user?.Dispose(); machine?.Dispose(); UserChanged.Dispose(); MachineChanged.Dispose(); }
}
