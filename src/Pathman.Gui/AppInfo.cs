using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using Pathman.Core;
using Pathman.Core.Updates;

namespace Pathman.Gui;

internal static class AppInfo
{
    public static string Version {get;}=typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item=>item.Key=="PathMan.ReleaseVersion")?.Value
        ?? (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version!.ToString(3)).Split('+')[0];
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
