using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Pathman.Core.Shims;

public sealed record ShimAlias(string Name, string Target);

public sealed class ShimManager(PathmanLayout layout)
{
    private const string Header = "[PathMan.Shim]";
    private const string Format = "Format=1";

    public IReadOnlyList<ShimAlias> List()
    {
        if (!Directory.Exists(layout.ShimsDirectory)) return [];
        return Directory.EnumerateFiles(layout.ShimsDirectory, "*.shim")
            .Select(ReadAlias)
            .OrderBy(alias => alias.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ShimAlias Add(string name, string target) => WithLock(() => AddAlias(name, target));

    private ShimAlias AddAlias(string name, string target)
    {
        name = ValidateName(name);
        target = Path.GetFullPath(target);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("别名目标必须是 .exe 文件。");
        if (!File.Exists(target)) throw new FileNotFoundException("别名目标不存在。", target);
        if (!File.Exists(layout.ShimExecutable))
            throw new FileNotFoundException("找不到垫片本体，请先构建发布目录。", layout.ShimExecutable);
        if (target.StartsWith(Path.TrimEndingDirectorySeparator(layout.ShimsDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            target.Equals(layout.ShimExecutable, StringComparison.OrdinalIgnoreCase) ||
            target.Equals(layout.CachedShimExecutable, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("别名目标不能是 PathMan 垫片，避免循环启动。");

        Directory.CreateDirectory(layout.ShimsDirectory);
        var executable = Path.Combine(layout.ShimsDirectory, name + ".exe");
        var manifest = Path.Combine(layout.ShimsDirectory, name + ".shim");
        if (Path.Exists(executable) || Path.Exists(manifest))
            throw new IOException($"别名或同名文件已存在：{name}");

        // Create the executable before its manifest, so failures do not publish a broken alias.
        CreateLink(executable);

        try
        {
            using var stream = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Encoding.Unicode);
            writer.Write($"{Header}\r\n{Format}\r\nTarget={target}\r\n");
        }
        catch
        {
            File.Delete(executable);
            throw;
        }
        EnsureUserShimsPath();
        return new ShimAlias(name, target);
    }

    public void Remove(string name) => WithLock(() =>
    {
        name = ValidateName(name);
        var manifest = Path.Combine(layout.ShimsDirectory, name + ".shim");
        _ = ReadAlias(manifest);
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
            try { File.Move(temporary, executable, overwrite: true); }
            finally { File.Delete(temporary); }
        }
        if (aliases.Count > 0) EnsureUserShimsPath();
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
                // Replace the cache atomically; running aliases may still use the old file.
                var temporary = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(layout.ShimExecutable, temporary); File.Move(temporary, cached, overwrite: true); }
                finally { File.Delete(temporary); }
            }
            if (CreateHardLink(executable, cached, IntPtr.Zero)) return;
            error = Marshal.GetLastWin32Error();
        }
        throw new Win32Exception(error, $"无法创建硬链接：{executable}");
    }

    private static ShimAlias ReadAlias(string manifest)
    {
        var lines = File.ReadAllLines(manifest, Encoding.Unicode);
        if (lines.Length != 3 || lines[0] != Header || lines[1] != Format || !lines[2].StartsWith("Target="))
            throw new InvalidDataException($"无法识别垫片配置：{manifest}");
        return new ShimAlias(Path.GetFileNameWithoutExtension(manifest), lines[2][7..]);
    }

    private static string ValidateName(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var stem = name.Split('.')[0];
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$" };
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            reserved.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException("别名必须是有效的 Windows 文件名，不能使用设备保留名。");
        return name;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
