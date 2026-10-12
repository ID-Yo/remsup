using System;
using System.Diagnostics;

internal sealed class ScreenGeometry
{
    public readonly string MonitorId;
    public readonly int Version,X,Y,Width,Height;
    public ScreenGeometry(string monitor,int version,int x,int y,int width,int height)
    {
        if(string.IsNullOrWhiteSpace(monitor)||version<1||width<2||height<2||width>32768||height>32768||(long)x+width>int.MaxValue||(long)y+height>int.MaxValue)throw new ArgumentException("Invalid monitor geometry.");
        MonitorId=monitor;Version=version;X=x;Y=y;Width=width;Height=height;
    }
    internal bool Matches(ScreenGeometry other){return other!=null&&MonitorId==other.MonitorId&&Version==other.Version&&X==other.X&&Y==other.Y&&Width==other.Width&&Height==other.Height;}
}
internal sealed class ScreenFrameReference
{
    public readonly Guid FrameId,SessionId;
    public readonly ScreenGeometry Geometry;
    public readonly int Width,Height;
    internal readonly long CapturedMs;
    internal ScreenFrameReference(Guid session,ScreenGeometry geometry,int width,int height,long now)
    {FrameId=Guid.NewGuid();SessionId=session;Geometry=geometry;Width=width;Height=height;CapturedMs=now;}
}
internal sealed class ScreenPoint
{
    public readonly int X,Y;
    internal ScreenPoint(int x,int y){X=x;Y=y;}
}
// Metadata validation only. No OS input or screen capture occurs in this class.
internal sealed class ScreenFrames
{
    readonly object gate=new object();readonly ScreenSession session;readonly Func<long> milliseconds;
    ScreenFrameReference latest;long lastCapture=-1000,lastInput=-200;
    public ScreenFrames(ScreenSession owner):this(owner,Clock()){}
    internal ScreenFrames(ScreenSession owner,Func<long> clock){if(owner==null||clock==null)throw new ArgumentNullException();session=owner;milliseconds=clock;}
    static Func<long> Clock(){var clock=Stopwatch.StartNew();return ()=>clock.ElapsedMilliseconds;}
    public void PrepareCapture(string connection,Guid lease,ScreenGeometry geometry)
    {
        lock(gate)
        {
            session.Require(connection,lease,false);
            if(geometry==null||geometry.MonitorId!=session.Snapshot.MonitorId)throw new InvalidOperationException("monitor-mismatch");
            if(milliseconds()-lastCapture<1000)throw new InvalidOperationException("capture-rate-limit");
            if(latest!=null&&latest.SessionId==lease&&!latest.Geometry.Matches(geometry)){session.Revoke();throw new InvalidOperationException("geometry-changed");}
        }
    }
    public ScreenFrameReference Record(string connection,Guid lease,ScreenGeometry geometry,int width,int height)
    {
        lock(gate)
        {
            session.Require(connection,lease,false);
            if(geometry==null||geometry.MonitorId!=session.Snapshot.MonitorId)throw new InvalidOperationException("monitor-mismatch");
            if(width<2||height<2||width>1920||height>1920||width>geometry.Width||height>geometry.Height||Math.Abs((long)width*geometry.Height-(long)height*geometry.Width)>Math.Max(geometry.Width,geometry.Height))throw new InvalidOperationException("invalid-frame-size");
            long now=milliseconds();if(now-lastCapture<1000)throw new InvalidOperationException("capture-rate-limit");
            if(latest!=null&&latest.SessionId==lease&&!latest.Geometry.Matches(geometry)){session.Revoke();throw new InvalidOperationException("geometry-changed");}
            latest=new ScreenFrameReference(lease,geometry,width,height,now);lastCapture=now;return latest;
        }
    }
    void ValidateContext(string connection,Guid lease,Guid frame,ScreenGeometry current,int geometryVersion) {
            session.Require(connection,lease,true);
            if(latest==null||latest.SessionId!=lease||latest.FrameId!=frame)throw new InvalidOperationException("stale-frame");
            if(!latest.Geometry.Matches(current)){session.Revoke();throw new InvalidOperationException("geometry-changed");}
            if(geometryVersion!=-1&&geometryVersion!=latest.Geometry.Version)throw new InvalidOperationException("geometry-version-mismatch");
            long now=milliseconds();if(now-latest.CapturedMs>30000)throw new InvalidOperationException("stale-frame");
    }
    public void ValidateKeyboard(string connection,Guid lease,Guid frame,ScreenGeometry current,int geometryVersion) {
        lock(gate){ValidateContext(connection,lease,frame,current,geometryVersion);long now=milliseconds();if(now-lastInput<200)throw new InvalidOperationException("input-rate-limit");lastInput=now;}
    }
    public ScreenPoint ValidateMouse(string connection,Guid lease,Guid frame,ScreenGeometry current,string action,int x,int y,int steps,int geometryVersion=-1)
    {
        lock(gate)
        {
            ValidateContext(connection,lease,frame,current,geometryVersion);
            long now=milliseconds();
            if(x<0||y<0||x>=latest.Width||y>=latest.Height)throw new InvalidOperationException("coordinates-out-of-range");
            if(action!="click"&&action!="double-click"&&action!="right-click"&&action!="scroll")throw new InvalidOperationException("invalid-action");
            if(action=="scroll"?(steps==0||steps<-10||steps>10):steps!=0)throw new InvalidOperationException("invalid-scroll");
            if(now-lastInput<200)throw new InvalidOperationException("input-rate-limit");
            lastInput=now;
            return new ScreenPoint(current.X+(int)Math.Round((double)x*(current.Width-1)/(latest.Width-1),MidpointRounding.AwayFromZero),current.Y+(int)Math.Round((double)y*(current.Height-1)/(latest.Height-1),MidpointRounding.AwayFromZero));
        }
    }
}
