using Microsoft.Win32;
using Pathman.Core.EnvironmentVariables;

namespace Pathman.Core;

public sealed class StartupManager(PathmanLayout layout)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PathMan";
    private string Command => $"\"{layout.BackgroundExecutable}\"";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if(enabled && !new EnvironmentWorkspaceStore(layout.DataDirectory).Read().MonitoringEnabled)
            throw new IOException("请先启用“在后台运行监控程序”。");
        if (enabled && !File.Exists(layout.BackgroundExecutable))
            throw new FileNotFoundException("后台程序不存在，请先构建或安装 PathMan。", layout.BackgroundExecutable);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else if (IsEnabled) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
    public void Reconcile()
    {if(IsEnabled && !new EnvironmentWorkspaceStore(layout.DataDirectory).Read().MonitoringEnabled) SetEnabled(false);}
    public static bool CanRun(EnvironmentWorkspace state,bool startupEnabled)=>state.MonitoringEnabled && startupEnabled;
}
