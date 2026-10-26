using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pathman.Core.EnvironmentVariables;

namespace Pathman.Core.EnvironmentVariables;

internal sealed record EnvironmentSessionRequest(EnvironmentMutation[] Mutations,VariableSnapshot[] Preconditions);
internal sealed record EnvironmentSessionResult(string? Error);
public sealed class EnvironmentOperationException(string message) : IOException(message);
public sealed class ElevatedEnvironmentSession : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Process worker;
    private const int MaximumFrame=1024*1024;
    private ElevatedEnvironmentSession(NamedPipeServerStream pipe,Process worker) {this.pipe=pipe;this.worker=worker;}
    public bool IsAlive=>pipe.IsConnected && !worker.HasExited;
    public static async Task<ElevatedEnvironmentSession> StartAsync()
    {
        var pipeName="PathMan.Environment."+Guid.NewGuid().ToString("N");
        var secret=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var identity=WindowsIdentity.GetCurrent();
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new(identity.User!,PipeAccessRights.FullControl,AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),PipeAccessRights.ReadWrite|PipeAccessRights.Synchronize,AccessControlType.Allow));
        var server=NamedPipeServerStreamAcl.Create(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.FirstPipeInstance,4096,4096,security,HandleInheritability.None,(PipeAccessRights)0);
        Process? worker=null;
        try
        {
            using var parent=Process.GetCurrentProcess();
            var start=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
            foreach(var argument in new[]{"--environment-session",parent.Id.ToString(CultureInfo.InvariantCulture),parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),pipeName,secret}) start.ArgumentList.Add(argument);
            worker=await Task.Run(()=>Process.Start(start)) ?? throw new IOException("无法启动管理员操作。");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while(true)
            {
                await server.WaitForConnectionAsync(timeout.Token);
                if(GetNamedPipeClientProcessId(server.SafePipeHandle,out var processId) && processId==worker.Id) break;
                server.Disconnect();
            }
            var hello=await ReadAsync<string>(server,timeout.Token);
            if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello),Encoding.UTF8.GetBytes(secret))) throw new IOException("管理员连接认证失败。");
            await WriteAsync(server,new EnvironmentSessionResult(null),timeout.Token);
            return new(server,worker);
        }
        catch {server.Dispose();worker?.Dispose();throw;}
    }
    public async Task ApplyAsync(IReadOnlyList<EnvironmentMutation> mutations,IReadOnlyList<VariableSnapshot> preconditions)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await WriteAsync(pipe,new EnvironmentSessionRequest(mutations.ToArray(),preconditions.ToArray()),timeout.Token);
            var result=await ReadAsync<EnvironmentSessionResult>(pipe,timeout.Token);
            if(result.Error is not null) throw new EnvironmentOperationException(result.Error);
        }
        catch(OperationCanceledException error) {throw new IOException("管理员连接超时，请刷新确认写入结果后重试。",error);}
        catch(EndOfStreamException error) {throw new IOException("管理员连接已断开，请刷新确认写入结果后重试。",error);}
    }
    public void Dispose() {pipe.Dispose();worker.Dispose();}

    public static async Task<int> RunWorkerAsync(string[] arguments)
    {
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            if(arguments.Length!=5 || !int.TryParse(arguments[1],out var parentId) || !long.TryParse(arguments[2],out var startTicks) || !arguments[3].StartsWith("PathMan.Environment.",StringComparison.Ordinal) || arguments[3].Length>100 || arguments[4].Length!=44 || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
            using var parent=Process.GetProcessById(parentId);
            if(parent.StartTime.ToUniversalTime().Ticks!=startTicks || !ImagePath(parent).Equals(Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) return 1;
            using var client=new NamedPipeClientStream(".",arguments[3],PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
            using var connectTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(connectTimeout.Token);
            if(!GetNamedPipeServerProcessId(client.SafePipeHandle,out var serverId) || serverId!=parentId) return 1;
            await WriteAsync(client,arguments[4],connectTimeout.Token);
            if((await ReadAsync<EnvironmentSessionResult>(client,connectTimeout.Token)).Error is not null) return 1;
            var parentExit=parent.WaitForExitAsync();
            while(!parent.HasExited)
            {
                using var idle=new CancellationTokenSource(TimeSpan.FromMinutes(15));
                var reading=ReadAsync<EnvironmentSessionRequest>(client,idle.Token);
                if(await Task.WhenAny(reading,parentExit)!=reading)
                {idle.Cancel();try {await reading;}catch(OperationCanceledException){}return 0;}
                EnvironmentSessionRequest request;
                try {request=await reading;}catch(OperationCanceledException){return 0;}
                string? error=null;
                try {WindowsEnvironmentVariableEditor.ApplyMachineRegistry(request.Mutations,request.Preconditions);}
                catch(Exception failure) when(failure is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {error=failure.Message;}
                await WriteAsync(client,new EnvironmentSessionResult(error),idle.Token);
            }
            return 0;
        }
        catch(EndOfStreamException) {return 0;}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
        {return 1;}
    }
    private static async Task WriteAsync<T>(Stream stream,T value,CancellationToken token)
    {
        var payload=JsonSerializer.SerializeToUtf8Bytes(value,EnvironmentWorkspaceStore.JsonOptions);
        if(payload.Length>MaximumFrame) throw new IOException("操作请求过大。");
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,payload.Length);
        await stream.WriteAsync(header,token);await stream.WriteAsync(payload,token);await stream.FlushAsync(token);
    }
    private static async Task<T> ReadAsync<T>(Stream stream,CancellationToken token)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,token);var size=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(size is <=0 or >MaximumFrame) throw new IOException("管理员请求格式无效。");
        var payload=new byte[size];await stream.ReadExactlyAsync(payload,token);
        return JsonSerializer.Deserialize<T>(payload,EnvironmentWorkspaceStore.JsonOptions) ?? throw new IOException("管理员请求为空。");
    }
    private static string ImagePath(Process process)
    {var buffer=new StringBuilder(32768);var size=buffer.Capacity;return QueryFullProcessImageName(process.Handle,0,buffer,ref size) ? buffer.ToString() : "";}
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle,out uint processId);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle,out uint processId);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder name,ref int size);
}
