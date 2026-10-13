using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

// Bounded framing/dispatch after SupportHost pairing. Local owner policy owns grants.
internal sealed class ScreenChannel
{
    readonly ScreenAccess access;
    internal Func<string,JsonElement,Func<bool>,object> SettingsHandler;
    internal event Action<ScreenSessionSnapshot,string> ConsentRequested;
    internal ScreenChannel(ScreenAccess owner){access=owner??throw new ArgumentNullException(nameof(owner));}
    internal void Run(Stream stream,string connection,CancellationToken cancel)
    {
        using(cancel.Register(()=>stream.Dispose()))
        try
        {
            if(stream.CanTimeout){stream.ReadTimeout=10000;stream.WriteTimeout=10000;}
            while(!cancel.IsCancellationRequested&&ProcessOne(stream,connection)){}
        }
        finally{access.Disconnect(connection);}
    }
    static byte[] Read(Stream stream,int size)
    {
        var data=new byte[size];int offset=0;
        while(offset<size){int count=stream.Read(data,offset,size-offset);if(count==0)throw new EndOfStreamException();offset+=count;}return data;
    }
    static int Length(Stream stream,int cap)
    {
        byte[] header=Read(stream,4);uint size=((uint)header[0]<<24)|((uint)header[1]<<16)|((uint)header[2]<<8)|header[3];
        if(size<2||size>cap)throw new InvalidDataException("message-size-limit");return (int)size;
    }
    static void WriteBytes(Stream stream,byte[] data)
    {
        int size=data.Length;byte[] header={(byte)(size>>24),(byte)(size>>16),(byte)(size>>8),(byte)size};stream.Write(header,0,4);stream.Write(data,0,size);
    }
    static void Write(Stream stream,object data){byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(data);if(bytes.Length>4096)throw new InvalidDataException("metadata-size-limit");WriteBytes(stream,bytes);}
    static string Text(JsonElement request,string name){string value=request.GetProperty(name).GetString();if(string.IsNullOrEmpty(value)||value.Length>128)throw new InvalidDataException("invalid-field");return value;}
    static Guid Id(JsonElement request,string name){return Guid.ParseExact(Text(request,name),"D");}
    static void Fields(JsonElement request,params string[] allowed)
    {
        var names=new HashSet<string>(allowed);var seen=new HashSet<string>();
        foreach(var property in request.EnumerateObject())if(!names.Contains(property.Name)||!seen.Add(property.Name))throw new InvalidDataException("invalid-field");
        if(seen.Count!=names.Count)throw new InvalidDataException("missing-field");
    }
    static bool RefusedBeforeDispatch(InvalidOperationException ex){
        switch(ex.Message){case "permission-required":case "mouse-disabled":case "typing-disabled":case "control-required":case "session-mismatch":case "stale-frame":case "geometry-changed":case "geometry-version-mismatch":case "coordinates-out-of-range":case "invalid-action":case "invalid-scroll":case "input-rate-limit":case "protected-window":case "desktop-unavailable":case "dpi-unavailable":case "monitor-unavailable":case "local-input-active":case "foreground-required":case "foreground-monitor-mismatch":case "modifiers-held":case "keyboard-context-changed":case "invalid-keyboard-operation":case "key-not-allowed":case "text-size-limit":case "invalid-text":return true;default:return false;}
    }
    static void InputFailure(Stream stream,string requestId,Guid lease,Exception ex)
    {
        bool refused=ex is InvalidOperationException invalid&&RefusedBeforeDispatch(invalid);
        var failure=ex as ScreenInputFailure;
        string code=refused?ex.Message:failure?.Message=="input-not-applied"?"input-not-applied":"outcome-unknown";
        bool? dispatched=refused?false:failure?.Dispatched;
        Write(stream,new{version=1,request_id=requestId,session_id=lease.ToString("D"),error=code,dispatched,channel_terminated=false});stream.Flush();
    }
    internal bool ProcessOne(Stream stream,string connection)
    {
        using var document=JsonDocument.Parse(Read(stream,Length(stream,4096)),new JsonDocumentOptions{MaxDepth=4});var request=document.RootElement;
        string requestId=Id(request,"request_id").ToString("D");
        if(request.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("unsupported-version");
        string operation=Text(request,"operation");
        if(operation=="capabilities")
        {
            Fields(request,"version","request_id","operation");
            var monitors=new List<object>();foreach(var monitor in access.Monitors())monitors.Add(new{monitor_id=monitor.MonitorId,geometry_version=monitor.Version,x=monitor.X,y=monitor.Y,width=monitor.Width,height=monitor.Height});
            Write(stream,new{version=1,request_id=requestId,monitors,keyboard_control=true,text_max_utf16=128,keys=ScreenKeyboard.Keys,auto_accept=access.AutoAcceptLocal,view_requires_local_consent=!access.AutoAcceptLocal,control_requires_separate_local_consent=!access.AutoAcceptLocal,control_allowed=access.ControlAllowed});return true;
        }
        Guid lease;
        if(operation=="request")
        {
            Fields(request,"version","request_id","operation","monitor_id","control","operator_label");
            string label=Text(request,"operator_label");foreach(char c in label)if(char.IsControl(c))throw new InvalidDataException("invalid-operator-label");
            lease=access.Request(connection,Text(request,"monitor_id"),request.GetProperty("control").GetBoolean());
            // Handler displays native controls and applies the local owner grant policy.
            ConsentRequested?.Invoke(access.Snapshot,label);
        }
        else
        {
            lease=Id(request,"session_id");var state=access.Snapshot;
            if(state.SessionId!=lease||state.ConnectionId!=connection)throw new InvalidOperationException("session-mismatch");
            if(operation=="status"||operation=="close")Fields(request,"version","request_id","operation","session_id");
            else if(operation=="settings.read"||operation=="settings.set")
            {
                if(operation=="settings.read")Fields(request,"version","request_id","operation","session_id");else Fields(request,"version","request_id","operation","session_id","name","value");
                object result=null;string error="";
                try{access.RequireControl(connection,lease);if(SettingsHandler==null)throw new InvalidOperationException("settings-unavailable");result=SettingsHandler(operation,request,()=>{access.RequireControl(connection,lease);return true;});}
                catch(Exception ex){error=ex is InvalidOperationException&&(ex.Message=="settings-disabled"||ex.Message=="settings-forbidden"||ex.Message=="permission-required"||ex.Message=="control-required"||ex.Message=="settings-busy")?ex.Message:"settings-unavailable";}
                var current=access.Snapshot;Write(stream,new{version=1,request_id=requestId,session_id=lease.ToString("D"),state=current.State.ToString(),can_view=current.CanView,can_control=current.CanControl,remaining_ms=current.RemainingMs,settings=result,settings_error=error});stream.Flush();return true;
            }
            else if(operation=="capture")
            {
                Fields(request,"version","request_id","operation","session_id");ScreenCaptureResult result;
                try{result=access.Capture(connection,lease);}
                catch(InvalidOperationException ex){if(ex.Message!="capture-rate-limit")throw;Write(stream,new{version=1,request_id=requestId,session_id=lease.ToString("D"),error=ex.Message,dispatched=false});return true;}
                var frame=result.Frame;
                Write(stream,new{version=1,request_id=requestId,session_id=lease.ToString("D"),frame_id=frame.FrameId.ToString("D"),monitor_id=frame.Geometry.MonitorId,geometry_version=frame.Geometry.Version,width=frame.Width,height=frame.Height,captured_utc=result.CapturedUtc.ToString("o"),mime_type="image/png"});
                WriteBytes(stream,result.Image.Png);stream.Flush();return true;
            }
            else if(operation=="text"||operation=="key") {
                Fields(request,"version","request_id","operation","session_id","frame_id","geometry_version",operation);
                int geometryVersion=request.GetProperty("geometry_version").GetInt32();if(geometryVersion<1)throw new InvalidDataException("invalid-geometry-version");
                string value=request.GetProperty(operation).GetString();if(value==null)throw new InvalidDataException("invalid-field");
                try{access.Keyboard(connection,lease,Id(request,"frame_id"),geometryVersion,operation=="text"?value:null,operation=="key"?value:null);}
                catch(Exception ex){InputFailure(stream,requestId,lease,ex);return true;}
            }
            else if(operation=="mouse")
            {
                Fields(request,"version","request_id","operation","session_id","frame_id","geometry_version","action","x","y","scroll_steps");
                int geometryVersion=request.GetProperty("geometry_version").GetInt32();if(geometryVersion<1)throw new InvalidDataException("invalid-geometry-version");
                try{access.Mouse(connection,lease,Id(request,"frame_id"),Text(request,"action"),request.GetProperty("x").GetInt32(),request.GetProperty("y").GetInt32(),request.GetProperty("scroll_steps").GetInt32(),geometryVersion);}
                catch(Exception ex){InputFailure(stream,requestId,lease,ex);return true;}
            }
            else throw new InvalidDataException("invalid-operation");
        }
        if(operation=="close")access.Disconnect(connection);
        var snapshot=access.Snapshot;
        Write(stream,new{version=1,request_id=requestId,session_id=lease.ToString("D"),state=snapshot.State.ToString(),can_view=snapshot.CanView,can_control=snapshot.CanControl,remaining_ms=snapshot.RemainingMs});stream.Flush();return operation!="close";
    }
}
