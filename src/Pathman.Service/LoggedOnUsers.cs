using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pathman.Core;

namespace Pathman.Service;

internal sealed class LoggedOnUsers : IDisposable
{
    public SafeAccessTokenHandle Token {get;}
    public string Sid {get;}
    public uint SessionId {get;}
    public string DataDirectory {get;}
    public bool Administrator {get;}
    public IReadOnlyDictionary<string,string> Runtime {get;}
    private LoggedOnUsers(SafeAccessTokenHandle token,uint sessionId)
    {
        SessionId=sessionId;
        Token=token;using var user=new WindowsIdentity(token.DangerousGetHandle());Sid=user.User!.Value;
        Administrator=new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator);
        if(GetTokenInformation(token,19,out var linked,IntPtr.Size,out _) && linked!=IntPtr.Zero)
        {using var handle=new SafeAccessTokenHandle(linked);using var identity=new WindowsIdentity(linked);Administrator|=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
        var folder=new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref folder,0,token.DangerousGetHandle(),out var path));
        try {DataDirectory=Path.Combine(Marshal.PtrToStringUni(path)!,"PathMan");}finally {Marshal.FreeCoTaskMem(path);}
        var runtime=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(CreateEnvironmentBlock(out var block,token,false))
        {
            try
            {
                var cursor=block;
                while(Marshal.PtrToStringUni(cursor) is {Length:>0} entry)
                {var equal=entry.IndexOf('=');if(equal>0) runtime[entry[..equal]]=entry[(equal+1)..];cursor=IntPtr.Add(cursor,(entry.Length+1)*2);}
            }
            finally {DestroyEnvironmentBlock(block);}
        }
        Runtime=runtime;
    }
    public static IReadOnlyList<LoggedOnUsers> Enumerate()
    {
        var users=new List<LoggedOnUsers>();
        if(!WTSEnumerateSessions(IntPtr.Zero,0,1,out var entries,out var count)) return users;
        try
        {
            for(var index=0;index<count;index++)
            {
                var entry=Marshal.PtrToStructure<Session>(IntPtr.Add(entries,index*Marshal.SizeOf<Session>()));
                if(entry.State is not (0 or 4) || !WTSQueryUserToken(entry.Id,out var token)) continue;
                try {users.Add(new LoggedOnUsers(token,entry.Id));}
                catch {token.Dispose();}
            }
        }
        finally {WTSFreeMemory(entries);}
        return users;
    }
    public void ResumeBackground(string installation)
    {
        if(installation.Length==0) return;
        var layout=new PathmanLayout(installation,DataDirectory);
        var run=WindowsIdentity.RunImpersonated(Token,()=>StartupManager.CanRun(new Pathman.Core.EnvironmentVariables.EnvironmentWorkspaceStore(DataDirectory).Read(),new StartupManager(layout).IsEnabled));
        if(!run || !File.Exists(layout.BackgroundExecutable)) return;
        var command=new StringBuilder($"\"{layout.BackgroundExecutable}\" --data-root \"{DataDirectory}\"");
        Start(layout.BackgroundExecutable,command,installation);
    }
    public void NotifyEnvironmentChanged()
    {
        // Session 0 broadcasts cannot reach Explorer in a user's desktop.
        // This one-shot, unelevated notification exits immediately; the unified
        // service remains the only resident monitor and ETW collector.
        var executable=Environment.ProcessPath!;
        Start(executable,new StringBuilder($"\"{executable}\" --environment-changed"),Path.GetDirectoryName(executable)!);
    }
    private void Start(string executable,StringBuilder command,string directory)
    {
        var startup=new Startup {Size=Marshal.SizeOf<Startup>(),Desktop=@"winsta0\default"};
        if(!CreateEnvironmentBlock(out var environment,Token,false)) throw new System.ComponentModel.Win32Exception();
        try {if(CreateProcessAsUser(Token,executable,command,IntPtr.Zero,IntPtr.Zero,false,0x08000400,environment,directory,ref startup,out var child)) {CloseHandle(child.Process);CloseHandle(child.Thread);}else throw new System.ComponentModel.Win32Exception();}
        finally {DestroyEnvironmentBlock(environment);}
    }
    public void Dispose()=>Token.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct Session {public uint Id;public IntPtr Station;public int State;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Startup {public int Size;public string? Reserved,Desktop,Title;public int X,Y,Width,Height,CharsX,CharsY,Fill,Flags;public short Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)] private struct Child {public IntPtr Process,Thread;public uint Id,ThreadId;}
    [DllImport("wtsapi32.dll")] private static extern bool WTSEnumerateSessions(IntPtr server,int reserved,int version,out IntPtr sessions,out int count);
    [DllImport("wtsapi32.dll")] private static extern bool WTSQueryUserToken(uint session,out SafeAccessTokenHandle token);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int type,out IntPtr info,int length,out int required);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,IntPtr token,out IntPtr path);
    [DllImport("userenv.dll",SetLastError=true)] private static extern bool CreateEnvironmentBlock(out IntPtr block,SafeAccessTokenHandle token,bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr block);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token,string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out Child process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
