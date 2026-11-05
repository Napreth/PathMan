using System.Security.AccessControl;
using System.Security.Principal;
using Pathman.Core;
using Pathman.Service;

internal static class GlobalServiceTests
{
    private sealed record Desktop(string Sid,int Session);
    public static void Run(Action<bool,string> assert)
    {
        bool Protected(string sddl,bool payload=true)=>PrivilegedServiceSource.IsProtected(new RawSecurityDescriptor(sddl),payload);
        assert(Protected("O:BAG:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)"),"A protected machine payload may be readable by ordinary users");
        foreach(var right in new[]{"GW","GA","WD","WO","SD","DC","0x2","0x4","0x10","0x100"})
            assert(!Protected($"O:BAG:BAD:(A;;FA;;;BA)(A;;{right};;;BU)"),"An ordinary user's dangerous payload grant must be rejected: "+right);
        assert(!Protected("O:BAG:BAD:(D;;GW;;;BU)(A;;GA;;;BU)"),"Source validation conservatively rejects broad write grants even with deny ACEs");
        assert(!Protected("O:S-1-5-21-1-2-3-1001G:BAD:(A;;FA;;;BA)"),"An ordinary owner can change a DACL and must not own a service source");
        assert(!Protected("O:BAG:BA"),"An absent DACL must not authorize a service source");
        assert(Protected("O:BAG:BAD:(A;;FA;;;BA)(A;;0x4;;;BU)",false) && !Protected("O:BAG:BAD:(A;;FA;;;BA)(A;;0x40;;;BU)",false),"Ancestor validation allows unrelated new folders but forbids replacing existing children");
        var sessions=new[]{new Desktop("a",1),new Desktop("a",4),new Desktop("b",7)};
        var polls=new List<string>();var notices=new List<int>();
        SessionRouting.Poll(sessions,item=>item.Sid,group=> {polls.Add(group[0].Sid);return group[0].Sid=="a";},item=>notices.Add(item.Session),false);
        assert(polls.SequenceEqual(new[]{"a","b"}) && notices.SequenceEqual(new[]{1,4}),"One user PATH write must poll once and notify every session of that user");
        notices.Clear();SessionRouting.Poll(sessions,item=>item.Sid,_=>false,item=>notices.Add(item.Session),true);
        assert(notices.SequenceEqual(new[]{1,4,7}),"A machine PATH write must notify all desktops, including multiple sessions of one SID");
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User!.Value;
        using var first=BackgroundProcess.OwnerMutex(sid,"",101);using var second=BackgroundProcess.OwnerMutex(sid,"",102);
        assert(first.WaitOne(0),"The first session can own its personal background");
        try {var acquired=Task.Run(()=> {if(!second.WaitOne(0)) return false;second.ReleaseMutex();return true;}).GetAwaiter().GetResult();assert(acquired,"Restoring the second desktop must not be suppressed by the first session's background");}
        finally {first.ReleaseMutex();}
        using var writer=BackgroundProcess.WriterMutex(sid);writer.WaitOne();
        try {assert(!Task.Run(()=> {using var other=BackgroundProcess.WriterMutex(sid);if(!other.WaitOne(0)) return false;other.ReleaseMutex();return true;}).GetAwaiter().GetResult(),"Personal processes in different sessions must serialize their shared workspace writes");}
        finally {writer.ReleaseMutex();}
        Console.WriteLine("PASS: privileged source ACLs, ancestor replacement rights, per-user writes and per-session notifications/background ownership.");
    }
}
