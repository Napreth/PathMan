using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;

namespace Pathman.Core;

// Keep every directory and the source image open until deployment completes.
// The ACL check rejects broad write grants even if another ACE denies them.
public sealed class PrivilegedServiceSource : IDisposable
{
    private readonly List<SafeFileHandle> directories=[];
    private FileStream? image;
    private string sourcePath="";
    private static readonly HashSet<string> trusted=["S-1-5-18","S-1-5-32-544",TrustedInstaller()];
    private static string TrustedInstaller()=>((SecurityIdentifier)new NTAccount(@"NT SERVICE\TrustedInstaller").Translate(typeof(SecurityIdentifier))).Value;
    private PrivilegedServiceSource() { }
    public static PrivilegedServiceSource Open(PathmanLayout layout)
    {
        var result=new PrivilegedServiceSource();
        try
        {
            var root=Path.GetPathRoot(layout.InstallationDirectory)!;
            if(root.StartsWith(@"\\",StringComparison.Ordinal) || new DriveInfo(root).DriveType!=DriveType.Fixed) throw new IOException("A local protected service source is required.");
            var paths=new Stack<string>();
            for(var directory=new DirectoryInfo(layout.InternalDirectory);directory is not null;directory=directory.Parent) paths.Push(directory.FullName);
            foreach(var directory in paths)
            {
                var handle=CreateFile(directory,0x20080,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
                if(handle.IsInvalid) {handle.Dispose();throw new Win32Exception();}
                result.directories.Add(handle);
                Validate(handle,directory.Equals(Path.TrimEndingDirectorySeparator(layout.InstallationDirectory),StringComparison.OrdinalIgnoreCase) || directory.Equals(layout.InternalDirectory,StringComparison.OrdinalIgnoreCase));
            }
            var file=CreateFile(layout.BackgroundExecutable,0x80020080,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            if(file.IsInvalid) {file.Dispose();throw new Win32Exception();}
            try {Validate(file,true);result.image=new FileStream(file,FileAccess.Read);result.sourcePath=layout.BackgroundExecutable;}
            catch {file.Dispose();throw;}
            using var manifestHandle=CreateFile(Path.Combine(layout.InternalDirectory,"service-payload.json"),0x80020080,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            if(manifestHandle.IsInvalid) throw new Win32Exception();
            Validate(manifestHandle,true);
            using var manifestStream=new FileStream(manifestHandle,FileAccess.Read);
            using var manifest=JsonDocument.Parse(manifestStream);
            var expected=manifest.RootElement.GetProperty("sha256").GetString();
            if(!Convert.ToHexString(SHA256.HashData(result.image)).Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new IOException("Service image does not match its protected payload manifest.");
            result.image.Position=0;
            return result;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or Win32Exception or JsonException or KeyNotFoundException or InvalidOperationException)
        {result.Dispose();throw new IOException(L.Get("全局服务只能从管理员保护的本地安装目录部署，请重新安装到安全目录。"),error);}
        catch {result.Dispose();throw;}
    }
    internal static bool IsProtected(RawSecurityDescriptor security,bool payload)
    {
        if(security.Owner is null || !trusted.Contains(security.Owner.Value) || security.DiscretionaryAcl is null) return false;
        // Ancestors may let users create unrelated subdirectories (e.g. C:\).
        // They must not let them change this directory or replace existing children.
        var dangerous=0x500d0152u | (payload ? 0x4u : 0u);
        foreach(GenericAce ace in security.DiscretionaryAcl)
        {
            if((ace.AceFlags&AceFlags.InheritOnly)!=0) continue;
            if(ace is not QualifiedAce rule) return false;
            if(rule.AceQualifier==AceQualifier.AccessAllowed && !trusted.Contains(rule.SecurityIdentifier.Value) && ((uint)rule.AccessMask&dangerous)!=0) return false;
        }
        return true;
    }
    private static void Validate(SafeFileHandle handle,bool payload)
    {
        if(!GetFileInformationByHandleEx(handle,9,out var attributes,8)) throw new Win32Exception();
        if((attributes.Attributes&0x400)!=0) throw new IOException("Service source cannot contain reparse points.");
        var error=GetSecurityInfo(handle,1,5,out _,out _,out _,out _,out var descriptor);
        if(error!=0) throw new Win32Exception((int)error);
        try
        {
            var bytes=new byte[GetSecurityDescriptorLength(descriptor)];Marshal.Copy(descriptor,bytes,0,bytes.Length);
            if(!IsProtected(new RawSecurityDescriptor(bytes,0),payload)) throw new IOException("Service source must be owned and writable only by SYSTEM, Administrators or TrustedInstaller.");
        }
        finally {LocalFree(descriptor);}
    }
    public void Deploy(string directory)
    {
        ValidateDestination(directory);
        // A clean destination prevents stale sidecar DLLs from entering the service.
        OriginTrackingTask.SecureDirectory(directory);
        if(Directory.Exists(directory)) OriginTrackingTask.DeleteStoppedDirectory(directory);
        OriginTrackingTask.SecureDirectory(directory);
        var destination=Path.Combine(directory,"PathMan.Service.exe");
        image!.Position=0;var expected=SHA256.HashData(image);image.Position=0;
        using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
        {
            image.CopyTo(output);output.Flush(true);output.Position=0;
            if(!SHA256.HashData(output).SequenceEqual(expected)) throw new IOException("Service payload verification failed.");
        }
    }
    public void ValidateDestination(string directory)
    {
        var target=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))+Path.DirectorySeparatorChar;
        if(sourcePath.StartsWith(target,StringComparison.OrdinalIgnoreCase)) throw new IOException("The service snapshot directory must not contain its installation source.");
    }
    public void Dispose() {image?.Dispose();foreach(var directory in directories) directory.Dispose();}
    [StructLayout(LayoutKind.Sequential)] private struct FileAttributesInfo {public uint Attributes,Tag;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")] private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out FileAttributesInfo attributes,uint length);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle,int kind,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
