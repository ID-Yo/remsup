using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

internal sealed class ScreenWorkspace:IDisposable
{
    readonly Dispatcher dispatcher=Dispatcher.CurrentDispatcher;readonly bool fixture;
    readonly Dictionary<string,ScreenPeer> peers=new Dictionary<string,ScreenPeer>();readonly Dictionary<string,object> states=new Dictionary<string,object>();
    ScreenAccess receiver;ScreenConsentWindow consent;IScreenSurface surface;Stream incoming;volatile bool enabled,disposed;bool controlAllowed=true;string operatorLabel="";
    internal event Action Changed;
    internal Func<string,JsonElement,Func<bool>,object> SettingsHandler;
    internal ScreenWorkspace(bool synthetic){fixture=synthetic;Enable(true);}
    internal object Receiving=>new{enabled,state=receiver?.Snapshot.State.ToString()??"Off",operatorLabel,canView=receiver?.Snapshot.CanView??false,canControl=receiver?.Snapshot.CanControl??false,warning="Relay and direct LAN connections use pinned TLS."};
    internal object Snapshot(string id)=>states.TryGetValue(id,out var state)?state:new{state="Off",canView=false,canControl=false,operatorLabel="",preview=(string)null,width=0,height=0,frameId="",geometryVersion=0,error=""};
    internal void Enable(bool value,bool allowControl=true,bool autoAccept=true)
    {
        if(!value){StopReceiver();foreach(var key in new List<string>(peers.Keys))Close(key);}enabled=value;controlAllowed=allowControl;
        if(value&&receiver==null){surface=fixture?new SyntheticScreenSurface():new WindowsScreenSurface();var watch=Stopwatch.StartNew();receiver=new ScreenAccess(surface,()=>watch.ElapsedMilliseconds);if(surface is SyntheticScreenSurface synthetic){synthetic.CurrentLease=()=>receiver.Snapshot.SessionId;synthetic.CanInput=()=>receiver.Snapshot.CanControl;}}
        receiver?.SetPolicy(value,allowControl);if(receiver!=null)receiver.AutoAcceptLocal=autoAccept;
        foreach(var peer in peers)Update(peer.Key,peer.Value);
        Changed?.Invoke();
    }
    internal void Serve(Stream stream,CancellationToken token)
    {
        if(!enabled||disposed){RemsupProtocol.WriteLine(stream,"REMSUPSCREEN1 ERROR screen-disabled");return;}
        incoming=stream;RemsupProtocol.WriteLine(stream,"REMSUPSCREEN1 OK");var channel=new ScreenChannel(receiver);channel.SettingsHandler=SettingsHandler;
        channel.ConsentRequested+=(snapshot,label)=>dispatcher.BeginInvoke(new Action(()=>{if(disposed||!enabled||receiver.Snapshot.SessionId!=snapshot.SessionId)return;operatorLabel=label;if(consent==null){consent=new ScreenConsentWindow(receiver);consent.Revoked+=StopReceiver;consent.Changed+=()=>Changed?.Invoke();consent.Closed+=(_,__)=>consent=null;}consent.OperatorLabel=label;consent.ShowCurrentRequest();try{receiver.GrantConfigured(snapshot.SessionId,snapshot.ConnectionId);}catch(InvalidOperationException){}consent.Refresh();Changed?.Invoke();}));
        try{channel.Run(stream,Guid.NewGuid().ToString("D"),token);}
        finally{if(incoming==stream){incoming=null;dispatcher.BeginInvoke(new Action(()=>Changed?.Invoke()));}}
    }
    internal void StopReceiver(){receiver?.Stop();try{incoming?.Close();}catch(ObjectDisposedException){}incoming=null;}
    internal void ApproveFixture(bool control){if(!fixture||consent==null)throw new InvalidOperationException("Synthetic local consent window required.");consent.LocalApproveForFixture(control);}
    internal void CaptureConsentFixture(string path){if(!fixture||consent==null)throw new InvalidOperationException("Synthetic consent window required.");consent.CaptureOwnVisual(path);}
    internal async Task Request(string key,DeviceEndpoint endpoint,string id,string password,string monitor,bool control)
    {
        if(!enabled)throw new InvalidOperationException("Screen viewing is disabled in Settings.");Close(key);if(peers.Count>=4)throw new InvalidOperationException("At most four screen sessions.");
        if(control&&!controlAllowed)throw new InvalidOperationException("Screen control is disabled in Settings.");
        var peer=new ScreenPeer();peers[key]=peer;states[key]=new{state="Connecting",canView=false,canControl=false,operatorLabel="",preview=(string)null,width=0,height=0,frameId="",geometryVersion=0,error=""};Changed?.Invoke();
        await peer.Gate.WaitAsync();
        if(!enabled||control&&!controlAllowed){peer.Gate.Release();Close(key);throw new InvalidOperationException("Screen permission was disabled in Settings.");}
        try
        {
            if(fixture)
            {
                // Same-process fixture uses the identical wire channel over a
                // task-owned loopback socket, never a relay or another desktop.
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
                _=Task.Run(()=>{try{using var accepted=listener.AcceptTcpClient();accepted.ReceiveTimeout=10000;var stream=accepted.GetStream();RemsupProtocol.ReadLine(stream,512);RemsupProtocol.WriteLine(stream,"OK");if(RemsupProtocol.ReadLine(stream,512)=="REMSUPSCREEN1")Serve(stream,CancellationToken.None);}catch(System.IO.IOException){}catch(ObjectDisposedException){}finally{listener.Stop();}});
                await Task.Run(()=>peer.ConnectFixture("127.0.0.1",port,id,password,CancellationToken.None));
            }
            else await Task.Run(()=>peer.Connect(endpoint.Host,endpoint.Port,id,password,CancellationToken.None,endpoint.Pin,endpoint.IsLan));
            var capabilities=await Task.Run(()=>peer.Call("capabilities"));string selected=monitor;
            if(string.IsNullOrEmpty(selected))selected=capabilities.GetProperty("monitors")[0].GetProperty("monitor_id").GetString();
            await Task.Run(()=>peer.Call("request",new{monitor_id=selected,control,operator_label=fixture?"Local synthetic test operator":Environment.UserName}));Update(key,peer);
        }
        catch(Exception ex){Failed(key,peer,ex);throw;}
        finally{peer.Gate.Release();}
    }
    internal async Task Operate(string key,string operation,string action="click",int x=0,int y=0,int scroll=0,string frameId="",int geometryVersion=-1,string input=null)
    {
        if(!enabled)throw new InvalidOperationException("Screen viewing is disabled in Settings.");
        bool isInput=operation=="mouse"||operation=="text"||operation=="key";
        if(isInput&&!controlAllowed)throw new InvalidOperationException("Screen control is disabled in Settings.");
        if(!peers.TryGetValue(key,out var peer))throw new InvalidOperationException("Screen connection is unavailable.");
        await peer.Gate.WaitAsync();
        if(!enabled||isInput&&!controlAllowed){peer.Gate.Release();throw new InvalidOperationException("Screen permission was disabled in Settings.");}
        try{if(isInput&&(peer.Png==null||frameId!=peer.Frame.GetProperty("frame_id").GetString()||geometryVersion!=peer.Frame.GetProperty("geometry_version").GetInt32()))throw new InvalidOperationException("displayed-frame-stale");if(operation=="text"||operation=="key")ScreenKeyboard.Validate(operation=="text"?input:null,operation=="key"?input:null);await Task.Run(()=>operation=="mouse"?peer.Call(operation,new{frame_id=frameId,geometry_version=geometryVersion,action,x,y,scroll_steps=scroll}):operation=="text"?peer.Call(operation,new{frame_id=frameId,geometry_version=geometryVersion,text=input}):operation=="key"?peer.Call(operation,new{frame_id=frameId,geometry_version=geometryVersion,key=input}):peer.Call(operation));Update(key,peer);}
        catch(ScreenRefusalException ex){Update(key,peer,ex.Message);throw;}
        catch(Exception ex){Failed(key,peer,ex);throw new InvalidOperationException(isInput?"Input outcome unknown. Do not retry; request a new image.":"Screen stopped or refused: "+ex.Message,ex);}
        finally{peer.Gate.Release();}
    }
    void Update(string key,ScreenPeer peer,string error=""){states[key]=new{state=peer.State,canView=peer.View,canControl=peer.Control&&controlAllowed,operatorLabel=fixture?"Local synthetic test operator":Environment.UserName,preview=peer.Png==null?null:"data:image/png;base64,"+Convert.ToBase64String(peer.Png),width=peer.Png==null?0:peer.Frame.GetProperty("width").GetInt32(),height=peer.Png==null?0:peer.Frame.GetProperty("height").GetInt32(),frameId=peer.Png==null?"":peer.Frame.GetProperty("frame_id").GetString(),geometryVersion=peer.Png==null?0:peer.Frame.GetProperty("geometry_version").GetInt32(),error};Changed?.Invoke();}
    void Failed(string key,ScreenPeer peer,Exception error){peer.Dispose();peers.Remove(key);states[key]=new{state="Unavailable",canView=false,canControl=false,operatorLabel="",preview=(string)null,width=0,height=0,frameId="",geometryVersion=0,error=error.Message};Changed?.Invoke();}
    internal void Close(string key){if(peers.Remove(key,out var peer))peer.Dispose();states.Remove(key);Changed?.Invoke();}
    public void Dispose(){if(disposed)return;disposed=true;enabled=false;StopReceiver();foreach(var peer in peers.Values)peer.Dispose();peers.Clear();states.Clear();if(consent!=null){consent.Revoked-=StopReceiver;consent.Close();}if(surface is IDisposable owner)owner.Dispose();}
}
