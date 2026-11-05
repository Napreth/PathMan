using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using Pathman.Core.EnvironmentVariables;

namespace Pathman.Core;

public sealed record GlobalMonitorSettings(bool Enabled=false,bool ProvideNonAdministratorOrigins=false,bool AutoArrangeMachinePath=false,string InstallationDirectory="")
{
    public bool RemoveMachinePathDuplicates {get;init;}=true;
    public bool MoveExternalMachinePathEntries {get;init;}=true;
    public bool IgnoreSystemEditorMachineChanges {get;init;}
    public bool IgnorePowerToysMachineChanges {get;init;}
    public bool ApplyMachineOptions(EnvironmentWorkspace state)
    {
        var changed=!state.MonitoringEnabled || !state.TrackChangeOrigins || state.AutoArrangeMachinePath!=AutoArrangeMachinePath ||
            state.RemoveMachinePathDuplicates!=RemoveMachinePathDuplicates || state.MoveExternalMachinePathEntries!=MoveExternalMachinePathEntries ||
            state.IgnoreSystemEditorMachineChanges!=IgnoreSystemEditorMachineChanges || state.IgnorePowerToysMachineChanges!=IgnorePowerToysMachineChanges;
        state.MonitoringEnabled=true;state.TrackChangeOrigins=true;state.AutoArrangeMachinePath=AutoArrangeMachinePath;
        state.RemoveMachinePathDuplicates=RemoveMachinePathDuplicates;state.MoveExternalMachinePathEntries=MoveExternalMachinePathEntries;
        state.IgnoreSystemEditorMachineChanges=IgnoreSystemEditorMachineChanges;state.IgnorePowerToysMachineChanges=IgnorePowerToysMachineChanges;
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
    public void Configure(GlobalMonitorSettings settings)
    {
        if(!IsAllUsers) throw new IOException(L.Get("全局监控仅支持为所有用户安装。"));
        var start=new ProcessStartInfo(layout.BackgroundExecutable) {UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--configure-global-monitor");
        foreach(var value in new[]{settings.Enabled,settings.ProvideNonAdministratorOrigins,settings.AutoArrangeMachinePath,settings.RemoveMachinePathDuplicates,settings.MoveExternalMachinePathEntries,settings.IgnoreSystemEditorMachineChanges,settings.IgnorePowerToysMachineChanges}) start.ArgumentList.Add(value.ToString());
        using var process=Process.Start(start) ?? throw new IOException(L.Get("无法配置全局监控服务。"));
        if(!process.WaitForExit(60000) || process.ExitCode!=0) throw new IOException(L.Get("无法配置全局监控服务。"));
    }
    private string ProtectedDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PathMan Monitor");
    public void ConfigureElevated(bool enabled,bool provideOrigins,bool organize,bool? removeDuplicates=null,bool? moveEntries=null,bool? ignoreSystemEditor=null,bool? ignorePowerToys=null)
    {
        DemandAdministrator();
        using var mutex=WindowsUserContext.WorkspaceMutex(@"Global\PathMan.GlobalMonitor.Configuration");
        try {if(!mutex.WaitOne(60000)) throw new IOException("Global configuration is busy.");}catch(AbandonedMutexException) { }
        try {ConfigureLocked(enabled,provideOrigins,organize,removeDuplicates,moveEntries,ignoreSystemEditor,ignorePowerToys);}finally {mutex.ReleaseMutex();}
    }
    private void ConfigureLocked(bool enabled,bool provideOrigins,bool organize,bool? removeDuplicates,bool? moveEntries,bool? ignoreSystemEditor,bool? ignorePowerToys)
    {
        if(!IsAllUsers) throw new IOException(L.Get("全局监控仅支持为所有用户安装。"));
        OriginTrackingTask.SecureDirectory(Path.GetDirectoryName(DataDirectory)!);
        OriginTrackingTask.SecureDirectory(DataDirectory);
        OriginTrackingTask.SecureDirectory(MachineDirectory);
        SecureExistingData(DataDirectory);
        var previous=ReadSettings();
        if(previous.Enabled && !previous.InstallationDirectory.Equals(layout.InstallationDirectory,StringComparison.OrdinalIgnoreCase)) throw new IOException("Another installation owns global monitoring.");
        var updated=previous with {Enabled=enabled,ProvideNonAdministratorOrigins=provideOrigins,AutoArrangeMachinePath=organize,InstallationDirectory=layout.InstallationDirectory,
            RemoveMachinePathDuplicates=removeDuplicates ?? previous.RemoveMachinePathDuplicates,MoveExternalMachinePathEntries=moveEntries ?? previous.MoveExternalMachinePathEntries,
            IgnoreSystemEditorMachineChanges=ignoreSystemEditor ?? previous.IgnoreSystemEditorMachineChanges,IgnorePowerToysMachineChanges=ignorePowerToys ?? previous.IgnorePowerToysMachineChanges};
        if(enabled)
        {
            var executable=Path.Combine(ProtectedDirectory,"PathMan.Service.exe");
            if(!File.Exists(executable) || !FileHash(executable).SequenceEqual(FileHash(layout.BackgroundExecutable)))
            {
                Stop();OriginTrackingTask.SecureDirectory(ProtectedDirectory);
                foreach(var file in Directory.EnumerateFiles(layout.InternalDirectory))
                {
                    if(!Path.GetFileName(file).Equals("PathMan.Service.exe",StringComparison.OrdinalIgnoreCase) && Path.GetExtension(file) is not (".dll" or ".json")) continue;
                    if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("Invalid service payload.");
                    for(var retry=0;;retry++)
                    {
                        try {File.Copy(file,Path.Combine(ProtectedDirectory,Path.GetFileName(file)),true);break;}
                        catch(IOException failure) when(retry<50 && (failure.HResult&0xffff) is 32 or 33) {Thread.Sleep(100);}
                    }
                }
            }
            var command=$"\"{executable}\" --system-monitor";
            var exists=Sc("query",ServiceName)==0;
            Checked(exists ? "config" : "create",ServiceName,"binPath=",command,"start=","auto","obj=","LocalSystem","DisplayName=","PathMan Monitor");
            // Restart automatically after a crash; an ordinary user cannot edit the service.
            Checked("failure",ServiceName,"reset=","86400","actions=","restart/5000/restart/15000/restart/60000");
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
        var result=Sc("stop",ServiceName);
        if(result is not (0 or 1060 or 1062)) throw new Win32Exception(result);
        for(var retry=0;retry<150;retry++)
        {
            var status=ScOutput("query",ServiceName);
            if(status.Code==1060 || status.Output.Contains("STOPPED",StringComparison.Ordinal)) return;
            Thread.Sleep(100);
        }
        throw new IOException("Global monitor did not stop.");
    }
    private static byte[] FileHash(string path) {using var stream=File.OpenRead(path);return System.Security.Cryptography.SHA256.HashData(stream);}
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
