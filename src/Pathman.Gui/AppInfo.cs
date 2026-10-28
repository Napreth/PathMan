using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using Pathman.Core;
using Pathman.Core.Updates;

namespace Pathman.Gui;

internal static class AppInfo
{
    public static string Version {get;}=Metadata("PathMan.VersionName")
        ?? (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version!.ToString(3)).Split('+')[0];
    public static string VersionNumber {get;}=Metadata("PathMan.VersionNumber") ?? typeof(App).Assembly.GetName().Version!.ToString(3);
    public static string DisplayVersion=>$"{Version} ({VersionNumber})";
    private static string? Metadata(string key)=>typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item=>item.Key==key)?.Value;
    public static UpdatePackage Package {get;}=new(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(item=>item.Key=="PathMan.PackageType" && item.Value=="SelfContained"));
    public static PathmanLayout Layout()
    {
#if PATHMAN_UPDATE_TEST
        return new(AppContext.BaseDirectory,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PathMan","update-test"));
#else
        return PathmanLayout.Discover();
#endif
    }
}
