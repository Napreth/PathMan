using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace Pathman.Core;

// Only the read-only collector runs as SYSTEM. The monitor retains the desktop
// user's token and HKCU. No task parameters select executable paths or commands.
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
                return ((string)definition.Principal.UserId is "SYSTEM" or "S-1-5-18") && definition.Actions.Count==1 &&
                    string.Equals((string)definition.Actions.Item(1).Path,Executable,StringComparison.OrdinalIgnoreCase) &&
                    definition.Actions.Item(1).Arguments==ArgumentsFor(CurrentSid) && File.Exists(Executable);
            }
            catch(COMException error) {throw new IOException(L.Get("无法访问来源识别计划任务。"),error);}
        }
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
        dynamic folder=Connect();dynamic? old=Find(folder,sid);old?.Stop(0);
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
            Element("Principals",new XElement(ns+"Principal",new XAttribute("id","Collector"),Element("UserId","S-1-5-18"),Element("RunLevel","HighestAvailable"))),
            new XElement(ns+"Settings",Element("MultipleInstancesPolicy","Parallel"),Element("DisallowStartIfOnBatteries","false"),Element("StopIfGoingOnBatteries","false"),
                Element("AllowStartOnDemand","true"),Element("Enabled","true"),Element("ExecutionTimeLimit","PT0S")),
            new XElement(ns+"Actions",new XAttribute("Context","Collector"),new XElement(ns+"Exec",Element("Command",Path.Combine(target,"PathMan.Service.exe")),Element("Arguments",ArgumentsFor(sid)),Element("WorkingDirectory",target)))));
        // The desktop user may read/run the fixed task, but cannot edit it.
        folder.RegisterTask(Prefix+sid,xml.ToString(),6,"SYSTEM",null,5,$"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{sid})");
    }
    private static void SecureDirectory(string path)
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
            if(((string)task.Name).StartsWith(Prefix,StringComparison.Ordinal)) {task.Stop(0);folder.DeleteTask(task.Name,0);}
        if(Directory.Exists(directory))
        {
            if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0) throw new IOException("Invalid collector cleanup directory.");
            Directory.Delete(directory,true);
        }
    }
    private static void DemandAdministrator()
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new Win32Exception(740);
    }
}
