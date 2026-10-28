namespace Pathman.Core.EnvironmentVariables;

public static class InstallerEnvironmentSync
{
    public static void Synchronize(PathmanLayout layout)
    {
        var store=new EnvironmentWorkspaceStore(layout.DataDirectory);
        if(File.Exists(store.FilePath)) store.Inspect(EnvironmentMonitorNotices.Reconcile);
    }
}
