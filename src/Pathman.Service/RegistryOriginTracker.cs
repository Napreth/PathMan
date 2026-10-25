using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Service;

internal sealed record OriginMessage(string Kind,string? Message=null,EnvironmentChangeOrigin? Origin=null);

// A read-only elevated collector. The pipe authenticates both process identities;
// its protocol has no registry-write, file-write or executable-launch requests.
internal sealed class RegistryOriginTracker : IEnvironmentChangeOrigins,IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Process worker;
    private readonly ConcurrentQueue<EnvironmentChangeOrigin> captured=new();
    private readonly CancellationTokenSource stop=new();
    private readonly Task reader;
    private volatile bool ready;
    public bool IsReady=>ready && !worker.HasExited;
    public string? Error {get;private set;}
    private RegistryOriginTracker(NamedPipeServerStream pipe,Process worker)
    {
        this.pipe=pipe;this.worker=worker;
        reader=Task.Run(async()=>
        {
            try
            {
                using var input=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
                while(!stop.IsCancellationRequested && await input.ReadLineAsync(stop.Token) is {} line)
                {
                    if(line.Length>262144) throw new IOException("来源跟踪消息过长。");
                    var message=JsonSerializer.Deserialize<OriginMessage>(line,EnvironmentWorkspaceStore.JsonOptions);
                    if(message?.Kind=="ready") ready=true;
                    else if(message?.Kind=="error") {Error=message.Message;ready=false;}
                    else if(message?.Origin is {} origin) {captured.Enqueue(origin);while(captured.Count>4096) captured.TryDequeue(out _);}
                }
            }
            catch(Exception error) when(error is IOException or OperationCanceledException or JsonException or ObjectDisposedException) {Error=error.Message;}
            finally {ready=false;if(!stop.IsCancellationRequested) Error ??= "修改来源跟踪连接已断开，请重新启用来源识别。";}
        });
    }
    public static RegistryOriginTracker Start()
    {
        var name="PathMan.RegistryOrigins."+Guid.NewGuid().ToString("N");
        var secret=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var identity=WindowsIdentity.GetCurrent();
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new(identity.User!,PipeAccessRights.FullControl,AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),PipeAccessRights.ReadWrite|PipeAccessRights.Synchronize,AccessControlType.Allow));
        var pipe=NamedPipeServerStreamAcl.Create(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.FirstPipeInstance,4096,4096,security,HandleInheritability.None,(PipeAccessRights)0);
        Process? child=null;
        try
        {
            using var parent=Process.GetCurrentProcess();
            var start=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
            foreach(var value in new[] {"--registry-origin-worker",parent.Id.ToString(CultureInfo.InvariantCulture),parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),name,secret,identity.User!.Value}) start.ArgumentList.Add(value);
            child=Process.Start(start) ?? throw new IOException("无法启动修改来源跟踪。");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while(true)
            {
                pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
                if(GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var id) && id==child.Id) break;
                pipe.Disconnect();
            }
            // Read one byte at a time for the handshake so buffering cannot steal
            // the collector's first ready/event message from the long-lived reader.
            var hello=new List<byte>();
            while(hello.Count<128) {var value=pipe.ReadByte();if(value<0) throw new EndOfStreamException();if(value==10) break;hello.Add((byte)value);}
            if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret),hello.ToArray())) throw new IOException("来源跟踪连接认证失败。");
            pipe.WriteByte(1);pipe.Flush();
            return new(pipe,child);
        }
        catch {pipe.Dispose();child?.Dispose();throw;}
    }
    public IReadOnlyList<EnvironmentChangeOrigin> Take(VariableScope scope,string name)=>throw new NotSupportedException("Use a fixed batch.");
    public IEnvironmentChangeOrigins Batch()
    {
        var values=new List<EnvironmentChangeOrigin>();
        while(captured.TryDequeue(out var item)) values.Add(item);
        return new BatchOrigins(IsReady,values);
    }
    private sealed class BatchOrigins(bool ready,IReadOnlyList<EnvironmentChangeOrigin> values) : IEnvironmentChangeOrigins
    {
        public bool IsReady=>ready;
        public IReadOnlyList<EnvironmentChangeOrigin> Take(VariableScope scope,string name)=>values.Where(item=>item.Scope==scope && item.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public void Dispose() {stop.Cancel();pipe.Dispose();worker.Dispose();_ = reader.ContinueWith(_=>stop.Dispose(),TaskScheduler.Default);}

    public static int RunWorker(string[] args)
    {
        try
        {
            if(args.Length!=6 || !int.TryParse(args[1],out var parentId) || !long.TryParse(args[2],out var ticks) || !args[3].StartsWith("PathMan.RegistryOrigins.",StringComparison.Ordinal) || args[3].Length>100 || args[4].Length!=44 || !IsAdministrator()) return 2;
            using var parent=Process.GetProcessById(parentId);
            if(parent.StartTime.ToUniversalTime().Ticks!=ticks || !Image(parent).Equals(Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) return 2;
            var sid=new SecurityIdentifier(args[5]).Value;
            using var pipe=new NamedPipeClientStream(".",args[3],PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
            pipe.Connect(30000);
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var server) || server!=parentId) return 2;
            using var output=new StreamWriter(pipe,new UTF8Encoding(false),4096,true) {AutoFlush=true,NewLine="\n"};
            output.WriteLine(args[4]);
            if(pipe.ReadByte()!=1) return 2;
            void Send(OriginMessage message)=>output.WriteLine(JsonSerializer.Serialize(message,EnvironmentWorkspaceStore.JsonOptions).Replace("\r","").Replace("\n",""));
            try
            {
            // Fixed identity permits reclaiming only our own orphaned session.
            var install=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.ProcessPath!.ToUpperInvariant())))[..16];
            using var session=new TraceEventSession("PathMan.EnvironmentOrigins."+sid+"."+install);
            session.BufferSizeMB=8;
            using var select=Registry.LocalMachine.OpenSubKey(@"SYSTEM\Select");
            var control=(int?)select?.GetValue("Current") ?? 1;
            var machine=$@"\REGISTRY\MACHINE\SYSTEM\ControlSet{control:000}\Control\Session Manager\Environment";
            var user=$@"\REGISTRY\USER\{sid}\Environment";
            var registryNames=new RegistryObjectNames(user,machine);
            var processes=new Dictionary<int,(string Image,string Command)>();
            (string Image,string Command) Identify(int id)
            {
                if(processes.TryGetValue(id,out var found) && (!Path.GetFileName(found.Image).Equals("rundll32.exe",StringComparison.OrdinalIgnoreCase) || found.Command.Length>0)) return found;
                try
                {
                    using var process=Process.GetProcessById(id);
                    var image=Image(process);var command="";
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
                var scope=key.Equals(user,StringComparison.OrdinalIgnoreCase) ? VariableScope.User : key.Equals(machine,StringComparison.OrdinalIgnoreCase) ? VariableScope.Machine : (VariableScope?)null;
                var name=data.PayloadByName("ValueName") as string ?? "";
                if(scope is null || name.Length==0) return;
                var process=Identify(data.ProcessID);
                try {Send(new("write",Origin:new(scope.Value,name,data.ProcessID,process.Image,data.TimeStamp.ToUniversalTime(),process.Command)));}
                catch(IOException) {session.Source.StopProcessing();}
            }
            void Started(ProcessTraceData data)
            {
                var image=data.ImageFileName;var command=data.CommandLine;
                try {using var process=Process.GetProcessById(data.ProcessID);image=Image(process);}
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
            _=Task.Run(()=> {try {pipe.ReadByte();} catch(IOException) {} finally {session.Source.StopProcessing();}});
            Send(new("ready"));session.Source.Process();return 0;
            }
            // TraceEvent also reports controller failures as plain Exception.
            // Report collector startup/runtime failures over its read-only pipe.
            catch(Exception error)
            {try {Send(new("error",error.Message));} catch(IOException) {} return 1;}
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException) {return 1;}
    }
    private static bool IsAdministrator() {using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
    private static string Image(Process process)
    {var path=new StringBuilder(32768);var size=path.Capacity;if(!QueryFullProcessImageName(process.Handle,0,path,ref size)) throw new System.ComponentModel.Win32Exception();return path.ToString();}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint id);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder path,ref int size);
}
