using System.IO;
using System.Security;
using System.Text.Json;
using Pathman.Core.EnvironmentVariables;
using Pathman.Gui.Views;

namespace Pathman.Gui;

public partial class MainWindow
{
    private bool healthWorking;
    private async Task IgnoreHealthIssueAsync(HealthIgnoreEventArgs args)
    {
        if(healthWorking) return;
        try
        {
            store.Update(state=> {if(args.Restore) state.IgnoredHealthIssues.Remove(args.Issue.Id); else state.IgnoredHealthIssues.Add(args.Issue.Id);});
            await ScanHealthAsync();
            health.ShowActionStatus(args.Restore ? L.Get("已恢复提醒。") : L.Get("已忽略此项；来源发生变化后会重新检测。"));
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {health.ShowActionStatus(error.Message,true);}
    }
    private async Task ApplyHealthFixAsync(EnvironmentHealthFix fix)
    {
        if(healthWorking || !await variables.ConfirmLeaveAsync()) return;
        healthWorking=true; Navigation.IsEnabled=false; health.SetWorking(true); health.ShowActionStatus(L.Get("正在保存…"));
        try
        {
            var current=await Task.Run(EnvironmentMonitor.ReadAll);
            fix.Validate(current);
            var scope=fix.Mutations[0].Scope;
            // Check all scopes before requesting UAC. The elevated helper only
            // opens machine keys; each actual write also checks its saved value.
            await EnvironmentEditHost.ApplyBatchAsync(fix.Mutations,fix.Preconditions.Where(item=>item.Scope==scope).ToArray(),store,layout.DataDirectory);
            await variables.ReloadAsync(); await ScanHealthAsync();
            health.ShowActionStatus(L.Get("已保存并重新检测。"));
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException or OperationCanceledException)
        {health.ShowActionStatus(L.Get("未保存。")+error.Message,true);await ScanHealthAsync();}
        finally {healthWorking=false;Navigation.IsEnabled=true;health.SetWorking(false);}
    }
}
