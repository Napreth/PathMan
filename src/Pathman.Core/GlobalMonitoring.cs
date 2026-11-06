using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Pathman.Core.EnvironmentVariables;

namespace Pathman.Core;

public sealed record GlobalMonitorSettings(bool Enabled=false,bool ProvideNonAdministratorOrigins=false,bool AutoArrangeMachinePath=false,string InstallationDirectory="")
{
    public bool RemoveMachinePathDuplicates {get;init;}=true;
    public bool MoveExternalMachinePathEntries {get;init;}=true;
    public bool IgnoreSystemEditorMachineChanges {get;init;}
    public bool IgnorePowerToysMachineChanges {get;init;}
    public string ExternalMachinePathVariable {get;init;}="EXTERNAL_PATH";
    public bool ApplyMachineOptions(EnvironmentWorkspace state)
    {
        var changed=!state.MonitoringEnabled || !state.TrackChangeOrigins || state.AutoArrangeMachinePath!=AutoArrangeMachinePath ||
            state.RemoveMachinePathDuplicates!=RemoveMachinePathDuplicates || state.MoveExternalMachinePathEntries!=MoveExternalMachinePathEntries ||
            state.IgnoreSystemEditorMachineChanges!=IgnoreSystemEditorMachineChanges || state.IgnorePowerToysMachineChanges!=IgnorePowerToysMachineChanges || state.ExternalMachinePathVariable!=ExternalMachinePathVariable;
        state.MonitoringEnabled=true;state.TrackChangeOrigins=true;state.AutoArrangeMachinePath=AutoArrangeMachinePath;
        state.RemoveMachinePathDuplicates=RemoveMachinePathDuplicates;state.MoveExternalMachinePathEntries=MoveExternalMachinePathEntries;
        state.IgnoreSystemEditorMachineChanges=IgnoreSystemEditorMachineChanges;state.IgnorePowerToysMachineChanges=IgnorePowerToysMachineChanges;
        state.ExternalMachinePathVariable=ExternalMachinePathVariable;
        return changed;
    }
}

