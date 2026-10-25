using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Pathman.Core.Models;
using System.Security.Principal;
using System.Text.Json;

namespace Pathman.Core.EnvironmentVariables;

public sealed class EnvironmentMonitor(EnvironmentWorkspaceStore store)
{
    private readonly HashSet<string> failedPlans=[];
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
    public void Poll(IEnvironmentChangeOrigins? origins=null)
    {
        var paused=false;
        store.Inspect(state=>
        {
            if(!state.MonitoringEnabled) return false;
            if(state.RegistryWriteInProgressUntil>DateTime.UtcNow) {paused=true;return false;}
            var current=ReadAll();
            var before=state.Snapshot.ToArray();
            var changed=Compare(state,current,origins);
            return PreparePlans(state,before,current,changed,origins:origins) || changed;
        });
        if(paused) return;
        var saved=store.Read();
        if(!saved.MonitoringEnabled || !saved.AnyPathOrganizationEnabled) return;
        using var identity=WindowsIdentity.GetCurrent();
        var administrator=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        foreach(var plan in saved.PendingPathOrganizations)
        {
            if(!saved.IsPathOrganizationEnabled(plan.Scope) || RequiresOrigin(saved,plan.Scope) && origins?.IsReady!=true) continue;
            if(failedPlans.Contains(plan.Id)) continue;
            if(plan.Scope==VariableScope.Machine && !administrator) continue;
            try
            {
                Backup(store,plan);
                WindowsEnvironmentVariableEditor.ApplyBatch(plan.Mutations,plan.Preconditions,store,state=>
                {
                    if(!state.MonitoringEnabled || ExternalPathOrganizer.Policy(state,plan.Scope)!=plan.Policy || !state.PendingPathOrganizations.Any(item=>item.Id==plan.Id))
                        throw new IOException("监控设置或待整理项目已改变，本次整理已跳过。");
                });
                Complete(store,plan);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {failedPlans.Add(plan.Id);ReportError("自动整理未完成："+error.Message);}
        }
        failedPlans.RemoveWhere(id=>!saved.PendingPathOrganizations.Any(plan=>plan.Id==id));
    }
    internal static bool PreparePlans(EnvironmentWorkspace state,IReadOnlyList<VariableSnapshot> before,
        IReadOnlyList<EnvironmentVariableInfo> current,bool changed,IReadOnlyDictionary<string,string>? runtime=null,IEnvironmentChangeOrigins? origins=null)
    {
        if(!state.AnyPathOrganizationEnabled)
        {var had=state.PendingPathOrganizations.Count>0;state.PendingPathOrganizations.Clear();return had;}
        var updated=false;
        foreach(var scope in new[] {VariableScope.User,VariableScope.Machine})
        {
            if(!state.IsPathOrganizationEnabled(scope))
            {updated|=state.PendingPathOrganizations.RemoveAll(item=>item.Scope==scope)>0;continue;}
            var pending=state.PendingPathOrganizations.FirstOrDefault(item=>item.Scope==scope);
            var old=before.FirstOrDefault(item=>item.Scope==scope && item.Name.Equals("Path",StringComparison.OrdinalIgnoreCase));
            var path=current.FirstOrDefault(item=>item.Scope==scope && item.Name.Equals("Path",StringComparison.OrdinalIgnoreCase));
            var pathChanged=changed && old?.Value!=path?.RawValue;
            var writers=pathChanged ? origins?.Take(scope,"Path") ?? [] : pending?.Origins ?? [];
            if(writers.Any(writer=>writer.Excluded(state)) || pathChanged && RequiresOrigin(state,scope) && (origins?.IsReady!=true || writers.Count==0 || writers.Any(writer=>!writer.IsIdentified)))
            {
                updated|=state.PendingPathOrganizations.RemoveAll(item=>item.Scope==scope)>0;
                if(writers.Count==0 || writers.Any(writer=>!writer.IsIdentified)) Message(state,"未能确认本次 PATH 修改来源，已保留外部更改，未自动整理。");
                continue;
            }
            if(pending is null && (!changed || old?.Value==path?.RawValue)) continue;
            if(pending is not null && !changed && pending.Policy==ExternalPathOrganizer.Policy(state,scope) &&
                pending.Mutations.All(mutation=>current.FirstOrDefault(item=>item.Scope==scope && item.Name.Equals(mutation.Name,StringComparison.OrdinalIgnoreCase))?.RawValue==mutation.ExpectedValue)) continue;
            var plan=ExternalPathOrganizer.Plan(scope,pending is null ? old : pending.BeforePath,current,state,runtime) with {Origins=writers};
            state.PendingPathOrganizations.RemoveAll(item=>item.Scope==scope);
            if(plan.Mutations.Count>0) state.PendingPathOrganizations.Add(plan);
            if(plan.Warning is not null) Message(state,plan.Warning);
            updated=true;
        }
        return updated;
    }
    public static bool RequiresOrigin(EnvironmentWorkspace state,VariableScope? scope=null)=>state.TrackChangeOrigins &&
        (scope is {} selected ? state.ShouldIgnoreSystemEditor(selected) || state.ShouldIgnorePowerToys(selected) :
            new[] {VariableScope.User,VariableScope.Machine}.Where(state.IsPathOrganizationEnabled).Any(item=>state.ShouldIgnoreSystemEditor(item) || state.ShouldIgnorePowerToys(item)));
    public static void Backup(EnvironmentWorkspaceStore store,ExternalPathOrganizationPlan plan)
    {
        var directory=Path.Combine(Path.GetDirectoryName(store.FilePath)!,"backups");Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,$"path-monitor-{plan.Id}.json");
        if(!File.Exists(file)) File.WriteAllText(file,JsonSerializer.Serialize(plan,EnvironmentWorkspaceStore.JsonOptions));
    }
    public static void Complete(EnvironmentWorkspaceStore store,ExternalPathOrganizationPlan plan)=>store.Update(state=>
    {
        state.PendingPathOrganizations.RemoveAll(item=>item.Id==plan.Id);
        if(plan.ChangesPathOrder) Message(state,"自动整理改变了 PATH 的路径搜索顺序，可能导致系统或程序调用到不同的命令或版本。原始值已备份，可在外部更改记录中核对。" );
        else if(state.PendingPathOrganizations.Count==0) {state.MonitorMessage=null;state.MonitorMessageTime=null;}
    });
    public void ReportError(string message)=>store.Inspect(state=>
    {if(state.MonitorMessage==message) return false;Message(state,message);return true;});
    private static void Message(EnvironmentWorkspace state,string message)
    {if(state.MonitorMessage==message) return;state.MonitorMessage=message;state.MonitorMessageTime=DateTime.Now;}
    private static VariableSnapshot Snapshot(EnvironmentVariableInfo item) => new(item.Scope, item.Name, item.RawValue, (int)item.Kind);
    internal static bool Compare(EnvironmentWorkspace state, IReadOnlyList<EnvironmentVariableInfo> current,IEnvironmentChangeOrigins? origins=null)
    {
        var before = state.Snapshot.ToDictionary(item => EnvironmentWorkspace.Key(item.Scope, item.Name), StringComparer.OrdinalIgnoreCase);
        var after = current.ToDictionary(item => EnvironmentWorkspace.Key(item.Scope, item.Name), StringComparer.OrdinalIgnoreCase);
        var changes = new List<VariableChange>();
        foreach (var item in current)
        {
            var key = EnvironmentWorkspace.Key(item.Scope, item.Name);
            if (!before.TryGetValue(key, out var old) || old.Value != item.RawValue || old.Kind != (int)item.Kind)
            {
                changes.Add(new(DateTime.Now, item.Scope, item.Name, old is null ? "create" : "set", old?.Value, item.RawValue, "外部程序") {Origins=origins?.Take(item.Scope,item.Name) ?? []});
                if(old is null) state.ExternalVariables.Add(key);
            }
        }
        foreach (var old in state.Snapshot.Where(item => !after.ContainsKey(EnvironmentWorkspace.Key(item.Scope, item.Name))))
        {
            changes.Add(new(DateTime.Now, old.Scope, old.Name, "delete", old.Value, null, "外部程序") {Origins=origins?.Take(old.Scope,old.Name) ?? []});
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
