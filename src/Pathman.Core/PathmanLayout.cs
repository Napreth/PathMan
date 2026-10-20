namespace Pathman.Core;

public sealed class PathmanLayout
{
    public string InstallationDirectory { get; }
    public string BinDirectory => Path.Combine(InstallationDirectory, "bin");
    public string InternalDirectory => Path.Combine(InstallationDirectory, "internal");
    public string DataDirectory { get; }
    public string ShimsDirectory => Path.Combine(DataDirectory, "shims");
    public string ShimExecutable => Path.Combine(InternalDirectory, "PathMan.Shim.exe");
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
        return new PathmanLayout(installation, dataDirectory);
    }
}
