using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Pathman.Core;

public static class WindowsUserContext
{
    public static RegistryKey OpenRegistry(bool writable=false)
    {
        var error=RegOpenCurrentUser(writable ? 0x2001f : 0x20019,out var key);
        if(error!=0) throw new Win32Exception(error);
        return RegistryKey.FromHandle(key);
    }
    public static RegistryKey? EnvironmentKey(bool writable)
    {using var user=OpenRegistry(writable);return writable ? user.CreateSubKey("Environment",true) : user.OpenSubKey("Environment");}
    public static bool IsElevatedAdministrator()
    {
        using var identity=WindowsIdentity.GetCurrent();
        return IsElevatedAdministrator(identity);
    }
    public static bool IsElevatedAdministrator(WindowsIdentity identity)
    {
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return false;
        var data=Marshal.AllocHGlobal(256);
        try
        {
            if(!GetTokenInformation(identity.Token,25,data,256,out _)) return false;
            var sid=new SecurityIdentifier(Marshal.ReadIntPtr(data));
            return int.Parse(sid.Value.Split('-')[^1],System.Globalization.CultureInfo.InvariantCulture)>=12288;
        }
        finally {Marshal.FreeHGlobal(data);}
    }
    public static bool IsAdministratorAccount()
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        var data=Marshal.AllocHGlobal(4);
        try {return GetTokenInformation(identity.Token,18,data,4,out _) && Marshal.ReadInt32(data) is 2 or 3;}
        finally {Marshal.FreeHGlobal(data);}
    }
    internal static Mutex WorkspaceMutex(string name)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{identity.User!.Value})S:(ML;;NW;;;ME)",1,out var descriptor,out _)) throw new Win32Exception();
        try
        {
            var security=new SecurityAttributes {Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor};
            var handle=CreateMutexEx(ref security,name,0,0x100001); // SYNCHRONIZE | MUTEX_MODIFY_STATE
            if(handle.IsInvalid) throw new Win32Exception();
            return new Mutex {SafeWaitHandle=handle};
        }
        finally {LocalFree(descriptor);}
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes {public int Length;public IntPtr Descriptor;public int Inherit;}
    [DllImport("advapi32.dll")] private static extern int RegOpenCurrentUser(int access,out SafeRegistryHandle key);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr info,int length,out int required);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text,uint revision,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateMutexExW")] private static extern SafeWaitHandle CreateMutexEx(ref SecurityAttributes attributes,string name,uint flags,uint access);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
