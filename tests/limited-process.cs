using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

// Launch the login-monitor tests with the same SID but a filtered, medium token.
// S4U batch logons can retain administrator groups even with RunLevel=Limited.
internal static class LimitedProcess
{
    [StructLayout(LayoutKind.Sequential)] private struct SidAttributes {public IntPtr Sid;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct StartupInfo
    {public int Size;public string Reserved,Desktop,Title;public int X,Y,Width,Height,CharsX,CharsY,Fill,Flags;public short Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo {public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes {public int Size;public IntPtr Descriptor;public int Inherit;}
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle,out int code);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateWindowStation(string name,uint flags,uint access,ref SecurityAttributes security);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,ref SecurityAttributes security);
    [DllImport("user32.dll")] private static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text,uint revision,out IntPtr descriptor,IntPtr size);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor,out bool present,out IntPtr dacl,out bool defaulted);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool CreateRestrictedToken(IntPtr token,uint flags,uint disableCount,ref SidAttributes disabled,uint deleteCount,IntPtr deleted,uint restrictCount,IntPtr restricted,out IntPtr result);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool SetTokenInformation(IntPtr token,int type,ref SidAttributes information,int length);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcessAsUser(IntPtr token,string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo process);
    private static IntPtr Sid(string value)
    {
        var sid=new SecurityIdentifier(value);var bytes=new byte[sid.BinaryLength];sid.GetBinaryForm(bytes,0);
        var memory=Marshal.AllocHGlobal(bytes.Length);Marshal.Copy(bytes,0,memory,bytes.Length);return memory;
    }
    private static string Quote(string value) {return "\""+System.Text.RegularExpressions.Regex.Replace(value,@"(\\*)(""|$)",m=>m.Groups[1].Value+m.Groups[1].Value+(m.Groups[2].Value.Length==0 ? "" : "\\\""))+"\"";}
    public static int Main(string[] args)
    {
        IntPtr original=IntPtr.Zero,limited=IntPtr.Zero,admins=IntPtr.Zero,medium=IntPtr.Zero;
        IntPtr station=IntPtr.Zero,desktop=IntPtr.Zero,descriptor=IntPtr.Zero;
        try
        {
            if(args.Length==0)return 2;
            if(!OpenProcessToken(GetCurrentProcess(),0x8B,out original))throw new Win32Exception(Marshal.GetLastWin32Error(),"Open process token");
            admins=Sid("S-1-5-32-544");var disabled=new SidAttributes {Sid=admins};
            if(!CreateRestrictedToken(original,1,1,ref disabled,0,IntPtr.Zero,0,IntPtr.Zero,out limited))throw new Win32Exception(Marshal.GetLastWin32Error(),"Filter administrator token");
            medium=Sid("S-1-16-8192");var label=new SidAttributes {Sid=medium,Attributes=0x20};
            if(!SetTokenInformation(limited,25,ref label,Marshal.SizeOf(typeof(SidAttributes))))throw new Win32Exception(Marshal.GetLastWin32Error(),"Set medium integrity");
            // PowerShell Direct's private desktop only permits elevated tokens.
            // Give this test its own medium-integrity desktop, away from the UI.
            var stationName="PathMan.Test."+Guid.NewGuid().ToString("N");
            var sid=WindowsIdentity.GetCurrent().User.Value;
            if(!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;"+sid+")(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;ME)",1,out descriptor,IntPtr.Zero))throw new Win32Exception();
            bool present,defaulted;IntPtr dacl;
            if(!GetSecurityDescriptorDacl(descriptor,out present,out dacl,out defaulted))throw new Win32Exception();
            var defaults=new SidAttributes {Sid=dacl};
            if(!SetTokenInformation(limited,6,ref defaults,IntPtr.Size))throw new Win32Exception(Marshal.GetLastWin32Error(),"Set token default permissions");
            var security=new SecurityAttributes {Size=Marshal.SizeOf(typeof(SecurityAttributes)),Descriptor=descriptor};
            station=CreateWindowStation(stationName,0,0xF037F,ref security);if(station==IntPtr.Zero)throw new Win32Exception();
            var previous=GetProcessWindowStation();
            if(!SetProcessWindowStation(station))throw new Win32Exception();
            try {desktop=CreateDesktop("Default",IntPtr.Zero,IntPtr.Zero,0,0xF01FF,ref security);if(desktop==IntPtr.Zero)throw new Win32Exception();}
            finally {SetProcessWindowStation(previous);}
            var command=new StringBuilder();foreach(var argument in args)command.Append(Quote(argument)).Append(' ');
            var startup=new StartupInfo {Size=Marshal.SizeOf(typeof(StartupInfo)),Desktop=stationName+"\\Default",Flags=0x100,Input=GetStdHandle(-10),Output=GetStdHandle(-11),Error=GetStdHandle(-12)};ProcessInfo child;
            if(!CreateProcessAsUser(limited,args[0],command,IntPtr.Zero,IntPtr.Zero,true,0x08000000,IntPtr.Zero,null,ref startup,out child))throw new Win32Exception(Marshal.GetLastWin32Error(),"Launch limited process");
            try {if(WaitForSingleObject(child.Process,120000)!=0)throw new TimeoutException("Limited test process timed out.");int code;if(!GetExitCodeProcess(child.Process,out code))throw new Win32Exception();return code;}
            finally {CloseHandle(child.Thread);CloseHandle(child.Process);}
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {if(desktop!=IntPtr.Zero)CloseDesktop(desktop);if(station!=IntPtr.Zero)CloseWindowStation(station);if(descriptor!=IntPtr.Zero)LocalFree(descriptor);if(limited!=IntPtr.Zero)CloseHandle(limited);if(original!=IntPtr.Zero)CloseHandle(original);if(admins!=IntPtr.Zero)Marshal.FreeHGlobal(admins);if(medium!=IntPtr.Zero)Marshal.FreeHGlobal(medium);}
    }
}
