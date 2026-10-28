using Pathman.Core.Localization;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public static class EnvironmentMonitorNotices
{
    public const string UnknownOriginMessage="未能确认本次 PATH 修改来源，已保留外部更改，未自动整理。";
    public static void ReportUnknown(EnvironmentWorkspace state,VariableScope scope,string? before,string? after)
    {
        state.UnattributedPathChanges.RemoveAll(item=>item.Scope==scope);
        var change=state.Changes.FirstOrDefault(item=>item.Scope==scope && IsPath(item.Name) && item.Before==before && item.After==after);
        var time=change?.Time ?? DateTime.Now;
        state.UnattributedPathChanges.Add(new(scope,before,after,time));
        state.MonitorMessage=L.Get(UnknownOriginMessage);state.MonitorMessageTime=time;
    }
    public static bool Reconcile(EnvironmentWorkspace state)
    {
        var unknown=Text.IsTranslation(UnknownOriginMessage,state.MonitorMessage);
        var changed=false;
        // Older workspaces stored only a message, with no event identity.
        if(unknown && state.UnattributedPathChanges.Count==0)
        {
            foreach(var scope in new[]{VariableScope.User,VariableScope.Machine})
            {
                var latest=state.Changes.Where(item=>item.Scope==scope && IsPath(item.Name)).MaxBy(item=>item.Time);
                if(latest is {IsExternal:true} && (state.MonitorMessageTime is null || latest.Time>=state.MonitorMessageTime.Value.AddSeconds(-2)))
                    state.UnattributedPathChanges.Add(new(scope,latest.Before,latest.After,latest.Time));
            }
            changed=true;
        }
        changed|=state.UnattributedPathChanges.RemoveAll(issue=>
        {
            var path=state.Snapshot.FirstOrDefault(item=>item.Scope==issue.Scope && IsPath(item.Name));
            return path?.Value==issue.Before || state.Changes.Any(item=>item.Scope==issue.Scope && IsPath(item.Name) && item.IsOwn && item.Time>=issue.Time);
        })>0;
        if(unknown && state.UnattributedPathChanges.Count==0)
        {state.MonitorMessage=null;state.MonitorMessageTime=null;changed=true;}
        return changed;
    }
    private static bool IsPath(string name)=>name.Equals("Path",StringComparison.OrdinalIgnoreCase);
}
