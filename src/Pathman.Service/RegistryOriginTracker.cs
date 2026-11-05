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
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Service;

internal sealed record OriginMessage(string Kind,string? Message=null,EnvironmentChangeOrigin? Origin=null,string? UserSid=null);

// A read-only elevated collector. The pipe authenticates both process identities;
// its protocol has no registry-write, file-write or executable-launch requests.
internal sealed class RegistryOriginTracker : IEnvironmentChangeOrigins,IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Process? worker;
    private readonly ConcurrentQueue<EnvironmentChangeOrigin> captured=new();
    private readonly CancellationTokenSource stop=new();
    private readonly Task reader;
    private volatile bool ready;
    public bool IsReady=>ready && (worker is null || !worker.HasExited);
    public string? Error {get;private set;}
    private RegistryOriginTracker(NamedPipeServerStream pipe,Process? worker)
    {
        this.pipe=pipe;this.worker=worker;
        reader=Task.Run(async()=>
        {
            try
            {
                using var input=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
                while(!stop.IsCancellationRequested && await input.ReadLineAsync(stop.Token) is {} line)
                {
                    if(line.Length>262144) throw new IOException(L.Get("来源跟踪消息过长。"));
                    var message=JsonSerializer.Deserialize<OriginMessage>(line,EnvironmentWorkspaceStore.JsonOptions);
                    if(message?.Kind=="ready") ready=true;
                    else if(message?.Kind=="error") {Error=message.Message;ready=false;}
                    else if(message?.Origin is {} origin) {captured.Enqueue(origin);while(captured.Count>4096) captured.TryDequeue(out _);}
                }
            }
            catch(Exception error) when(error is IOException or OperationCanceledException or JsonException or ObjectDisposedException) {Error=error.Message;}
            finally {ready=false;if(!stop.IsCancellationRequested) Error ??= L.Get("修改来源跟踪连接已断开，请重新启用来源识别。");}
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
            var arguments=new[] {"--registry-origin-worker",parent.Id.ToString(CultureInfo.InvariantCulture),parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),name,secret,identity.User!.Value};
            child=!IsAdministrator() ? InstallerOriginBroker.TryStart(arguments) : null;
            var scheduled=child is null && !IsAdministrator();
            var task=new OriginTrackingTask(PathmanLayout.Discover());
            if(scheduled) task.Start(arguments[1..5]);
            else if(child is null)
            {
                var start=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true};
                foreach(var value in arguments) start.ArgumentList.Add(value);
                child=Process.Start(start) ?? throw new IOException(L.Get("无法启动修改来源跟踪。"));
            }
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while(true)
            {
                pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
                if(GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var id))
                {
                    if(child is not null && id==child.Id) break;
                    if(scheduled)
                    {
                        string? sid=null;var elevated=false;
                        pipe.RunAsClient(()=> {using var user=WindowsIdentity.GetCurrent(true);sid=user?.User?.Value;elevated=user is not null && WindowsUserContext.IsElevatedAdministrator(user);});
                        // The protected task fixes the image. Authenticate the
                        // peer's pipe token and per-launch secret without asking
                        // a limited process to open a privileged process token.
                        if(sid=="S-1-5-18") break;
                        if(sid==identity.User!.Value && elevated) break;
                    }
                }
                pipe.Disconnect();
            }
            // Read one byte at a time for the handshake so buffering cannot steal
            // the collector's first ready/event message from the long-lived reader.
            var hello=new List<byte>();
            while(hello.Count<128) {var value=pipe.ReadByte();if(value<0) throw new EndOfStreamException();if(value==10) break;hello.Add((byte)value);}
            if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret),hello.ToArray())) throw new IOException(L.Get("来源跟踪连接认证失败。"));
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
    public void Dispose() {stop.Cancel();pipe.Dispose();worker?.Dispose();_ = reader.ContinueWith(_=>stop.Dispose(),TaskScheduler.Default);}

    public static int RunScheduledWorker(string[] args)
    {
        if(args is not ["--scheduled-origin-worker",var source,var sid,var pid,var ticks,var name,var secret]) return 2;
        return RunWorker(["--registry-origin-worker",pid,ticks,name,secret,sid],source);
    }
    public static int RunWorker(string[] args,string? expectedParent=null)
    {
        try
        {
            if(args.Length!=6 || !int.TryParse(args[1],out var parentId) || !long.TryParse(args[2],out var ticks) || !args[3].StartsWith("PathMan.RegistryOrigins.",StringComparison.Ordinal) || args[3].Length>100 || args[4].Length!=44 || !IsAdministrator()) return 2;
            using var parent=Process.GetProcessById(parentId);
            if(parent.StartTime.ToUniversalTime().Ticks!=ticks || !Image(parent).Equals(expectedParent ?? Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) return 2;
            var sid=new SecurityIdentifier(args[5]).Value;
            using(var handle=OpenProcess(0x1000,false,parent.Id))
            {
                if(!OpenProcessToken(handle,8,out var token)) return 2;
                using(token) using(var user=new WindowsIdentity(token.DangerousGetHandle())) if(user.User?.Value!=sid) return 2;
            }
            using var pipe=new NamedPipeClientStream(".",args[3],PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
            pipe.Connect(30000);
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var server) || server!=parentId) return 2;
            using var output=new StreamWriter(pipe,new UTF8Encoding(false),4096,true) {AutoFlush=true,NewLine="\n"};
            output.WriteLine(args[4]);
            if(pipe.ReadByte()!=1) return 2;
            void Send(OriginMessage message)=>output.WriteLine(JsonSerializer.Serialize(message,EnvironmentWorkspaceStore.JsonOptions).Replace("\r","").Replace("\n",""));
            try
            {
            using var stop=new CancellationTokenSource();
            _=Task.Run(()=> {try {pipe.ReadByte();} catch(IOException) {} finally {stop.Cancel();}});
            RegistryOriginCollector.Run(sid,Send,stop.Token);return 0;
            }
            // TraceEvent also reports controller failures as plain Exception.
            // Report collector startup/runtime failures over its read-only pipe.
            catch(Exception error)
            {try {Send(new("error",error.Message));} catch(IOException) {} return 1;}
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException) {return 1;}
    }
    private static bool IsAdministrator()=>WindowsUserContext.IsElevatedAdministrator();
    internal static string Image(Process process)
    {
        using var handle=OpenProcess(0x1000,false,process.Id);
        var path=new StringBuilder(32768);var size=path.Capacity;
        if(handle.IsInvalid || !QueryFullProcessImageName(handle,0,path,ref size)) throw new System.ComponentModel.Win32Exception();return path.ToString();
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint id);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint id);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process,int flags,StringBuilder path,ref int size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(int access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int processId);
}
