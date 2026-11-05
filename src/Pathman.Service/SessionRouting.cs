namespace Pathman.Service;

internal static class SessionRouting
{
    // A user has one registry/workspace, but every desktop needs its own notice.
    internal static void Poll<T>(IEnumerable<T> sessions,Func<T,string> sid,Func<IReadOnlyList<T>,bool> poll,Action<T> notify,bool machineChanged)
    {
        foreach(var group in sessions.GroupBy(sid))
        {
            var desktops=group.ToArray();var changed=poll(desktops);
            if(machineChanged || changed) foreach(var desktop in desktops) notify(desktop);
        }
    }
}
