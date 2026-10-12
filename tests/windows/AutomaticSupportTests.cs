using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
internal static class AutomaticSupportTests {
 public static void Run(){
  var settings=new RemsupSettings();var panel=new RemsupSupportPanel(()=>settings,()=>new SupportStatus{DeviceId="111222333"});bool applied=false;panel.SettingsApplied+=()=>applied=true;
  var root=Path.Combine(Path.GetTempPath(),"RemsupAuto-"+Guid.NewGuid().ToString("N"));
  try{using(var desktop=new DesktopForm(new DeviceStore(Path.Combine(root,"devices.json")),panel)){
   desktop.Show();Pump(()=>applied&&panel.Snapshot.State==SupportState.Failed,"automatic launch completes with observed Not configured");int first=panel.Snapshot.Generation;desktop.Hide();desktop.Show();Application.DoEvents();Check(panel.Snapshot.Generation==first,"shown again does not start twice");
   Check(Find(desktop,"Start support")==null&&Find(desktop,"Stop support")==null,"no start/stop GUI buttons");
   var tray=(NotifyIcon)typeof(DesktopForm).GetField("tray",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(desktop);Check(tray.ContextMenu.MenuItems.Count==3,"tray Show/Settings/Exit only");
   var generation=panel.Snapshot.Generation;settings=new RemsupSettings{RelayHost="bad host"};try{panel.StartSupportAsync().GetAwaiter().GetResult();throw new Exception("Expected invalid config");}catch(ArgumentException){}Check(panel.Snapshot.Generation==generation,"invalid reconfigure preserves host");
   desktop.Close();Check(!desktop.Visible&&panel.Snapshot.State==SupportState.Stopped,"X closes Desktop and stops host");
  }
  using(var portable=new RemsupClientForm(true,new RemsupSupportPanel(()=>new RemsupSettings(),()=>new SupportStatus{DeviceId="111222333"}))){portable.Show();Application.DoEvents();portable.Close();Check(!portable.Visible,"portable X exits during startup");}
  CheckLiveLifecycle(root);CheckOutboundClose(root);CheckListenerClose();
  Console.WriteLine("PASS automatic support, successful/failed Settings reconfigure, waiting/reconnecting Close and outbound cancellation");
  }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
 }
 static void CheckLiveLifecycle(string root){
  foreach(bool portable in new[]{false,true}){
   var first=new TcpListener(IPAddress.Loopback,0);var next=new TcpListener(IPAddress.Loopback,0);first.Start();next.Start();
   TcpClient peer=null,reconfigured=null;
   var current=new RemsupSettings{RelayHost="127.0.0.1",RelayPort=((IPEndPoint)first.LocalEndpoint).Port,RelayPin=RelayTls.DefaultPin};
   var panel=new RemsupSupportPanel(()=>current,()=>new SupportStatus{DeviceId="111222333"},true);
   using(Form form=portable?(Form)new RemsupClientForm(true,panel):new DesktopForm(new DeviceStore(Path.Combine(root,"live.json")),panel)){
    try{
     var accept=first.AcceptTcpClientAsync();form.Show();Pump(()=>accept.IsCompleted,"automatic launch connects loopback");peer=accept.Result;
     Register(peer);Pump(()=>panel.Snapshot.State==SupportState.RelayWaiting,"automatic relay reaches WAIT");
     int generation=panel.Snapshot.Generation;form.Hide();form.Show();Application.DoEvents();Check(panel.Snapshot.Generation==generation,"restore does not restart waiting host");
     current=new RemsupSettings{RelayHost="bad host"};var invalid=panel.ApplySettingsAsync();Pump(()=>invalid.IsCompleted,"invalid reconfigure observed");
     Check(panel.Snapshot.Generation==generation&&panel.Snapshot.State==SupportState.RelayWaiting,"invalid settings preserve working host");
     var saved=new RemsupSettings{RelayHost="127.0.0.1",RelayPort=((IPEndPoint)next.LocalEndpoint).Port,RelayPin=RelayTls.DefaultPin};var secondAccept=next.AcceptTcpClientAsync();Task applied=null;
     using(var dialog=new RemsupSettingsForm(saved,value=>current=value)){
      dialog.SettingsSaved+=()=>applied=panel.ApplySettingsAsync();
      typeof(RemsupSettingsForm).GetMethod("SaveChanges",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,new object[]{saved,false});
      Check(applied!=null,"successful Save triggers reconfigure");Pump(()=>applied.IsCompleted&&secondAccept.IsCompleted,"successful Save applies new host/port");
     }
     Check(SocketClosed(peer),"reconfigure closes previous WAIT connection");reconfigured=secondAccept.Result;Register(reconfigured);
     Pump(()=>panel.Snapshot.State==SupportState.RelayWaiting,"new configured relay reaches WAIT");
     Check(panel.Snapshot.Generation>generation,"Settings restarts host generation");
     if(portable){reconfigured.Close();Pump(()=>panel.Snapshot.State==SupportState.Reconnecting,"remote-only reconnect observed");}
     if(portable)((RemsupClientForm)form).RequestClose();else ((NotifyIcon)typeof(DesktopForm).GetField("tray",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form)).ContextMenu.MenuItems[2].PerformClick();
     Check(!form.Visible&&panel.Snapshot.State==SupportState.Stopped&&panel.CurrentPassword=="","Exit stops host and clears displayed credential");
     if(!portable)Check(SocketClosed(reconfigured),"Exit closes waiting socket");
     var watch=System.Diagnostics.Stopwatch.StartNew();while(watch.ElapsedMilliseconds<1200){Application.DoEvents();Thread.Sleep(10);}
     Check(!first.Pending()&&!next.Pending(),"Exit cancels reconnect loop");
    }finally{if(peer!=null)peer.Close();if(reconfigured!=null)reconfigured.Close();first.Stop();next.Stop();}
   }
  }
 }
 static void Register(TcpClient peer){peer.ReceiveTimeout=3000;var stream=peer.GetStream();Check(RemsupProtocol.ReadLine(stream,512).StartsWith("AGENT "),"synthetic relay registration");RemsupProtocol.WriteLine(stream,"WAIT");}
 static bool SocketClosed(TcpClient peer){return SpinWait.SpinUntil(()=>peer.Client.Poll(1000,SelectMode.SelectRead)&&peer.Client.Available==0,3000);}
 static void CheckOutboundClose(string root){
  var store=new DeviceStore(Path.Combine(root,"outbound.json"));var data=store.Load();data.Upsert(new DeviceRecord{DisplayName="Synthetic",DeviceId="111222333",Host="relay.example.test",Pin=RelayTls.DefaultPin,SavePermanentPassword=true,PermanentPassword="synthetic-only"});store.Save(data);
  bool entered=false;CancellationToken observed=CancellationToken.None;
  using(var form=new DesktopForm(store,new RemsupSupportPanel(()=>new RemsupSettings(),()=>new SupportStatus{DeviceId="222333444"}),()=>new RelayConfiguration("relay.example.test",8443,RelayTls.DefaultPin),false,(endpoint,device,password,text,seconds,bytes,cancel)=>{observed=cancel;entered=true;cancel.WaitHandle.WaitOne(5000);cancel.ThrowIfCancellationRequested();return new CommandResult();},_=>true)){
   form.Show();var connect=(Task)typeof(DesktopForm).GetMethod("Connect",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{data.Records[0]});
   Pump(()=>entered,"outbound command entered");form.Close();Pump(()=>connect.IsCompleted,"Close releases outbound command");connect.GetAwaiter().GetResult();Check(observed.IsCancellationRequested,"Close cancels outgoing operation");
  }
 }
 static void CheckListenerClose(){
  // Inject only a synthetic loopback listener: no Private-profile or firewall changes.
  using(var host=new SupportHost()){
   var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
   typeof(SupportHost).GetField("listener",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(host,listener);host.Stop();
   using(var probe=new TcpListenerScope(port)){}Check(host.Snapshot.State==SupportState.Stopped,"Close releases listener");
  }
 }
 sealed class TcpListenerScope:IDisposable{readonly TcpListener value;internal TcpListenerScope(int port){value=new TcpListener(IPAddress.Loopback,port);value.Start();}public void Dispose(){value.Stop();}}
 static void Pump(Func<bool> done,string message){var watch=System.Diagnostics.Stopwatch.StartNew();while(!done()&&watch.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(10);}Check(done(),message);}
 static Button Find(Control root,string text){var b=root as Button;if(b!=null&&b.Text==text)return b;foreach(Control c in root.Controls){var found=Find(c,text);if(found!=null)return found;}return null;}
 static void Check(bool ok,string message){if(!ok)throw new Exception("AutomaticSupportTests: "+message);}
}
