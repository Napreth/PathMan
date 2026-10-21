using Microsoft.Win32;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed class WindowsEnvironmentVariableReader
{
    public IReadOnlyList<EnvironmentVariableInfo> Read(VariableScope scope)
    {
        // Read the two persisted scopes independently, rather than the merged
        // environment inherited by this process. Reading needs no elevation.
        using var key = scope switch
        {
            VariableScope.User => Registry.CurrentUser.OpenSubKey("Environment", writable: false),
            VariableScope.Machine => Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: false),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
        return key is null ? [] : ReadKey(key, scope);
    }

    internal static IReadOnlyList<EnvironmentVariableInfo> ReadKey(RegistryKey key, VariableScope scope)
    {
        var variables = new List<EnvironmentVariableInfo>();
        foreach (var name in key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0) continue;
            // Leave %NAME% references intact. DWORD and other registry metadata
            // are not Windows string environment variables.
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                variables.Add(new EnvironmentVariableInfo(name, value, scope, key.GetValueKind(name)));
        }
        return variables.AsReadOnly();
    }
}
