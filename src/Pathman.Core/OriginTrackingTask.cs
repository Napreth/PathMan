using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace Pathman.Core;

// An administrator's own collector uses their elevated interactive token.
// Cross-user authorization uses SYSTEM for the fixed, read-only collector.
public sealed class OriginTrackingTask(PathmanLayout layout)
{
    private string InstallationId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout.BackgroundExecutable.ToUpperInvariant())))[..24];
    private string Prefix => "PathMan.Origins." + InstallationId + ".";
    private static string CurrentSid {get {using var user=WindowsIdentity.GetCurrent();return user.User!.Value;}}
    private string DirectoryFor(string sid) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PathMan Origin Collectors",InstallationId,sid);
    public string Executable => Path.Combine(DirectoryFor(CurrentSid),"PathMan.Service.exe");
    private string ArgumentsFor(string sid) => $"--scheduled-origin-worker \"{layout.BackgroundExecutable}\" {sid} \"$(Arg0)\" \"$(Arg1)\" \"$(Arg2)\" \"$(Arg3)\"";
    private static dynamic Connect()
    {
        dynamic scheduler=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",throwOnError:true)!)!;
        scheduler.Connect();return scheduler.GetFolder(@"\");
    }
    private dynamic? Find(dynamic folder,string sid)
    {
        try {return folder.GetTask(Prefix+sid);}
        catch(FileNotFoundException) {return null;}
        catch(DirectoryNotFoundException) {return null;}
        catch(COMException error) when(error.HResult==unchecked((int)0x80070002)) {return null;}
    }
    public bool IsRegistered
    {
        get
        {
            try
            {
                dynamic? task=Find(Connect(),CurrentSid);
                if(task is null || !task.Enabled) return false;
                dynamic definition=task!.Definition;
                return ValidPrincipal((string)definition.Principal.UserId,CurrentSid) && definition.Principal.RunLevel==1 && definition.Actions.Count==1 &&
                    string.Equals((string)definition.Actions.Item(1).Path,Executable,StringComparison.OrdinalIgnoreCase) &&
                    definition.Actions.Item(1).Arguments==ArgumentsFor(CurrentSid) && File.Exists(Executable);
            }
            catch(COMException error) {throw new IOException(L.Get("无法访问来源识别计划任务。"),error);}
        }
    }
    private static bool ValidPrincipal(string principal,string sid)
    {
        if(principal is "SYSTEM" or "S-1-5-18" || principal==sid) return true;
        try {return ((SecurityIdentifier)new NTAccount(principal).Translate(typeof(SecurityIdentifier))).Value==sid;}
        catch(IdentityNotMappedException) {return false;}
    }
    public void EnsureRegistered()
    {
        if(IsRegistered && FileHash(Executable).SequenceEqual(FileHash(layout.BackgroundExecutable))) return;
        using var user=WindowsIdentity.GetCurrent();
        var start=new ProcessStartInfo(layout.BackgroundExecutable) {UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--register-origin-task");start.ArgumentList.Add(user.User!.Value);
        using var process=Process.Start(start) ?? throw new IOException(L.Get("无法注册来源识别计划任务。"));
        if(!process.WaitForExit(60000) || process.ExitCode!=0 || !IsRegistered) throw new IOException(L.Get("无法注册来源识别计划任务。"));
    }
    private static byte[] FileHash(string path) {using var input=File.OpenRead(path);return SHA256.HashData(input);}
    public void Start(string[] arguments)
    {
        if(arguments.Length!=4 || !IsRegistered) throw new IOException(L.Get("请重新开启“精确识别修改来源”，完成一次管理员授权。"));
        try {dynamic task=Find(Connect(),CurrentSid)!;task.Run(arguments);}
        catch(COMException error) {throw new IOException(L.Get("无法启动来源识别计划任务。"),error);}
    }
    public void Register(string sid)
    {
        sid=new SecurityIdentifier(sid).Value;
        DemandAdministrator();
        using var identity=WindowsIdentity.GetCurrent();var own=identity.User!.Value==sid;
        dynamic folder=Connect();dynamic? old=Find(folder,sid);if(old is not null) StopTask(old);
        var target=DirectoryFor(sid);
        // Program Files is a trusted anchor even for a per-user installation.
        // Do not follow junctions or reuse a directory owned by an ordinary user.
        var root=Path.GetDirectoryName(Path.GetDirectoryName(target)!)!;
        SecureDirectory(root);SecureDirectory(Path.GetDirectoryName(target)!);SecureDirectory(target);
        var source=Path.GetDirectoryName(layout.BackgroundExecutable)!;
        foreach(var file in Directory.EnumerateFiles(source))
        {
            var name=Path.GetFileName(file);var extension=Path.GetExtension(file);
            if(!name.Equals("PathMan.Service.exe",StringComparison.OrdinalIgnoreCase) && extension is not (".dll" or ".json")) continue;
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("Collector files cannot be reparse points.");
            File.Copy(file,Path.Combine(target,name),overwrite:true);
        }
        XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Element(string name,object value)=>new(ns+name,value);
        var xml=new XDocument(new XElement(ns+"Task",new XAttribute("version","1.2"),
            Element("Principals",new XElement(ns+"Principal",new XAttribute("id","Collector"),Element("UserId",own ? sid : "S-1-5-18"),own ? Element("LogonType","InteractiveToken") : null,Element("RunLevel","HighestAvailable"))),
            new XElement(ns+"Settings",Element("MultipleInstancesPolicy","Parallel"),Element("DisallowStartIfOnBatteries","false"),Element("StopIfGoingOnBatteries","false"),
                Element("AllowStartOnDemand","true"),Element("Enabled","true"),Element("ExecutionTimeLimit","PT0S")),
            new XElement(ns+"Actions",new XAttribute("Context","Collector"),new XElement(ns+"Exec",Element("Command",Path.Combine(target,"PathMan.Service.exe")),Element("Arguments",ArgumentsFor(sid)),Element("WorkingDirectory",target)))));
        // The desktop user may read/run the fixed task, but cannot edit it.
        folder.RegisterTask(Prefix+sid,xml.ToString(),6,own ? sid : "SYSTEM",null,own ? 3 : 5,$"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{sid})");
    }
    internal static void SecureDirectory(string path)
    {
        var directory=Directory.CreateDirectory(path);
        if((directory.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Collector directories cannot be reparse points.");
        var security=directory.GetAccessControl();var owner=security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        using var identity=WindowsIdentity.GetCurrent();
        if(owner!=system && owner!=admins && owner!=identity.User) throw new UnauthorizedAccessException("Untrusted collector directory owner.");
        security=new DirectorySecurity();security.SetOwner(admins);security.SetAccessRuleProtection(true,false);
        foreach(var principal in new[] {system,admins}) security.AddAccessRule(new(principal,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid,null),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
    public bool HasInstallationTasks()
    {
        foreach(dynamic task in Connect().GetTasks(1)) if(((string)task.Name).StartsWith(Prefix,StringComparison.Ordinal)) return true;
        return false;
    }
    public bool HasUserTask(string sid) => Find(Connect(),new SecurityIdentifier(sid).Value) is not null;
    public void RemoveInstallation()
    {
        var directory=Path.GetDirectoryName(DirectoryFor(CurrentSid))!;
        if(!HasInstallationTasks() && !Directory.Exists(directory)) return;
        DemandAdministrator();dynamic folder=Connect();
        foreach(dynamic task in folder.GetTasks(1))
            if(((string)task.Name).StartsWith(Prefix,StringComparison.Ordinal)) {StopTask(task);folder.DeleteTask(task.Name,0);}
        if(Directory.Exists(directory))
        {
            if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0) throw new IOException("Invalid collector cleanup directory.");
            DeleteStoppedDirectory(directory);
        }
    }
    private static void StopTask(dynamic task)
    {
        task.Stop(0);
        for(var retry=0;retry<100;retry++) {if(task.GetInstances(0).Count==0) return;Thread.Sleep(100);}
        throw new IOException("Collector task did not stop.");
    }
    internal static void DeleteStoppedDirectory(string directory)
    {
        for(var retry=0;;retry++)
        {
            try {Directory.Delete(directory,true);return;}
            catch(IOException failure) when(retry<100 && (failure.HResult&0xffff) is 5 or 32 or 33) {Thread.Sleep(100);}
            catch(UnauthorizedAccessException) when(retry<100) {Thread.Sleep(100);}
        }
    }
    private static void DemandAdministrator()
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new Win32Exception(740);
    }
}
