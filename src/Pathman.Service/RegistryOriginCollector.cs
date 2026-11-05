using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
namespace Pathman.Service;
internal static class RegistryOriginCollector
{
    public static void Run(string? sid,Action<OriginMessage> send,CancellationToken stop)
    {
            // Fixed identity permits reclaiming only our own orphaned session.
            var install=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.ProcessPath!.ToUpperInvariant())))[..16];
            using var session=new TraceEventSession("PathMan.EnvironmentOrigins."+(sid ?? "Global")+"."+install);
            session.BufferSizeMB=8;
            using var select=Registry.LocalMachine.OpenSubKey(@"SYSTEM\Select");
            var control=(int?)select?.GetValue("Current") ?? 1;
            var machine=$@"\REGISTRY\MACHINE\SYSTEM\ControlSet{control:000}\Control\Session Manager\Environment";
            var user=sid is null ? @"\REGISTRY\USER" : $@"\REGISTRY\USER\{sid}\Environment";
            var registryNames=new RegistryObjectNames(user,machine);
            var processes=new Dictionary<int,(string Image,string Command)>();
            (string Image,string Command) Identify(int id)
            {
                if(processes.TryGetValue(id,out var found) && (!Path.GetFileName(found.Image).Equals("rundll32.exe",StringComparison.OrdinalIgnoreCase) || found.Command.Length>0)) return found;
                try
                {
                    using var process=Process.GetProcessById(id);
                    var image=RegistryOriginTracker.Image(process);var command="";
                    if(Path.GetFileName(image).Equals("rundll32.exe",StringComparison.OrdinalIgnoreCase))
                    {using var item=new ManagementObject($"Win32_Process.Handle='{id}'");command=item["CommandLine"] as string ?? "";}
                    return processes[id]=(image,command);
                }
                catch(Exception error) when(error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or ManagementException) {return ("","");}
            }
            var registryProvider=new Guid("70eb4f03-c1de-4f73-a051-33d13d5413bd");
            void Changed(TraceEvent data)
            {
                if(data.ProviderGuid!=registryProvider || data.PayloadByName("Status")?.ToString()!="0") return;
                var id=(int)data.ID;
                ulong Pointer(string name)=>Convert.ToUInt64(data.PayloadByName(name),CultureInfo.InvariantCulture);
                if(id is 1 or 2)
                {
                    registryNames.Open(data.ProcessID,Pointer("KeyObject"),Pointer("BaseObject"),data.PayloadByName("BaseName") as string ?? "",data.PayloadByName("RelativeName") as string ?? "");
                    return;
                }
                if(id is not (5 or 6)) return;
                var key=registryNames.Lookup(Pointer("KeyObject"),data.PayloadByName("KeyName") as string ?? "");
                var userSid=sid;
                if(sid is null && key.StartsWith(@"\REGISTRY\USER\",StringComparison.OrdinalIgnoreCase) && key.EndsWith(@"\Environment",StringComparison.OrdinalIgnoreCase))
                    userSid=key.Split('\\').ElementAtOrDefault(3);
                var scope=(sid is not null && key.Equals(user,StringComparison.OrdinalIgnoreCase) || sid is null && userSid is not null && key.Equals($@"\REGISTRY\USER\{userSid}\Environment",StringComparison.OrdinalIgnoreCase)) ? VariableScope.User : key.Equals(machine,StringComparison.OrdinalIgnoreCase) ? VariableScope.Machine : (VariableScope?)null;
                var name=data.PayloadByName("ValueName") as string ?? "";
                if(scope is null || name.Length==0) return;
                var process=Identify(data.ProcessID);
                try {send(new("write",Origin:new(scope.Value,name,data.ProcessID,process.Image,data.TimeStamp.ToUniversalTime(),process.Command),UserSid:scope==VariableScope.User ? userSid : null));}
                catch(IOException) {session.Source.StopProcessing();}
            }
            void Started(ProcessTraceData data)
            {
                var image=data.ImageFileName;var command=data.CommandLine;
                try {using var process=Process.GetProcessById(data.ProcessID);image=RegistryOriginTracker.Image(process);}
                catch(Exception error) when(error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Preserve the complete executable token for short-lived
                    // writers that have already exited when ETW is delivered.
                    if(!string.IsNullOrWhiteSpace(command))
                    {var text=command.TrimStart();image=text.StartsWith('"') ? text[1..].Split('"',2)[0] : text.Split(' ',2)[0];}
                }
                processes[data.ProcessID]=(image,command);
                if(processes.Count>8192) foreach(var id in processes.Keys.Take(1024).ToArray()) processes.Remove(id);
            }
            // Accessing Source starts an ordinary session, so the kernel provider
            // must be enabled before obtaining the source or attaching handlers.
            TraceEventSession.SetDebugPrivilege();
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            session.Source.Kernel.ProcessStart+=Started;session.Source.Kernel.ProcessDCStart+=Started;
            session.Source.Dynamic.All+=Changed;
            session.EnableProvider(registryProvider,TraceEventLevel.Verbose,0x3300,new TraceEventProviderOptions {EventIDsToEnable=new int[] {1,2,5,6}});
            using var timer=new Timer(_=> {try {session.Flush();} catch(ObjectDisposedException) {}},null,100,100);
            using var registration=stop.Register(()=>session.Source.StopProcessing());
            send(new("ready"));session.Source.Process();
    }
}
