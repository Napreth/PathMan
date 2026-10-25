using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pathman.Service;

// Modern registry ETW records may omit key names. Follow observed key opens;
// for roots opened before tracing began, resolve the actual source handle.
internal sealed class RegistryObjectNames(string userKey,string machineKey)
{
    private readonly Dictionary<ulong,string> names=new();
    public string Open(int processId,ulong keyObject,ulong baseObject,string baseName,string relative)
    {
        var name=relative.StartsWith(@"\REGISTRY\",StringComparison.OrdinalIgnoreCase) ? relative : "";
        if(name.Length==0)
        {
            if(baseName.Length==0) names.TryGetValue(baseObject,out baseName!);
            if(string.IsNullOrEmpty(baseName) && Candidate(relative)) baseName=Resolve(processId,baseObject) ?? "";
            if(!string.IsNullOrEmpty(baseName)) name=baseName.TrimEnd('\\')+"\\"+relative;
        }
        if(Relevant(name))
        {names[keyObject]=name;if(names.Count>4096) {names.Clear();names[keyObject]=name;}}
        else names.Remove(keyObject);
        return name;
    }
    public string Lookup(ulong keyObject,string supplied)
    {
        if(supplied.Length>0) return supplied;
        return names.TryGetValue(keyObject,out var name) ? name : "";
    }
    private bool Relevant(string name)=>name.Length>0 && (userKey.Equals(name,StringComparison.OrdinalIgnoreCase) ||
        userKey.StartsWith(name+"\\",StringComparison.OrdinalIgnoreCase) || machineKey.Equals(name,StringComparison.OrdinalIgnoreCase) ||
        machineKey.StartsWith(name+"\\",StringComparison.OrdinalIgnoreCase));
    private bool Candidate(string relative)=>relative.EndsWith("Environment",StringComparison.OrdinalIgnoreCase) ||
        relative.Equals(userKey.Split('\\')[3],StringComparison.OrdinalIgnoreCase);
    private string? Resolve(int processId,ulong keyObject)
    {
        if(keyObject==0 || processId<=0) return null;
        using var process=OpenProcess(0x40,false,processId); // PROCESS_DUP_HANDLE only
        if(process.IsInvalid) return null;
        var size=1024*1024;
        while(size<=64*1024*1024)
        {
            var buffer=Marshal.AllocHGlobal(size);
            try
            {
                var status=NtQuerySystemInformation(64,buffer,size,out var required);
                if(status==unchecked((int)0xC0000004)) {size=Math.Max(size*2,required+65536);continue;}
                if(status!=0) return null;
                var count=(ulong)(nuint)Marshal.ReadIntPtr(buffer);
                var entrySize=3*IntPtr.Size+16;
                var header=2*IntPtr.Size;
                if(count>(ulong)((size-header)/entrySize)) return null;
                for(ulong index=0;index<count;index++)
                {
                    var entry=IntPtr.Add(buffer,checked(header+(int)index*entrySize));
                    if((ulong)(nuint)Marshal.ReadIntPtr(entry)!=keyObject || (long)Marshal.ReadIntPtr(entry,IntPtr.Size)!=processId) continue;
                    if(!DuplicateHandle(process,Marshal.ReadIntPtr(entry,2*IntPtr.Size),GetCurrentProcess(),out var key,0,false,2)) continue;
                    using(key)
                    {
                        var nameBuffer=Marshal.AllocHGlobal(65536);
                        try
                        {
                            if(NtQueryKey(key,3,nameBuffer,65536,out _)!=0) continue; // KeyNameInformation
                            var length=Marshal.ReadInt32(nameBuffer);
                            if(length<0 || length>65532 || length%2!=0) return null;
                            var name=Marshal.PtrToStringUni(IntPtr.Add(nameBuffer,4),length/2);
                            if(name is not null && Relevant(name)) names[keyObject]=name;
                            return name;
                        }
                        finally {Marshal.FreeHGlobal(nameBuffer);}
                    }
                }
                return null;
            }
            finally {Marshal.FreeHGlobal(buffer);}
        }
        return null;
    }
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass,IntPtr buffer,int length,out int required);
    [DllImport("ntdll.dll")] private static extern int NtQueryKey(SafeRegistryHandle key,int informationClass,IntPtr buffer,int length,out int required);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DuplicateHandle(SafeProcessHandle source,IntPtr handle,IntPtr target,out SafeRegistryHandle duplicate,uint access,bool inherit,uint options);
}
