using System;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class ScreenChannelTests
{
    sealed class Synthetic:IScreenSurface
    {
        public bool Changed;public int Captures,Inputs;public Exception Failure;
        public bool IsInteractive=>true;
        public ScreenGeometry Geometry(string monitor)=>monitor=="synthetic"?new ScreenGeometry(monitor,1,-4,0,4,4):throw new InvalidOperationException("monitor-unavailable");
        public ScreenGeometry[] Monitors()=>new[]{Geometry("synthetic")};
        public ScreenImage Capture(ScreenGeometry geometry)
        {
            Captures++;byte[] pixels=new byte[4*4*3];for(int i=0;i<pixels.Length;i+=3)pixels[i+(Changed?1:0)]=255;
            var bitmap=BitmapSource.Create(4,4,96,96,PixelFormats.Rgb24,null,pixels,12);bitmap.Freeze();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output=new MemoryStream();encoder.Save(output);return new ScreenImage(output.ToArray(),4,4);
        }
        public bool IsProtectedPoint(ScreenPoint point)=>false;
        public string Typed="",Key="";
        public void Keyboard(string text,string key,ScreenGeometry geometry,Action authorize){authorize();Typed+=text;Key=key;}
        public int HeldButtons;
        public void Mouse(string action,ScreenPoint point,int steps,Action authorize){
            authorize();Inputs++;if(Failure!=null)throw Failure;
            if(action=="left-down"||action=="down")HeldButtons|=1;
            else if(action=="right-down")HeldButtons|=2;
            else if(action=="middle-down")HeldButtons|=4;
            else if(action=="left-up"||action=="up"){if((HeldButtons&1)==0)return;HeldButtons&=~1;}
            else if(action=="right-up"){if((HeldButtons&2)==0)return;HeldButtons&=~2;}
            else if(action=="middle-up"){if((HeldButtons&4)==0)return;HeldButtons&=~4;}
            Changed=true;
        }
        public int Releases; public bool PendingRelease; public bool HasPendingRelease=>PendingRelease; public bool FailRelease; public bool ReleaseHeldInputs(){ Releases++; if(!FailRelease){HeldButtons=0;PendingRelease=false;} return !FailRelease; }
    }
    sealed class Duplex:Stream
    {
        readonly MemoryStream incoming;internal readonly MemoryStream Output=new MemoryStream();
        internal Duplex(byte[] data){incoming=new MemoryStream(data);}
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] bytes,int offset,int count)=>incoming.Read(bytes,offset,count);
        public override void Write(byte[] bytes,int offset,int count)=>Output.Write(bytes,offset,count);
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing){incoming.Dispose();Output.Dispose();}base.Dispose(disposing);}
    }
    static byte[] Packet(object data){byte[] body=JsonSerializer.SerializeToUtf8Bytes(data);using var stream=new MemoryStream();int n=body.Length;stream.Write(new[]{(byte)(n>>24),(byte)(n>>16),(byte)(n>>8),(byte)n});stream.Write(body);return stream.ToArray();}
    static JsonElement Response(byte[] data){int length=(data[0]<<24)|(data[1]<<16)|(data[2]<<8)|data[3];return JsonDocument.Parse(data.AsMemory(4,length)).RootElement.Clone();}
    static byte[] Call(ScreenChannel channel,object request){using var stream=new Duplex(Packet(request));channel.ProcessOne(stream,"synthetic-connection");return stream.Output.ToArray();}
    static void Check(bool value,string name){if(!value)throw new Exception(name);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            long now=0;var surface=new Synthetic();var access=new ScreenAccess(surface,()=>now);var channel=new ScreenChannel(access);int prompts=0;channel.ConsentRequested+=(_,label)=>prompts++;
            var request=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="request",monitor_id="synthetic",control=true,operator_label="synthetic operator"}));Guid lease=Guid.Parse(request.GetProperty("session_id").GetString());
            Check(prompts==1&&request.GetProperty("state").GetString()=="Pending","request pending without grant");
            var consent=new ScreenConsentWindow(access);Check(consent.Title=="Remsup screen access"&&consent.Topmost&&consent.ShowInTaskbar,"native permission window contract (not shown)");consent.Close();
            // Closed permission window revokes the lease; request again.
            request=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="request",monitor_id="synthetic",control=true,operator_label="synthetic operator"}));lease=Guid.Parse(request.GetProperty("session_id").GetString());
            try{Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="capture",session_id=lease});throw new Exception("unapproved capture allowed");}catch(InvalidOperationException){}
            try{Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="cadence",session_id=lease,interval_ms=100});throw new Exception("cadence on pending session allowed");}catch(InvalidOperationException){}
            Check(surface.Captures==0,"unapproved provider not called");access.GrantLocal(lease,true);
            bool settingsEnabled=false;int writes=0;channel.SettingsHandler=(op,data,authorize)=>{authorize();if(!settingsEnabled)throw new InvalidOperationException("settings-disabled");if(op=="settings.set"){if(data.GetProperty("name").GetString()!="autoUpdates")throw new InvalidOperationException("settings-forbidden");writes++;}return new{autoUpdates=false};};
            Check(Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="settings.read",session_id=lease})).GetProperty("settings_error").GetString()=="settings-disabled"&&writes==0,"wire settings OFF denial");
            settingsEnabled=true;Check(Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="settings.set",session_id=lease,name="autoUpdates",value=false})).GetProperty("settings_error").GetString()==""&&writes==1,"wire granted settings set");
            Check(Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="settings.set",session_id=lease,name="allowRemoteSettings",value=true})).GetProperty("settings_error").GetString()=="settings-forbidden"&&writes==1,"wire permission cannot grant itself");
            settingsEnabled=false;Check(Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="settings.read",session_id=lease})).GetProperty("settings_error").GetString()=="settings-disabled","wire immediate revoke");
            byte[] capture=Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="capture",session_id=lease});var metadata=Response(capture);Guid frame=Guid.Parse(metadata.GetProperty("frame_id").GetString());
            Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=frame,geometry_version=1,action="click",x=1,y=1,scroll_steps=0});Check(surface.Inputs==1,"synthetic wire mouse sink");
            now=20;Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=frame,geometry_version=1,action="move",x=2,y=2,scroll_steps=0});Check(surface.Inputs==2,"wire move sink");
            now=70;Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=frame,geometry_version=1,action="down",x=2,y=2,scroll_steps=0});Check(surface.Inputs==3,"wire down sink");
            now=120;Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=frame,geometry_version=1,action="up",x=2,y=2,scroll_steps=0});Check(surface.Inputs==4,"wire up sink");
            now=350;Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="text",session_id=lease,frame_id=frame,geometry_version=1,text="Тест 😀"});Check(surface.Typed=="Тест 😀","wire Unicode text sink");
            now=550;Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="key",session_id=lease,frame_id=frame,geometry_version=1,key="ctrl+shift+s"});Check(surface.Key=="ctrl+shift+s","wire Save As key sink");
            var refusal=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="key",session_id=lease,frame_id=frame,geometry_version=1,key="win+r"}));Check(!refusal.GetProperty("dispatched").GetBoolean()&&surface.Key=="ctrl+shift+s","unsupported key confirmed refused");
            var captureRefusal=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="capture",session_id=lease}));
            Check(captureRefusal.GetProperty("error").GetString()=="capture-rate-limit"&&!captureRefusal.GetProperty("dispatched").GetBoolean()&&surface.Captures==1&&access.Snapshot.CanView,"early capture confirmed refused without provider or revocation");
            var status=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="status",session_id=lease}));Check(status.GetProperty("can_control").GetBoolean(),"channel still granted after capture refusal");
            now=750;surface.Failure=new Exception("PRIVATE_SENTINEL");
            var unknown=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=frame,geometry_version=1,action="click",x=1,y=1,scroll_steps=0}));
            Check(unknown.GetProperty("error").GetString()=="outcome-unknown"&&unknown.GetProperty("dispatched").ValueKind==JsonValueKind.Null&&!unknown.GetProperty("channel_terminated").GetBoolean()&&access.Snapshot.CanView,"unexpected input exception structured without false refusal or channel loss");surface.Failure=new ScreenInputFailure("input-not-applied",true);now=950;
            var held=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=frame,geometry_version=1,action="click",x=1,y=1,scroll_steps=0}));
            Check(held.GetProperty("error").GetString()=="input-not-applied"&&held.GetProperty("dispatched").GetBoolean()&&!held.GetProperty("channel_terminated").GetBoolean(),"held cursor failure preserves dispatch and channel");surface.Failure=null;
            now=1150;capture=Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="capture",session_id=lease});Check(surface.Captures==2&&surface.Changed,"wire capture -> click -> capture");
            try{Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="cadence",session_id=lease,interval_ms=30});throw new Exception("cadence < 50 allowed");}catch(InvalidDataException){}
            try{Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="cadence",session_id=lease,interval_ms=1500});throw new Exception("cadence > 1000 allowed");}catch(InvalidDataException){}
            var cad=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="cadence",session_id=lease,interval_ms=100}));
            Check(cad.GetProperty("ok").GetBoolean()&&cad.GetProperty("interval_ms").GetInt32()==100,"wire cadence config failed");
            now=1250;var fastCap=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="capture",session_id=lease}));
            Check(fastCap.GetProperty("frame_id").GetString()!=null,"wire fast capture at 100ms interval failed");
            now=1270;Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=frame,geometry_version=1,action="left-down",x=1,y=1,scroll_steps=0});
            now=1275;Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=frame,geometry_version=1,action="left-up",x=1,y=1,scroll_steps=0});
            if(args.Length==1)File.WriteAllBytes(args[0],capture); // synthetic cross-language fixture only
            Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="close",session_id=lease});Check(!access.Snapshot.CanView,"wire close revokes");
            Check(surface.Releases>0,"central release invoked on session termination");
            // Negative test: HasPendingRelease blocks new session request and grant
            surface.PendingRelease=true;
            surface.FailRelease=true;
            try{access.Request("block-conn","synthetic",true);throw new Exception("request accepted with pending release");}catch(InvalidOperationException ex){Check(ex.Message=="pending-input-release","pending release did not block new request");}
            surface.FailRelease=false;
            surface.ReleaseHeldInputs();

            // Stale-frame Up release test: down on frame, then newer capture advances latest frame, then Up with old frame succeeds
            lease=access.Request("synthetic-connection","synthetic",true);
            access.GrantLocal(lease,true);
            now+=1000;var staleCapture=access.Capture("synthetic-connection",lease);Guid oldFrameId=staleCapture.Frame.FrameId;
            var downResp=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=oldFrameId,geometry_version=1,action="left-down",x=1,y=1,scroll_steps=0}));
            Check(downResp.GetProperty("state").GetString()=="Granted","down on fresh frame failed");
            now+=1000;var newerCapture=access.Capture("synthetic-connection",lease);
            now+=10;var upResp=Response(Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=oldFrameId,geometry_version=1,action="left-up",x=1,y=1,scroll_steps=0}));
            Check(upResp.GetProperty("state").GetString()=="Granted"&&(!upResp.TryGetProperty("error",out var upErr)||string.IsNullOrEmpty(upErr.GetString())),"up on old frame was rejected");
            Check(surface.HeldButtons==0,"held button not cleared after valid left-up");

            // Test unowned Up: right-up without right-down does not release unowned button
            Guid freshFrameId = newerCapture.Frame.FrameId;
            now+=60;
            Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=freshFrameId,geometry_version=1,action="left-down",x=1,y=1,scroll_steps=0});
            Check(surface.HeldButtons==1,"left-down did not set held button");
            Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=freshFrameId,geometry_version=1,action="right-up",x=1,y=1,scroll_steps=0});
            Check(surface.HeldButtons==1,"unowned right-up cleared left button");
            Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="mouse",session_id=lease,frame_id=freshFrameId,geometry_version=1,action="left-up",x=1,y=1,scroll_steps=0});
            Check(surface.HeldButtons==0,"left-up did not clear owned button");

            // Local mouse revocation invokes ReleaseHeldInputs
            surface.Releases=0;
            access.SetLocalInput(false,true);
            Check(surface.Releases==1,"local mouse revocation invoked ReleaseHeldInputs");
            access.SetLocalInput(true,true);
            access.Stop();

            // Automatic failed release retry on RefreshEnvironment when session is revoked/off
            surface.Releases=0;
            surface.PendingRelease=true;
            access.RefreshEnvironment();
            Check(surface.Releases==1&&!surface.HasPendingRelease,"RefreshEnvironment did not service pending release on inactive session");
            try{Call(channel,new{version=1,request_id=Guid.NewGuid(),operation="request",monitor_id="synthetic",control=false,approve=true});throw new Exception("remote approval field accepted");}catch(InvalidDataException){}
            access.SetPolicy(false,true);try{access.Request("policy","synthetic",true);throw new Exception("disabled view accepted");}catch(InvalidOperationException ex){Check(ex.Message=="screen-disabled","disabled view refuses new requests");}
            access.SetPolicy(true,false);lease=access.Request("policy","synthetic",true);Check(!access.Snapshot.RequestedControl,"global control disabled removes control request");access.GrantLocal(lease,false);Check(access.Snapshot.CanView&&!access.Snapshot.CanControl,"view survives control disabled");access.Stop();
            access.SetPolicy(true,true);lease=access.Request("policy","synthetic",true);access.GrantLocal(lease,true,true);Check(access.Snapshot.AutoAccepted,"auto accepted grant marked");now+=1000;var policyFrame=access.Capture("policy",lease).Frame;
            access.SetLocalInput(false,false);try{access.Mouse("policy",lease,policyFrame.FrameId,"click",1,1,0,1);throw new Exception("mouse toggle ignored");}catch(InvalidOperationException ex){Check(ex.Message=="mouse-disabled","local mouse toggle enforced");}
            try{access.Keyboard("policy",lease,policyFrame.FrameId,1,"test",null);throw new Exception("typing toggle ignored");}catch(InvalidOperationException ex){Check(ex.Message=="typing-disabled","local typing toggle enforced");}
            access.SetLocalInput(true,true);access.SetPolicy(true,false);Check(access.Snapshot.CanView&&!access.Snapshot.CanControl,"global control disable downgrades active grant");
            try{access.Mouse("policy",lease,policyFrame.FrameId,"click",1,1,0,1);throw new Exception("disabled control dispatched");}catch(InvalidOperationException ex){Check(ex.Message=="control-required","global mouse refusal");}
            try{access.Keyboard("policy",lease,policyFrame.FrameId,1,"test",null);throw new Exception("disabled typing dispatched");}catch(InvalidOperationException ex){Check(ex.Message=="control-required","global typing refusal");}
            access.SetPolicy(true,true);Check(!access.Snapshot.CanControl,"reenabling setting does not silently regrant control");access.Stop();lease=access.Request("policy","synthetic",true);
            var visible=new ScreenConsentWindow(access){OperatorLabel="Local synthetic operator"};visible.ShowCurrentRequest();Check(access.Snapshot.ElapsedMs==0,"pending timer inactive");Check(visible.IsVisible&&visible.WindowState==System.Windows.WindowState.Normal,"manual Pending remains visible and normal");visible.LocalApproveForFixture(true);Check(!access.Snapshot.AutoAccepted,"manual grant label");Check(visible.IsVisible&&visible.WindowState==System.Windows.WindowState.Minimized,"manual Granted automatically minimizes visible card");now+=65000;visible.Refresh();
            visible.WindowState=System.Windows.WindowState.Minimized;Check(access.Snapshot.CanView,"minimize retains grant");visible.ShowCurrentRequest();Check(access.Snapshot.ElapsedMs==65000,"restore preserves elapsed time");visible.Refresh();Check(visible.WindowState==System.Windows.WindowState.Normal&&access.Snapshot.CanControl,"restore and refresh preserve control without minimizing again");
            var elapsed=(System.Windows.Controls.TextBlock)typeof(ScreenConsentWindow).GetField("elapsed",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(visible);Check(elapsed.Text=="Connected 00:01:05","elapsed displayed from monotonic grant");
            var summary=(System.Windows.Controls.TextBlock)typeof(ScreenConsentWindow).GetField("summary",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(visible);Check(!summary.Text.Contains("remaining")&&!summary.Text.Contains("Expires"),"countdown absent from UI");
            if(args.Length==1)visible.CaptureOwnVisual(args[0]+"-consent.png");
            var disconnect=(System.Windows.Controls.Button)typeof(ScreenConsentWindow).GetField("stop",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(visible);
            visible.MaxHeight=Math.Min(320,System.Windows.SystemParameters.WorkArea.Height);var scroll=(System.Windows.Controls.ScrollViewer)visible.Content;((System.Windows.FrameworkElement)scroll.Content).LayoutTransform=new ScaleTransform(1.5,1.5);visible.UpdateLayout();scroll.ScrollToBottom();visible.UpdateLayout();
            Check(visible.ActualHeight<=visible.MaxHeight&&scroll.ExtentHeight>scroll.ViewportHeight,"small height / enlarged UI remains bounded with scrolling");
            Check(disconnect.Template.Triggers.Count==4,"custom buttons retain focus hover pressed disabled states");
            if(args.Length==1)visible.CaptureOwnVisual(args[0]+"-compact.png");
            disconnect.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Check(!access.Snapshot.CanView,"Disconnect revokes");
            lease=access.Request("policy","synthetic",false);visible.ShowCurrentRequest();visible.LocalApproveForFixture(false);Check(access.Snapshot.ElapsedMs==0,"new grant resets elapsed");visible.Close();Check(!access.Snapshot.CanView,"Close revokes");
            lease=access.Request("policy","synthetic",false);access.GrantLocal(lease,false);now+=600000;Check(!access.Snapshot.CanView,"expiry retained despite no countdown");
            lease=access.Request("policy","synthetic",true);access.AutoAcceptLocal=true;access.GrantConfigured(Guid.NewGuid(),"policy");Check(access.Snapshot.State==ScreenSessionState.Pending,"stale configured grant refused");access.GrantConfigured(lease,"foreign");Check(access.Snapshot.State==ScreenSessionState.Pending,"foreign configured grant refused");access.AutoAcceptLocal=false;access.GrantConfigured(lease,"policy");Check(access.Snapshot.State==ScreenSessionState.Pending,"autoaccept off needs manual consent");access.AutoAcceptLocal=true;access.SetPolicy(true,false);var automatic=new ScreenConsentWindow(access);automatic.ShowCurrentRequest();Check(automatic.IsVisible&&automatic.WindowState==System.Windows.WindowState.Normal,"autoaccept card first shown before grant");access.GrantConfigured(lease,"policy");automatic.Refresh();Check(automatic.IsVisible&&automatic.ShowInTaskbar&&automatic.WindowState==System.Windows.WindowState.Minimized,"configured Granted automatically minimizes registered visible card");automatic.ShowCurrentRequest();automatic.Refresh();Check(automatic.WindowState==System.Windows.WindowState.Normal&&access.Snapshot.CanView,"autoaccept restore retains grant");Check(access.Snapshot.AutoAccepted&&access.Snapshot.CanView&&!access.Snapshot.CanControl,"configured grant bounded by control policy");automatic.Close();Check(!access.Snapshot.CanView,"closing restored automatic card revokes grant");access.Stop();
            Console.WriteLine("PASS wire roundtrip, local owner policy grants, stale/manual/configured permissions, minimize/restore, elapsed timer, Disconnect/Close/expiry and own-window screenshot");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
