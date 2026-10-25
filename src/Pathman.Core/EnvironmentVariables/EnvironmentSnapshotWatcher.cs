namespace Pathman.Core.EnvironmentVariables;

// An observer only: it never changes registry values, monitor preferences or history.
public sealed class EnvironmentSnapshotWatcher : IDisposable
{
    private readonly CancellationTokenSource stop=new();
    private readonly Task worker;
    public event Action<IReadOnlyList<EnvironmentVariableInfo>>? Changed;
    public event Action<Exception>? Failed;
    public EnvironmentSnapshotWatcher(Action<IReadOnlyList<EnvironmentVariableInfo>>? changed=null)
    {
        if(changed is not null) Changed+=changed;
        worker=Task.Run(()=>
        {
            using var signal=new EnvironmentRegistrySignal();
            IReadOnlyList<EnvironmentVariableInfo>? previous=null;
            while(!stop.IsCancellationRequested)
            {
                try
                {
                    // Arm before reading. A write during the read remains signalled.
                    var current=EnvironmentMonitor.ReadAll();
                    if(previous is null || !Equal(previous,current)) {previous=current;Changed?.Invoke(current);}
                }
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException) {Failed?.Invoke(error);}
                var reason=WaitHandle.WaitAny([stop.Token.WaitHandle,signal.UserChanged,signal.MachineChanged],500);
                if(reason==0) break;
                if(reason is 1 or 2) signal.Arm(reason-1);
            }
        });
    }
    public static bool Equal(IReadOnlyList<EnvironmentVariableInfo> before,IReadOnlyList<EnvironmentVariableInfo> after)
    {
        if(before.Count!=after.Count) return false;
        var values=before.ToDictionary(item=>EnvironmentWorkspace.Key(item.Scope,item.Name),StringComparer.OrdinalIgnoreCase);
        return after.All(item=>values.TryGetValue(EnvironmentWorkspace.Key(item.Scope,item.Name),out var old) && old.RawValue==item.RawValue && old.Kind==item.Kind);
    }
    public void Dispose() {stop.Cancel();_ = worker.ContinueWith(_=>stop.Dispose(),TaskScheduler.Default);}
}
