using System;
internal static class ScreenSessionTests
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Refused(Action action,string code)
    {
        try{action();throw new Exception("Expected refusal: "+code);}
        catch(InvalidOperationException ex){Check(ex.Message==code,"Refusal code: "+code);}
    }
    public static int Main(){try{Run();return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    public static void Run()
    {
        long now=0;var session=new ScreenSession(()=>now);
        Check(session.Snapshot.State==ScreenSessionState.Off,"default off");
        Refused(()=>session.Require("connection-a",Guid.NewGuid(),false),"permission-required");
        Guid first=session.Request("connection-a","monitor-1",true);
        Refused(()=>session.Require("connection-a",first,false),"permission-required");
        Refused(()=>session.Request("connection-b","monitor-1",true),"busy");
        Refused(()=>session.GrantLocal(Guid.NewGuid(),false),"stale-request");
        session.GrantLocal(first,false);
        now=1234;Check(session.Snapshot.ElapsedMs==1234,"elapsed since actual grant");
        session.Require("connection-a",first,false);
        Refused(()=>session.Require("connection-a",first,true),"control-required");
        Refused(()=>session.Require("connection-b",first,false),"session-mismatch");
        Refused(()=>session.Require("connection-a",Guid.NewGuid(),false),"session-mismatch");
        session.Revoke();
        Refused(()=>session.Require("connection-a",first,false),"permission-required");
        Guid second=session.Request("connection-b","monitor-2",false);
        Check(second!=first,"reconnect new ID");
        Refused(()=>session.Require("connection-b",second,false),"permission-required");
        Refused(()=>session.GrantLocal(second,true),"control-not-requested");
        session.DenyLocal(second);
        Refused(()=>session.GrantLocal(second,false),"stale-request");
        Guid third=session.Request("connection-c","monitor-2",true);
        Check(session.Snapshot.ElapsedMs==0,"pending has no elapsed timer");
        now+=30000;
        Refused(()=>session.GrantLocal(third,true),"stale-request");
        Check(session.Snapshot.State==ScreenSessionState.Revoked,"pending expires");
        Guid fourth=session.Request("connection-c","monitor-2",true);
        session.GrantLocal(fourth,true);
        Check(session.Snapshot.CanView&&session.Snapshot.CanControl,"control includes view");
        now+=599999;session.Require("connection-c",fourth,true);
        now++;
        Refused(()=>session.Require("connection-c",fourth,false),"permission-required");
        Check(!session.Snapshot.CanView&&!session.Snapshot.CanControl,"expiry revokes both");
        Guid fifth=session.Request("connection-d","monitor-1",true);session.GrantLocal(fifth,true);
        session.Disconnect("connection-c");session.Require("connection-d",fifth,true);
        session.Disconnect("connection-d");
        Refused(()=>session.Require("connection-d",fifth,true),"permission-required");
        session.Revoke();session.Revoke();
        Check(session.Snapshot.ConnectionId==null&&session.Snapshot.MonitorId==null,"revoke releases context");
        Console.WriteLine("PASS screen default/pending/view/control/owner/deny/stop/disconnect/expiry/reconnect");
    }
}
