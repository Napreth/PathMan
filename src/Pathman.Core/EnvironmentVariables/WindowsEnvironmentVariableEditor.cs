using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed record EnvironmentMutation(string Action, VariableScope Scope, string Name,
    string? Value = null, string? NewName = null, string? ExpectedValue = null, RegistryValueKind? ExpectedKind = null);

public static class WindowsEnvironmentVariableEditor
{
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.IndexOfAny(['=', '\0', '\r', '\n']) >= 0)
            throw new ArgumentException("变量名不能为空，最长 255 个字符，不能包含等号、空字符或换行。");
    }
    public static void Apply(EnvironmentMutation mutation, EnvironmentWorkspaceStore store)
    {
        if (mutation.Scope is not (VariableScope.User or VariableScope.Machine)) throw new ArgumentOutOfRangeException(nameof(mutation));
        store.Update(state =>
        {
            using var key = mutation.Scope == VariableScope.User ? Registry.CurrentUser.CreateSubKey("Environment", writable: true) :
                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: true)
                    ?? throw new IOException("找不到系统环境变量注册表项。");
            ApplyKey(key, mutation);
            RecordOwnChange(state, mutation);
        });
        Broadcast();
    }
    public static void ApplyBatch(IReadOnlyList<EnvironmentMutation> mutations, IReadOnlyList<VariableSnapshot> preconditions, EnvironmentWorkspaceStore store)
    {
        if (mutations.Count == 0) return;
        var scope = mutations[0].Scope;
        if (scope is not (VariableScope.User or VariableScope.Machine) || mutations.Any(item => item.Scope != scope || item.Action is not ("create" or "set")))
            throw new ArgumentException("批量保存仅支持同一作用域的创建和修改。");
        var keys = new Dictionary<VariableScope, RegistryKey>();
        Action? rollback = null;
        try
        {
            foreach (var requiredScope in preconditions.Select(item => item.Scope).Append(scope).Distinct())
            {
                var writable = requiredScope == scope;
                keys[requiredScope] = requiredScope == VariableScope.User
                    ? Registry.CurrentUser.CreateSubKey("Environment", writable)
                    : Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable)
                        ?? throw new IOException("找不到环境变量注册表项。");
            }
            store.Update(state =>
            {
                rollback = ApplyBatchKeys(keys, mutations, preconditions);
                foreach (var mutation in mutations) RecordOwnChange(state, mutation);
            });
        }
        catch { rollback?.Invoke(); throw; }
        finally { foreach (var key in keys.Values) key.Dispose(); }
        Broadcast();
    }
    // Also used with isolated registry fixtures. Validate every input before the first write.
    internal static Action ApplyBatchKeys(IReadOnlyDictionary<VariableScope, RegistryKey> keys,
        IReadOnlyList<EnvironmentMutation> mutations, IReadOnlyList<VariableSnapshot> preconditions)
    {
        if (mutations.Select(item => EnvironmentWorkspace.Key(item.Scope, item.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mutations.Count)
            throw new ArgumentException("同一批次不能多次修改一个变量。");
        foreach (var guard in preconditions)
        {
            var key = keys[guard.Scope];
            if (key.GetValue(guard.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value ||
                value != guard.Value || (int)key.GetValueKind(guard.Name) != guard.Kind)
                throw new IOException($"{guard.Name} 已被其他程序修改，请重新预览清理结果。");
        }
        foreach (var mutation in mutations)
        {
            if (mutation.Action is not ("create" or "set")) throw new ArgumentException("不支持此批量操作。");
            ValidateKey(keys[mutation.Scope], mutation);
        }
        var backups = mutations.Select(mutation =>
        {
            var key = keys[mutation.Scope];
            var name = key.GetValueNames().FirstOrDefault(name => name.Equals(mutation.Name, StringComparison.OrdinalIgnoreCase));
            return (Key: key, Mutation: mutation, Name: name, Value: name is null ? null : key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                Kind: name is null ? RegistryValueKind.String : key.GetValueKind(name));
        }).ToArray();
        var written = 0;
        void Rollback()
        {
            foreach (var item in backups.Take(written).Reverse())
            {
                // Never overwrite another writer while unwinding a failed batch.
                if (item.Key.GetValue(item.Mutation.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != item.Mutation.Value) continue;
                if (item.Name is null) item.Key.DeleteValue(item.Mutation.Name, false);
                else item.Key.SetValue(item.Name, item.Value!, item.Kind);
            }
        }
        try { foreach (var mutation in mutations) { ApplyKey(keys[mutation.Scope], mutation); written++; } }
        catch { Rollback(); throw; }
        return Rollback;
    }
    internal static void ApplyKey(RegistryKey key, EnvironmentMutation mutation)
    {
        ValidateKey(key, mutation);
        var existingName = key.GetValueNames().FirstOrDefault(name => name.Equals(mutation.Name, StringComparison.OrdinalIgnoreCase));
        var existing = existingName is null ? null : key.GetValue(existingName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        switch (mutation.Action)
        {
            case "create": case "set":
                var kind = mutation.Value?.Contains('%') == true ? RegistryValueKind.ExpandString : mutation.ExpectedKind ?? RegistryValueKind.String;
                key.SetValue(existingName ?? mutation.Name, mutation.Value ?? "", kind);
                break;
            case "delete": key.DeleteValue(existingName!); break;
            case "rename":
                ValidateName(mutation.NewName ?? "");
                var conflict = key.GetValueNames().FirstOrDefault(name => name.Equals(mutation.NewName, StringComparison.OrdinalIgnoreCase));
                if (conflict is not null && !conflict.Equals(existingName, StringComparison.OrdinalIgnoreCase)) throw new IOException("新名称已经存在。");
                var originalKind = key.GetValueKind(existingName!);
                var caseOnly = mutation.Name.Equals(mutation.NewName, StringComparison.OrdinalIgnoreCase);
                if (caseOnly) break;
                try { key.SetValue(mutation.NewName!, existing!, originalKind); key.DeleteValue(existingName!); }
                catch { key.SetValue(existingName!, existing!, originalKind); key.DeleteValue(mutation.NewName!, false); throw; }
                break;
            default: throw new ArgumentException("未知的环境变量操作。");
        }
    }
    private static void ValidateKey(RegistryKey key, EnvironmentMutation mutation)
    {
        ValidateName(mutation.Name);
        if (mutation.Value?.Contains('\0') == true || mutation.Value?.Length > 32767) throw new ArgumentException("变量值不能包含空字符，且最长为 32767 个字符。");
        var existingName = key.GetValueNames().FirstOrDefault(name => name.Equals(mutation.Name, StringComparison.OrdinalIgnoreCase));
        var existing = existingName is null ? null : key.GetValue(existingName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (mutation.Action == "create")
        {
            if (existingName is not null) throw new IOException("该作用域中已存在同名环境变量。");
        }
        else
        {
            if (existingName is null || existing is not string text || text != mutation.ExpectedValue ||
                mutation.ExpectedKind is not null && key.GetValueKind(existingName) != mutation.ExpectedKind)
                throw new IOException("此变量已被其他程序修改或删除，请刷新后重试。");
        }
    }
    private static void RecordOwnChange(EnvironmentWorkspace state, EnvironmentMutation mutation)
    {
        var oldKey = EnvironmentWorkspace.Key(mutation.Scope, mutation.Name);
        state.Snapshot.RemoveAll(item => item.Scope == mutation.Scope && item.Name.Equals(mutation.Name, StringComparison.OrdinalIgnoreCase));
        state.ExternalVariables.Remove(oldKey);
        if (mutation.Action == "rename")
        {
            var newKey = EnvironmentWorkspace.Key(mutation.Scope, mutation.NewName!);
            if (state.Assignments.Remove(oldKey, out var group)) state.Assignments[newKey] = group;
            var pin = state.PinnedVariables.FindIndex(item => item.Equals(oldKey, StringComparison.OrdinalIgnoreCase));
            if (pin >= 0) state.PinnedVariables[pin] = newKey;
        }
        else if (mutation.Action == "delete")
        {
            state.Assignments.Remove(oldKey);
            state.PinVariable(mutation.Scope, mutation.Name, false);
        }
        if (mutation.Action != "delete")
        {
            var name = mutation.Action == "rename" ? mutation.NewName! : mutation.Name;
            var value = mutation.Action == "rename" ? mutation.ExpectedValue! : mutation.Value ?? "";
            var kind = mutation.Action == "rename" ? mutation.ExpectedKind ?? RegistryValueKind.String :
                value.Contains('%') ? RegistryValueKind.ExpandString : mutation.ExpectedKind ?? RegistryValueKind.String;
            state.Snapshot.Add(new(mutation.Scope, name, value, (int)kind));
        }
        if (state.MonitoringEnabled)
        {
            state.Changes.Insert(0, new(DateTime.Now, mutation.Scope, mutation.Name, mutation.Action,
                mutation.ExpectedValue, mutation.Value ?? mutation.NewName, "PathMan"));
            if (state.Changes.Count > 200) state.Changes.RemoveRange(200, state.Changes.Count - 200);
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    private static void Broadcast() => SendMessageTimeout(new IntPtr(0xffff), 0x1a, UIntPtr.Zero, "Environment", 2, 200, out _);
}
