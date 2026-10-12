using System;
using System.Threading;
internal enum SupportState
{
    Stopped, Connecting, RelayWaiting, SessionActive, Reconnecting, Failed
}
internal sealed class SupportSnapshot
{
    public readonly int Generation;
    public readonly SupportState State;
    public readonly bool LanReady;
    public readonly bool RelaySession;
    public readonly bool RelayReady;
    public readonly string Reason;
    public readonly string LanWarning;
    public readonly DateTime ObservedAt;
    public SupportSnapshot(int generation,SupportState state,bool lanReady,bool relaySession,string reason,bool relayReady=false,string lanWarning="")
    {
        Generation=generation;
        State=state;
        LanReady=lanReady;
        RelaySession=relaySession;
        RelayReady=state!=SupportState.Stopped&&(relayReady||state==SupportState.RelayWaiting||(state==SupportState.SessionActive&&relaySession));
        Reason=reason;
        LanWarning=lanWarning;
        ObservedAt=DateTime.UtcNow;
    }
    public string ColorName
    {
        get
        {
            return State==SupportState.Stopped?"Red":RelayReady?"Green":LanReady?"Yellow":"Red";
        }
    }
    public string Description
    {
        get
        {
            if(State==SupportState.SessionActive)return "Busy - "+Reason;
            if(ColorName=="Green")return "Connected to relay - ready for requests";
            if(ColorName=="Yellow")return "Local network only - relay is not connected. "+Reason;
            return Reason;
        }
    }
}
internal sealed class SessionGate
{
    int busy;
    public bool IsBusy
    {
        get
        {
            return Interlocked.CompareExchange(ref busy,0,0)!=0;
        }
    }
    public bool TryEnter()
    {
        return Interlocked.CompareExchange(ref busy,1,0)==0;
    }
    public void Exit()
    {
        Interlocked.Exchange(ref busy,0);
    }
}
