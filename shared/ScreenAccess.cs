using System;

internal interface IScreenSurface
{
    bool IsInteractive { get; }
    ScreenGeometry[] Monitors();
    ScreenGeometry Geometry(string monitorId);
    ScreenImage Capture(ScreenGeometry geometry);
    bool IsProtectedPoint(ScreenPoint point);
    void Mouse(string action,ScreenPoint point,int scrollSteps,Action authorize);
    void Keyboard(string text,string key,ScreenGeometry geometry,Action authorize);
}
internal sealed class ScreenImage
{
    public readonly byte[] Png;
    public readonly int Width,Height;
    public ScreenImage(byte[] png,int width,int height){Png=png;Width=width;Height=height;}
}
internal sealed class ScreenCaptureResult
{
    public readonly ScreenFrameReference Frame;
    public readonly ScreenImage Image;
    public readonly DateTime CapturedUtc;
    internal ScreenCaptureResult(ScreenFrameReference frame,ScreenImage image){Frame=frame;Image=image;CapturedUtc=DateTime.UtcNow;}
}
// All lifecycle and provider calls use one lock. Stop waits for an in-flight
// bounded provider call, then prevents subsequent operations from starting.
// The supplied provider must enforce
// the desktop/native checks again immediately before touching OS resources.
internal sealed class ScreenAccess
{
    readonly object gate=new object();readonly ScreenSession session;readonly ScreenFrames frames;readonly IScreenSurface surface;
    ScreenGeometry requestedGeometry;
    bool viewAllowed=true,controlAllowed=true,mouseAllowed=true,typingAllowed=true;
    public bool ControlAllowed{get{lock(gate)return controlAllowed;}}
    bool autoAcceptLocal;
    public bool AutoAcceptLocal{get{lock(gate)return autoAcceptLocal;}set{lock(gate)autoAcceptLocal=value;}}
    public bool CanMouse{get{lock(gate)return session.Snapshot.CanControl&&mouseAllowed;}}
    public bool CanType{get{lock(gate)return session.Snapshot.CanControl&&typingAllowed;}}
    public void SetPolicy(bool view,bool control){lock(gate){viewAllowed=view;controlAllowed=control;if(!view)session.Revoke();if(!control)session.DisableControl();}}
    public void SetLocalInput(bool mouse,bool typing){lock(gate){mouseAllowed=mouse;typingAllowed=typing;}}
    internal ScreenAccess(IScreenSurface provider,Func<long> clock)
    {
        if(provider==null)throw new ArgumentNullException("provider");surface=provider;session=new ScreenSession(clock);frames=new ScreenFrames(session,clock);
    }
    public ScreenSessionSnapshot Snapshot{get{lock(gate)return session.Snapshot;}}
    public Guid Request(string connection,string monitor,bool control){lock(gate){if(!viewAllowed)throw new InvalidOperationException("screen-disabled");Interactive();var geometry=surface.Geometry(monitor);Guid id=session.Request(connection,monitor,control&&controlAllowed);requestedGeometry=geometry;mouseAllowed=typingAllowed=true;return id;}}
    public void GrantLocal(Guid id,bool control,bool automatically=false){lock(gate){if(!viewAllowed)throw new InvalidOperationException("screen-disabled");if(control&&!controlAllowed)throw new InvalidOperationException("control-disabled");Interactive();session.GrantLocal(id,control,automatically);}}
    public void GrantConfigured(Guid id,string connection){lock(gate){var current=session.Snapshot;if(!autoAcceptLocal||!viewAllowed||current.State!=ScreenSessionState.Pending||current.SessionId!=id||current.ConnectionId!=connection)return;Interactive();session.GrantLocal(id,current.RequestedControl&&controlAllowed,true);}}
    public void DenyLocal(Guid id){lock(gate)session.DenyLocal(id);}
    public void Stop(){lock(gate)session.Revoke();}
    public void RequireControl(string connection,Guid lease){lock(gate){session.Require(connection,lease,true);Interactive();}}
    public void DisableControl(){lock(gate)session.DisableControl();}
    public void Disconnect(string connection){lock(gate)session.Disconnect(connection);}
    public ScreenGeometry[] Monitors(){lock(gate){Interactive();return surface.Monitors();}}
    public void RefreshEnvironment(){lock(gate){var state=session.Snapshot.State;if(state==ScreenSessionState.Pending||state==ScreenSessionState.Granted)Interactive();}}
    void Interactive()
    {
        if(!surface.IsInteractive){session.Revoke();throw new InvalidOperationException("desktop-unavailable");}
        var snapshot=session.Snapshot;
        if(snapshot.State==ScreenSessionState.Pending||snapshot.State==ScreenSessionState.Granted)
        {
            try{if(requestedGeometry==null||!requestedGeometry.Matches(surface.Geometry(snapshot.MonitorId)))throw new InvalidOperationException("geometry-changed");}
            catch{session.Revoke();throw;}
        }
    }
    public ScreenCaptureResult Capture(string connection,Guid lease)
    {
        lock(gate)
        {
            session.Require(connection,lease,false);Interactive();
            var geometry=surface.Geometry(session.Snapshot.MonitorId);
            frames.PrepareCapture(connection,lease,geometry);
            var image=surface.Capture(geometry);
            Interactive();
            if(!geometry.Matches(surface.Geometry(geometry.MonitorId))){session.Revoke();throw new InvalidOperationException("geometry-changed");}
            ValidateImage(image);
            return new ScreenCaptureResult(frames.Record(connection,lease,geometry,image.Width,image.Height),image);
        }
    }
    static void ValidateImage(ScreenImage image)
    {
        if(image==null||image.Png==null||image.Png.Length<33||image.Png.Length>4194304)throw new InvalidOperationException("invalid-image");
        byte[] png=image.Png;byte[] signature={137,80,78,71,13,10,26,10};
        for(int i=0;i<signature.Length;i++)if(png[i]!=signature[i])throw new InvalidOperationException("invalid-image");
        if(png[8]!=0||png[9]!=0||png[10]!=0||png[11]!=13||png[12]!=73||png[13]!=72||png[14]!=68||png[15]!=82||ReadSize(png,16)!=image.Width||ReadSize(png,20)!=image.Height)throw new InvalidOperationException("invalid-image");
        // Full decoding remains the recipient's responsibility; this checks the
        // trusted encoder's declared size before publishing bounded bytes.
    }
    static long ReadSize(byte[] data,int offset){return ((long)data[offset]<<24)|((long)data[offset+1]<<16)|((long)data[offset+2]<<8)|data[offset+3];}
    public void Keyboard(string connection,Guid lease,Guid frame,int geometryVersion,string text,string key) {
        lock(gate){if(!typingAllowed)throw new InvalidOperationException("typing-disabled");ScreenKeyboard.Validate(text,key);session.Require(connection,lease,true);Interactive();var geometry=surface.Geometry(session.Snapshot.MonitorId);
            frames.ValidateKeyboard(connection,lease,frame,geometry,geometryVersion);Interactive();session.Require(connection,lease,true);
            surface.Keyboard(text,key,geometry,()=>session.Require(connection,lease,true));}
    }
    public void Mouse(string connection,Guid lease,Guid frame,string action,int x,int y,int scrollSteps,int geometryVersion=-1)
    {
        lock(gate)
        {
            if(!mouseAllowed)throw new InvalidOperationException("mouse-disabled");
            session.Require(connection,lease,true);Interactive();var geometry=surface.Geometry(session.Snapshot.MonitorId);
            var point=frames.ValidateMouse(connection,lease,frame,geometry,action,x,y,scrollSteps,geometryVersion);
            if(surface.IsProtectedPoint(point))throw new InvalidOperationException("protected-window");
            Interactive();session.Require(connection,lease,true);surface.Mouse(action,point,scrollSteps,()=>session.Require(connection,lease,true));
        }
    }
}

// Dispatch status is explicit only when the native provider can prove it.
internal sealed class ScreenInputFailure:InvalidOperationException
{
    internal readonly bool? Dispatched;
    internal ScreenInputFailure(string code,bool? dispatched):base(code){Dispatched=dispatched;}
}