public sealed class GlobalMonitoring(PathmanLayout layout)
{
    public const string ServiceName="PathMan.Monitor";
    public static string DataDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"PathMan","monitor");
    public static string SettingsPath=>Path.Combine(DataDirectory,"settings.json");
    public static string MachineDirectory=>Path.Combine(DataDirectory,"machine");
    public static string StatusPath=>Path.Combine(DataDirectory,"status.json");
    public bool IsAllUsers
    {
        get
        {
            var file=Path.Combine(layout.InternalDirectory,"install.ini");
            return File.Exists(file) && File.ReadLines(file).Any(line=>line.Trim().Equals("Scope=AllUsers",StringComparison.OrdinalIgnoreCase));
        }
    }
    public GlobalMonitorSettings Read()
    {
        var settings=ReadSettings();
        return settings.InstallationDirectory.Equals(layout.InstallationDirectory,StringComparison.OrdinalIgnoreCase) ? settings : new();
    }
    public static GlobalMonitorSettings ReadSettings()
    {
        if(!File.Exists(SettingsPath)) return new();
        using var stream=File.Open(SettingsPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        return JsonSerializer.Deserialize<GlobalMonitorSettings>(stream,EnvironmentWorkspaceStore.JsonOptions) ?? new();
    }
    public bool IsEnabled=>ReadSettings().Enabled;
    public static EnvironmentWorkspace EffectiveOptions(EnvironmentWorkspace state)
    {
        var global=ReadSettings();
        if(global.Enabled) {state.AutoArrangeUserPath&=state.MonitoringEnabled;global.ApplyMachineOptions(state);}
        return state;
    }
    public void Configure(GlobalMonitorSettings settings)
    {
        if(!IsAllUsers) throw new IOException(L.Get("全局监控仅支持为所有用户安装。"));
        using var source=PrivilegedServiceSource.Open(layout);
        var start=new ProcessStartInfo(layout.BackgroundExecutable) {UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--configure-global-monitor");
        foreach(var value in new[]{settings.Enabled,settings.ProvideNonAdministratorOrigins,settings.AutoArrangeMachinePath,settings.RemoveMachinePathDuplicates,settings.MoveExternalMachinePathEntries,settings.IgnoreSystemEditorMachineChanges,settings.IgnorePowerToysMachineChanges}) start.ArgumentList.Add(value.ToString());
        ExternalPathOrganizer.ValidateTarget(settings.ExternalMachinePathVariable);start.ArgumentList.Add(settings.ExternalMachinePathVariable.Trim());
        using var process=Process.Start(start) ?? throw new IOException(L.Get("无法配置全局监控服务。"));
        if(!process.WaitForExit(60000) || process.ExitCode!=0) throw new IOException(L.Get("无法配置全局监控服务。"));
    }
    private string ProtectedDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PathMan Monitor");
    public void ConfigureElevated(bool enabled,bool provideOrigins,bool organize,bool? removeDuplicates=null,bool? moveEntries=null,bool? ignoreSystemEditor=null,bool? ignorePowerToys=null,string? targetVariable=null)
    {
        DemandAdministrator();
        using var mutex=WindowsUserContext.WorkspaceMutex(@"Global\PathMan.GlobalMonitor.Configuration");
        try {if(!mutex.WaitOne(60000)) throw new IOException("Global configuration is busy.");}catch(AbandonedMutexException) { }
        try {ConfigureLocked(enabled,provideOrigins,organize,removeDuplicates,moveEntries,ignoreSystemEditor,ignorePowerToys,targetVariable);}finally {mutex.ReleaseMutex();}
    }
    private void ConfigureLocked(bool enabled,bool provideOrigins,bool organize,bool? removeDuplicates,bool? moveEntries,bool? ignoreSystemEditor,bool? ignorePowerToys,string? targetVariable)
    {
        if(!IsAllUsers) throw new IOException(L.Get("全局监控仅支持为所有用户安装。"));
        if(targetVariable is not null) ExternalPathOrganizer.ValidateTarget(targetVariable);
        using var source=enabled ? PrivilegedServiceSource.Open(layout) : null;
        source?.ValidateDestination(ProtectedDirectory);
        OriginTrackingTask.SecureDirectory(Path.GetDirectoryName(DataDirectory)!);
        OriginTrackingTask.SecureDirectory(DataDirectory);
        OriginTrackingTask.SecureDirectory(MachineDirectory);
        SecureExistingData(DataDirectory);
        var previous=ReadSettings();
        if(previous.Enabled && !previous.InstallationDirectory.Equals(layout.InstallationDirectory,StringComparison.OrdinalIgnoreCase)) throw new IOException("Another installation owns global monitoring.");
        var updated=previous with {Enabled=enabled,ProvideNonAdministratorOrigins=provideOrigins,AutoArrangeMachinePath=organize,InstallationDirectory=layout.InstallationDirectory,
            RemoveMachinePathDuplicates=removeDuplicates ?? previous.RemoveMachinePathDuplicates,MoveExternalMachinePathEntries=moveEntries ?? previous.MoveExternalMachinePathEntries,
            IgnoreSystemEditorMachineChanges=ignoreSystemEditor ?? previous.IgnoreSystemEditorMachineChanges,IgnorePowerToysMachineChanges=ignorePowerToys ?? previous.IgnorePowerToysMachineChanges,
            ExternalMachinePathVariable=targetVariable?.Trim() ?? previous.ExternalMachinePathVariable};
        if(enabled)
        {
            var executable=Path.Combine(ProtectedDirectory,"PathMan.Service.exe");
            Stop();source!.Deploy(ProtectedDirectory);
            var command=$"\"{executable}\" --system-monitor";
            var exists=Sc("query",ServiceName)==0;
            Checked(exists ? "config" : "create",ServiceName,"binPath=",command,"start=","auto","obj=","LocalSystem","DisplayName=","PathMan Monitor");
            // Restart automatically after a crash; an ordinary user cannot edit the service.
            Checked("failure",ServiceName,"reset=","86400","actions=","restart/5000/restart/15000/restart/60000");
            Checked("failureflag",ServiceName,"1");
        }
        WriteSettings(updated);
        try
        {
            if(enabled) {var result=Sc("start",ServiceName);if(result is not (0 or 1056)) throw new Win32Exception(result);}
            else {Stop();if(Sc("query",ServiceName)==0) Checked("config",ServiceName,"start=","demand");}
        }
        catch {WriteSettings(previous);throw;}
    }
    public void Maintain()
    {
        var state=Read();
        if(state.Enabled) ConfigureElevated(true,state.ProvideNonAdministratorOrigins,state.AutoArrangeMachinePath);
    }
    public void Remove()
    {
        var state=ReadSettings();
        if(!state.InstallationDirectory.Equals(layout.InstallationDirectory,StringComparison.OrdinalIgnoreCase)) return;
        DemandAdministrator();Stop();
        if(Sc("query",ServiceName)==0) Checked("delete",ServiceName);
        WriteSettings(state with {Enabled=false});
        // Delete only the fixed, protected executable directory owned by this service.
        if(Directory.Exists(ProtectedDirectory))
        {
            if((File.GetAttributes(ProtectedDirectory)&FileAttributes.ReparsePoint)!=0) throw new IOException("Invalid service cleanup directory.");
            OriginTrackingTask.DeleteStoppedDirectory(ProtectedDirectory);
        }
    }
    public static void Stop()
    {
        var manager=OpenSCManager(null,null,1);
        if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service=OpenService(manager,ServiceName,4);
            if(service==IntPtr.Zero)
            {
                var error=Marshal.GetLastWin32Error();
                if(error==1060) return;
                throw new Win32Exception(error);
            }
            try
            {
                var elapsed=Stopwatch.StartNew();var requested=false;
                while(true)
                {
                    if(!QueryServiceStatusEx(service,0,out var status,Marshal.SizeOf<ServiceStatus>(),out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if(status.State==1) return;
                    if(elapsed.Elapsed>=TimeSpan.FromSeconds(30)) throw new IOException($"Global monitor did not stop (SCM state {status.State}).");
                    // Pending services cannot accept STOP. A successful request must
                    // also finish before deployment or service removal can continue.
                    if(!requested && status.State is 4 or 7)
                    {
                        var result=Sc("stop",ServiceName);
                        if(result==0) requested=true;
                        else if(result is not (1060 or 1062))
                        {
                            if(result is not (1052 or 1061) ||
                                !QueryServiceStatusEx(service,0,out status,Marshal.SizeOf<ServiceStatus>(),out _) || status.State is not (1 or 2 or 3 or 5 or 6))
                                throw new Win32Exception(result);
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            finally {CloseServiceHandle(service);}
        }
        finally {CloseServiceHandle(manager);}
    }
    public static string? GetUnavailableMessage()
    {
        if(!ReadSettings().Enabled) return null;
        var manager=OpenSCManager(null,null,1);
        if(manager==IntPtr.Zero) return L.Get("无法查询全局监控服务状态。");
        try
        {
            var service=OpenService(manager,ServiceName,4);
            if(service==IntPtr.Zero) return L.Get("全局监控已启用，但服务未运行。请由管理员检查或重新启用全局设置。");
            try
            {
                if(!QueryServiceStatusEx(service,0,out var status,Marshal.SizeOf<ServiceStatus>(),out _)) return L.Get("无法查询全局监控服务状态。");
                if(status.State==2) return L.Get("全局监控服务正在启动或恢复，请稍候。");
                if(status.State!=4) return L.Get("全局监控已启用，但服务未运行。请由管理员检查或重新启用全局设置。");
                using var stream=File.Open(StatusPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var document=JsonDocument.Parse(stream);var root=document.RootElement;
                if(!root.GetProperty("Running").GetBoolean() || root.GetProperty("Time").GetDateTime()<DateTime.UtcNow.AddSeconds(-15) ||
                    root.TryGetProperty("ProcessId",out var pid) && pid.GetUInt32()!=status.ProcessId)
                    return L.Get("全局监控服务未正常响应，请由管理员检查或重新启用全局设置。");
                return root.TryGetProperty("Error",out var error) && error.ValueKind==JsonValueKind.String ? error.GetString() : null;
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
            {return L.Get("全局监控服务未正常响应，请由管理员检查或重新启用全局设置。");}
            finally {CloseServiceHandle(service);}
        }
        finally {CloseServiceHandle(manager);}
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus {public uint Type,State,Accepted,Exit,Specific,Checkpoint,Wait,ProcessId,Flags;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenService(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool QueryServiceStatusEx(IntPtr service,int level,out ServiceStatus status,int size,out int needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    private static void WriteSettings(GlobalMonitorSettings settings)
    {
        var temporary=SettingsPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(settings,EnvironmentWorkspaceStore.JsonOptions),new UTF8Encoding(false));
        File.Move(temporary,SettingsPath,true);
    }
    private static void SecureExistingData(string directory)
    {
        foreach(var child in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes=File.GetAttributes(child);
            if((attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Global monitor data cannot contain reparse points.");
            if((attributes&FileAttributes.Directory)!=0) {OriginTrackingTask.SecureDirectory(child);SecureExistingData(child);continue;}
            var file=new FileInfo(child);var security=new FileSecurity();
            var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
            security.SetOwner(admins);security.SetAccessRuleProtection(true,false);
            foreach(var principal in new[]{admins,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)}) security.AddAccessRule(new(principal,FileSystemRights.FullControl,AccessControlType.Allow));
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid,null),FileSystemRights.ReadAndExecute,AccessControlType.Allow));
            try {file.SetAccessControl(security);}catch(FileNotFoundException) { } // An atomic service write may have replaced this file.
        }
    }
    private static int Sc(params string[] arguments)=>ScOutput(arguments).Code;
    private static void Checked(params string[] arguments) {var result=ScOutput(arguments);if(result.Code!=0) throw new IOException(result.Output);}
    private static (int Code,string Output) ScOutput(params string[] arguments)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe")) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments) start.ArgumentList.Add(argument);
        using var process=Process.Start(start) ?? throw new IOException("Cannot start the service controller.");
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(20000)) throw new IOException("Service controller timeout.");
        return (process.ExitCode,output.GetAwaiter().GetResult()+error.GetAwaiter().GetResult());
    }
    private static void DemandAdministrator() {if(!WindowsUserContext.IsElevatedAdministrator()) throw new Win32Exception(740);}
}
