using System;
using System.Diagnostics;

internal enum ScreenSessionState { Off, Pending, Granted, Revoked }
internal sealed class ScreenSessionSnapshot
{
    public readonly ScreenSessionState State;
    public readonly Guid SessionId;
    public readonly string ConnectionId,MonitorId;
    public readonly bool CanView,CanControl,RequestedControl;
    public readonly long RemainingMs;
    public readonly long ElapsedMs;
    public readonly bool AutoAccepted;
    internal ScreenSessionSnapshot(ScreenSessionState state,Guid id,string connection,string monitor,bool requestedControl,bool control,long remaining,long elapsed,bool autoAccepted)
    {
        State=state;SessionId=id;ConnectionId=connection;MonitorId=monitor;
        RequestedControl=requestedControl;CanView=state==ScreenSessionState.Granted;CanControl=CanView&&control;RemainingMs=remaining;ElapsedMs=elapsed;AutoAccepted=CanView&&autoAccepted;
    }
}

// In-memory authorization only. No transport, capture or native input is enabled here.
// Local approval methods must never be exposed as remote operations.
internal sealed class ScreenSession
{
    readonly object gate=new object();
    readonly Func<long> milliseconds;
    ScreenSessionState state;
    Guid id;
    string connection,monitor;
    bool requestedControl,control;
    bool autoAccepted;
    long deadline;
    long grantedAt;
    public ScreenSession():this(MonotonicClock()){}
    internal ScreenSession(Func<long> clock){if(clock==null)throw new ArgumentNullException("clock");milliseconds=clock;}
    static Func<long> MonotonicClock(){var clock=Stopwatch.StartNew();return ()=>clock.ElapsedMilliseconds;}
    void Expire(){if((state==ScreenSessionState.Pending||state==ScreenSessionState.Granted)&&milliseconds()>=deadline)RevokeCore();}
    void RevokeCore(){state=ScreenSessionState.Revoked;connection=null;monitor=null;requestedControl=false;control=false;deadline=0;}
    public ScreenSessionSnapshot Snapshot
    {
        get{lock(gate){Expire();return new ScreenSessionSnapshot(state,id,connection,monitor,requestedControl,control,Math.Max(0,deadline-milliseconds()),state==ScreenSessionState.Granted?Math.Max(0,milliseconds()-grantedAt):0,autoAccepted);}}
    }
    public Guid Request(string connectionId,string monitorId,bool wantControl)
    {
        if(string.IsNullOrWhiteSpace(connectionId)||connectionId.Length>128||string.IsNullOrWhiteSpace(monitorId)||monitorId.Length>128)throw new ArgumentException("Invalid screen context.");
        lock(gate)
        {
            Expire();if(state==ScreenSessionState.Pending||state==ScreenSessionState.Granted)throw new InvalidOperationException("busy");
            id=Guid.NewGuid();connection=connectionId;monitor=monitorId;requestedControl=wantControl;control=false;
            deadline=milliseconds()+30000;state=ScreenSessionState.Pending;return id;
        }
    }
    public void GrantLocal(Guid requestId,bool allowControl,bool automatically=false)
    {
        lock(gate)
        {
            Expire();if(state!=ScreenSessionState.Pending||requestId!=id)throw new InvalidOperationException("stale-request");
            if(allowControl&&!requestedControl)throw new InvalidOperationException("control-not-requested");
            control=allowControl;autoAccepted=automatically;state=ScreenSessionState.Granted;grantedAt=milliseconds();deadline=grantedAt+600000;
        }
    }
    public void DenyLocal(Guid requestId)
    {
        lock(gate){Expire();if(state!=ScreenSessionState.Pending||requestId!=id)throw new InvalidOperationException("stale-request");RevokeCore();}
    }
    public void Require(string connectionId,Guid sessionId,bool input)
    {
        lock(gate)
        {
            Expire();if(state!=ScreenSessionState.Granted)throw new InvalidOperationException("permission-required");
            if(connectionId!=connection||sessionId!=id)throw new InvalidOperationException("session-mismatch");
            if(input&&!control)throw new InvalidOperationException("control-required");
        }
    }
    public void Disconnect(string connectionId){lock(gate){if(connectionId==connection)RevokeCore();}}
    public void Revoke(){lock(gate)RevokeCore();}
    public void DisableControl(){lock(gate){control=false;requestedControl=false;}}
}
