using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Pathman.Core.Shims;

public sealed record ShimAlias(string Name, string Target);

public sealed class ShimManager(PathmanLayout layout,bool ensureUserPath=true)
{
    private const string Header = "[PathMan.Shim]";
    private const string Format = "Format=2";

    public IReadOnlyList<ShimAlias> List()
    {
        if (!Directory.Exists(layout.ShimsDirectory)) return [];
        return Directory.EnumerateFiles(layout.ShimsDirectory).Where(file=>Path.GetExtension(file).Equals(".shim",StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".exe",StringComparison.OrdinalIgnoreCase))
            .Select(file=>Path.GetFileNameWithoutExtension(file)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name=>ReadAlias(Path.Combine(layout.ShimsDirectory,name+".shim")))
            .OrderBy(alias => alias.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ShimAlias Add(string name, string target) => WithLock(() => AddAlias(name, target));

    private ShimAlias AddAlias(string name, string target)
    {
        name = ValidateName(name);
        ArgumentNullException.ThrowIfNull(target);
        Directory.CreateDirectory(layout.ShimsDirectory);
        var executable = Path.Combine(layout.ShimsDirectory, name + ".exe");
        var manifest = Path.Combine(layout.ShimsDirectory, name + ".shim");
        if (Path.Exists(executable) || Path.Exists(manifest)) throw new IOException(L.Format("别名或同名文件已存在：{0}",name));
        CreateLink(executable);
        try {WriteManifest(manifest,target,false);} catch {File.Delete(executable);throw;}
        if(ensureUserPath) EnsureUserShimsPath();
        return new(name,target);
    }
    private static void WriteManifest(string manifest,string target,bool overwrite)
    {
        var temporary=manifest+"."+Guid.NewGuid().ToString("N")+".tmp";
        // Store the literal text, including empty/invalid paths, without INI trimming or line injection.
        try {File.WriteAllText(temporary,$"{Header}\r\n{Format}\r\nTargetBase64={Convert.ToBase64String(Encoding.Unicode.GetBytes(target))}\r\n",Encoding.Unicode);File.Move(temporary,manifest,overwrite);}
        finally {File.Delete(temporary);}
    }
    private ShimAlias Existing(string name,string? expectedTarget)
    {
        if(!File.Exists(Path.Combine(layout.ShimsDirectory,name+".shim")) && !File.Exists(Path.Combine(layout.ShimsDirectory,name+".exe"))) throw new IOException(L.Get("别名已不存在，请刷新后重试。"));
        var alias=ReadAlias(Path.Combine(layout.ShimsDirectory,name+".shim"));
        if(expectedTarget is not null && alias.Target!=expectedTarget) throw new IOException(L.Get("别名已被其他程序修改，请刷新后重试。"));
        return alias;
    }
    public ShimAlias Update(string name,string target,string? expectedTarget=null)=>WithLock(()=>
    {
        name=ValidateName(name);var existing=Existing(name,expectedTarget);ArgumentNullException.ThrowIfNull(target);
        WriteManifest(Path.Combine(layout.ShimsDirectory,existing.Name+".shim"),target,true);
        return new ShimAlias(existing.Name,target);
    });
    public ShimAlias Rename(string name,string newName,string? expectedTarget=null)=>WithLock(()=>
    {
        name=ValidateName(name);newName=ValidateName(newName);var existing=Existing(name,expectedTarget);
        if(existing.Name==newName) return existing;
        var oldExe=Path.Combine(layout.ShimsDirectory,existing.Name+".exe");var oldManifest=Path.Combine(layout.ShimsDirectory,existing.Name+".shim");
        var newExe=Path.Combine(layout.ShimsDirectory,newName+".exe");var newManifest=Path.Combine(layout.ShimsDirectory,newName+".shim");
        if(!existing.Name.Equals(newName,StringComparison.OrdinalIgnoreCase) && (Path.Exists(newExe) || Path.Exists(newManifest))) throw new IOException(L.Get("别名或同名文件已存在。"));
        var temporary=Path.Combine(layout.ShimsDirectory,Guid.NewGuid().ToString("N"));var exeAt=oldExe;var manifestAt=oldManifest;
        try
        {
            if(File.Exists(exeAt)) {File.Move(exeAt,temporary+".exe.tmp");exeAt=temporary+".exe.tmp";}
            if(File.Exists(manifestAt)) {File.Move(manifestAt,temporary+".shim.tmp");manifestAt=temporary+".shim.tmp";}
            if(exeAt!=oldExe) {File.Move(exeAt,newExe);exeAt=newExe;}
            if(manifestAt!=oldManifest) {File.Move(manifestAt,newManifest);manifestAt=newManifest;}
        }
        catch
        {
            if(manifestAt!=oldManifest) File.Move(manifestAt,oldManifest);
            if(exeAt!=oldExe) File.Move(exeAt,oldExe);
            throw;
        }
        return new ShimAlias(newName,existing.Target);
    });
    public void Remove(string name,string? expectedTarget=null) => WithLock(() =>
    {
        name = ValidateName(name);
        var manifest = Path.Combine(layout.ShimsDirectory, name + ".shim");
        _ = Existing(name,expectedTarget);
        File.Delete(Path.Combine(layout.ShimsDirectory, name + ".exe"));
        File.Delete(manifest);
        return true;
    });

    public int Refresh() => WithLock(() =>
    {
        var aliases = List();
        foreach (var alias in aliases)
        {
            var executable = Path.Combine(layout.ShimsDirectory, alias.Name + ".exe");
            var temporary = Path.Combine(layout.ShimsDirectory, Guid.NewGuid() + ".tmp");
            CreateLink(temporary);
            try { ShimFiles.Replace(temporary, executable); }
            finally { File.Delete(temporary); }
        }
        if (aliases.Count > 0 && ensureUserPath) EnsureUserShimsPath();
        return aliases.Count;
    });

    private T WithLock<T>(Func<T> action)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout.DataDirectory.ToUpperInvariant())));
        using var mutex = new Mutex(false, @"Global\PathMan.Shims." + key);
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try { return action(); } finally { mutex.ReleaseMutex(); }
    }

    private void EnsureUserShimsPath()
    {
        // A machine-wide install adds only bin to the machine PATH. Each user
        // adds their own shims folder when creating aliases. Isolated data roots
        // used by callers/tests must never change the real user environment.
        var defaultData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PathMan");
        if (!Path.GetFullPath(layout.DataDirectory).Equals(defaultData, StringComparison.OrdinalIgnoreCase)) return;
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        if (current.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(entry =>
            Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')).TrimEnd('\\')
                .Equals(layout.ShimsDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
        Environment.SetEnvironmentVariable("PATH", current.TrimEnd(';') + ";" + layout.ShimsDirectory,
            EnvironmentVariableTarget.User);
    }

    private void CreateLink(string executable)
    {
        if (CreateHardLink(executable, layout.ShimExecutable, IntPtr.Zero)) return;
        var error = Marshal.GetLastWin32Error();
        if (error == 17) // ERROR_NOT_SAME_DEVICE: copy one template onto the user's volume.
        {
            var cached = layout.CachedShimExecutable;
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            using var source = File.OpenRead(layout.ShimExecutable);
            var current = false;
            if (File.Exists(cached))
            {
                using var existing = File.OpenRead(cached);
                current = SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(existing));
            }
            if (!current)
            {
                // Move the mapped cache aside before publishing its replacement.
                var temporary = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(layout.ShimExecutable, temporary); ShimFiles.Replace(temporary, cached); }
                finally { File.Delete(temporary); }
            }
            if (CreateHardLink(executable, cached, IntPtr.Zero)) return;
            error = Marshal.GetLastWin32Error();
        }
        throw new Win32Exception(error, L.Format("无法创建硬链接：{0}",executable));
    }

    private static ShimAlias ReadAlias(string manifest)
    {
        var target="";
        try
        {
            var lines=File.ReadAllLines(manifest,Encoding.Unicode);
            if(lines.Length>=3 && lines[0]==Header)
            {
                if(lines[1]=="Format=1" && lines[2].StartsWith("Target=")) target=lines[2][7..];
                else if(lines[1]==Format && lines[2].StartsWith("TargetBase64=")) target=Encoding.Unicode.GetString(Convert.FromBase64String(lines[2][13..]));
            }
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or FormatException) { }
        return new(Path.GetFileNameWithoutExtension(manifest),target);
    }

    public static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var stem = name.Split('.')[0].TrimEnd(' ','.');
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$" };
        if (string.IsNullOrWhiteSpace(name) || name.Length>250 || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            reserved.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException(L.Get("别名必须是有效的 Windows 文件名，最长 250 个字符，不能使用设备保留名或以空格、句点结尾。"));
        return name;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
