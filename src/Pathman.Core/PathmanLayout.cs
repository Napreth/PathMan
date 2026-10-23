using Microsoft.Win32;

namespace Pathman.Core;

public sealed class PathmanLayout
{
    public string InstallationDirectory { get; }
    public string BinDirectory => Path.Combine(InstallationDirectory, "bin");
    public string InternalDirectory => Path.Combine(InstallationDirectory, "internal");
    public string DataDirectory { get; }
    public string ShimsDirectory => Path.Combine(DataDirectory, "shims");
    public string ShimExecutable => Path.Combine(InternalDirectory, "PathMan.Shim.exe");
    public string CachedShimExecutable => Path.Combine(DataDirectory, "internal", "PathMan.Shim.exe");
    public string BackgroundExecutable => Path.Combine(InternalDirectory, "PathMan.Service.exe");

    public PathmanLayout(string installationDirectory, string? dataDirectory = null)
    {
        InstallationDirectory = Path.GetFullPath(installationDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PathMan"));
    }

    public static PathmanLayout Discover(string? dataDirectory = null)
    {
        var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var name = Path.GetFileName(directory);
        var installation = (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("internal", StringComparison.OrdinalIgnoreCase))
            ? Directory.GetParent(directory)!.FullName
            : directory;
        if (!File.Exists(Path.Combine(installation, "internal", "PathMan.Shim.exe")))
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan");
                if (uninstall?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location) &&
                    File.Exists(Path.Combine(location, "internal", "PathMan.Shim.exe")))
                    return new PathmanLayout(location, dataDirectory);
            }
        }
        return new PathmanLayout(installation, dataDirectory);
    }
}
