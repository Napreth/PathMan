using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;

namespace Pathman.Service;

// One use, read-only collector handoff from an already authorized installer.
// The ordinary background process keeps the desktop user's token and HKCU,
// including when setup used credentials belonging to a different administrator.
internal static class InstallerOriginBroker
{
    private static string Name(string sid)
    {
        using var process=Process.GetCurrentProcess();
        var install=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.ProcessPath!.ToUpperInvariant())))[..16];
        return $"PathMan.InstallerOrigins.{sid}.{process.SessionId}.{install}";
    }
    public static Process? TryStart(string[] arguments)
    {
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            using var pipe=new NamedPipeClientStream(".",Name(identity.User!.Value),PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
            pipe.Connect(750);
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var server)) return null;
            using(var process=Process.GetProcessById((int)server))
                if(!RegistryOriginTracker.Image(process).Equals(Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) return null;
            using var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true) {AutoFlush=true};
            writer.WriteLine(JsonSerializer.Serialize(arguments));
            using var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response=reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            return int.TryParse(response,NumberStyles.None,CultureInfo.InvariantCulture,out var id) && id>0 ? Process.GetProcessById(id) : null;
        }
        catch(Exception error) when(error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) {return null;}
    }
    public static int Run(string[] args)
    {
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            if(args.Length!=4 || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || !args[3].StartsWith(@"Local\PathMan.Setup.Origins.",StringComparison.Ordinal)) return 2;
            var sid=new SecurityIdentifier(args[1]);
            // No config writes or monitor startup under the administrator's HKCU.
            var config=Path.Combine(Path.GetFullPath(args[2]),"environment.json");
            if(!File.Exists(config)) return 0;
            var settings=JsonSerializer.Deserialize<EnvironmentWorkspace>(File.ReadAllText(config),EnvironmentWorkspaceStore.JsonOptions);
            if(settings is not {MonitoringEnabled:true,TrackChangeOrigins:true}) return 0;
            var task=new OriginTrackingTask(PathmanLayout.Discover());if(!task.HasUserTask(sid.Value)) task.Register(sid.Value);
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new(sid,PipeAccessRights.ReadWrite|PipeAccessRights.Synchronize,AccessControlType.Allow));
            security.AddAccessRule(new(identity.User!,PipeAccessRights.FullControl,AccessControlType.Allow));
            using var pipe=NamedPipeServerStreamAcl.Create(Name(sid.Value),PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.FirstPipeInstance,4096,4096,security,HandleInheritability.None,(PipeAccessRights)0);
            using(var ready=EventWaitHandle.OpenExisting(args[3])) ready.Set();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var client)) return 2;
            string? clientSid=null;pipe.RunAsClient(()=> {using var user=WindowsIdentity.GetCurrent(true);clientSid=user?.User?.Value;});
            if(clientSid!=sid.Value) return 2;
            using var parent=Process.GetProcessById((int)client);
            using var self=Process.GetCurrentProcess();
            if(parent.SessionId!=self.SessionId || !RegistryOriginTracker.Image(parent).Equals(Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) return 2;
            // Bound input before JSON parsing; no client-selected executable or
            // arbitrary command is ever accepted by the elevated process.
            var bytes=new List<byte>();
            while(bytes.Count<4096)
            {
                var one=new byte[1];var count=pipe.ReadAsync(one,timeout.Token).AsTask().GetAwaiter().GetResult();
                if(count==0) return 2;if(one[0]==10) break;bytes.Add(one[0]);
            }
            if(bytes.Count>=4096) return 2;
            var command=JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(bytes.ToArray()));
            if(command is not ["--registry-origin-worker",var pid,var ticks,var name,var secret,var requestedSid] ||
                pid!=parent.Id.ToString(CultureInfo.InvariantCulture) || ticks!=parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ||
                requestedSid!=sid.Value || !name.StartsWith("PathMan.RegistryOrigins.",StringComparison.Ordinal) || name.Length>100 || Convert.FromBase64String(secret).Length!=32) return 2;
            var start=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true};
            foreach(var value in command) start.ArgumentList.Add(value);
            using var collector=Process.Start(start);
            using var output=new StreamWriter(pipe,new UTF8Encoding(false),4096,true) {AutoFlush=true};
            output.WriteLine((collector?.Id ?? 0).ToString(CultureInfo.InvariantCulture));
            return collector is null ? 1 : 0;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or FormatException or System.Runtime.InteropServices.COMException) {return 1;}
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint id);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint id);
}
