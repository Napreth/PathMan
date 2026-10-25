using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui;

internal static class EnvironmentEditHost
{
    private static readonly SemaphoreSlim Gate=new(1,1);
    private static ElevatedEnvironmentSession? session;
    public static event EventHandler? AuthorizationChanged;
    public static bool IsAuthorized=>session?.IsAlive==true;
    public static bool IsAdministrator {get {using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}}
    public static Task ApplyAsync(EnvironmentMutation mutation,EnvironmentWorkspaceStore store,string dataDirectory)=>ApplyBatchAsync([mutation],[],store,dataDirectory);
    public static async Task ApplyBatchAsync(IReadOnlyList<EnvironmentMutation> mutations,IReadOnlyList<VariableSnapshot> preconditions,EnvironmentWorkspaceStore store,string dataDirectory)
    {
        if(mutations.Count==0) return;
        var scope=mutations[0].Scope;
        if(mutations.Any(item=>item.Scope!=scope)) throw new ArgumentException("一次保存只支持一种作用域。");
        if(scope!=VariableScope.Machine || IsAdministrator)
        {
            await Task.Run(()=> {
                if(mutations.Count==1 && preconditions.Count==0) WindowsEnvironmentVariableEditor.Apply(mutations[0],store);
                else WindowsEnvironmentVariableEditor.ApplyBatch(mutations,preconditions,store);
            });
            return;
        }
        await Gate.WaitAsync();
        try
        {
            if(session is null || !session.IsAlive)
            {
                session?.Dispose();session=null;
                session=await ElevatedEnvironmentSession.StartAsync();
                AuthorizationChanged?.Invoke(null,EventArgs.Empty);
            }
            // The registry signal can arrive before the unelevated process records
            // the result. Pause observation briefly across that hand-off so an
            // application write is never mistaken for an external PATH addition.
            store.Update(state=>state.RegistryWriteInProgressUntil=DateTime.UtcNow.AddMinutes(2));
            try
            {
                try {await session.ApplyAsync(mutations,preconditions);}
                catch(IOException error) when(error is not EnvironmentOperationException) {session.Dispose();session=null;AuthorizationChanged?.Invoke(null,EventArgs.Empty);throw;}
                try {await Task.Run(()=>WindowsEnvironmentVariableEditor.RecordOwnChanges(store,mutations));}
                catch(Exception error) when(error is IOException or UnauthorizedAccessException)
                {throw new IOException("系统变量已保存，但用户记录更新失败。请刷新确认。"+error.Message,error);}
            }
            finally {store.Update(state=>state.RegistryWriteInProgressUntil=null);}
        }
        catch(Win32Exception error) when(error.NativeErrorCode==1223) {throw new OperationCanceledException("已取消管理员操作。");}
        finally {Gate.Release();}
    }
    public static void Shutdown() {session?.Dispose();session=null;AuthorizationChanged?.Invoke(null,EventArgs.Empty);}
}
