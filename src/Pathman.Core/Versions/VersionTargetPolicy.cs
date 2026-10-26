using Pathman.Core.EnvironmentVariables;

namespace Pathman.Core.Versions;

public static class VersionTargetPolicy
{
    public static bool IsBuiltIn(string name)=>EnvironmentVariableClassifier.IsWindowsBuiltIn(name);
    public static string? Restriction(string name,bool allowBuiltIns)
    {
        if(name.Equals("PATH",StringComparison.OrdinalIgnoreCase)) return "PATH 不能作为多版本管理的目标变量。";
        if(IsBuiltIn(name) && !allowBuiltIns) return "此变量属于内置系统变量。可在“关于 → 高级设置”中解锁，PATH 始终不可选。";
        return null;
    }
    public static void EnsureAllowed(string name,EnvironmentWorkspace state)
    {
        if(name.Length==0) throw new ArgumentException("请先选择目标环境变量。");
        WindowsEnvironmentVariableEditor.ValidateName(name);
        if(Restriction(name,state.AllowBuiltInVersionTargets) is {} reason) throw new ArgumentException(reason);
    }
}
