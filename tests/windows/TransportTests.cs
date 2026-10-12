using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
internal static class TransportTests {
 static void Check(bool v, string m) { if (!v) throw new Exception(m); }
 public static void Run() {
  var s = new SupportSnapshot(1, SupportState.Stopped, false, false, "Stopped");
  Check(s.ColorName=="Red","Stopped red");
  Check(new SupportSnapshot(1,SupportState.Failed,true,false,"Relay unavailable").ColorName=="Yellow","LAN yellow");
  Check(new SupportSnapshot(1,SupportState.RelayWaiting,true,false,"Ready").ColorName=="Green","WAIT green");
  Check(new SupportSnapshot(1,SupportState.SessionActive,false,true,"Busy").Description.Contains("Busy"),"busy text");
  Check(new SupportSnapshot(1,SupportState.SessionActive,true,false,"LAN active; relay waiting",true).ColorName=="Green","registered relay stays green during LAN session");
  Check(new SupportSnapshot(1,SupportState.Stopped,true,false,"Stopped",true).ColorName=="Red","stopped overrides transport flags");
  Check(new SupportSnapshot(1,SupportState.Connecting,false,false,"Connecting").ColorName=="Red","connecting is not ready");
  var key=RemsupProtocol.GenerateKey(); Check(RemsupProtocol.ValidKey(key),"key");
  Check(!RemsupProtocol.ValidKey(new string('x',30)),"bad key");
  var result=RemsupProtocol.ParseResult("REMSUP1 RESULT 0 0 1 aGVsbG8= -");
  Check(result.Stdout=="hello" && result.Stderr=="","parse");
  bool bad=false; try { RemsupProtocol.ValidateCredentials("123456789","bad\nheader"); } catch(ArgumentException) { bad=true; } Check(bad,"header injection");
  using(var host=new SupportHost()) {
   bool invalid=false;try{host.Start("invalid:port","123456789","test-pass",null,0);}catch(ArgumentException){invalid=true;}Check(invalid&&host.Snapshot.ColorName=="Red","invalid endpoint has no side effects");
   host.Start("","123456789","test-pass",null,0);
   Check(host.Snapshot.ColorName=="Red","no transport red"); host.Stop();
   var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
   int port=((IPEndPoint)listener.LocalEndpoint).Port;
   var peer=new Thread(()=>{ using(var client=listener.AcceptTcpClient()){ var stream=client.GetStream(); RemsupProtocol.ReadLine(stream,512); RemsupProtocol.WriteLine(stream,"WAIT"); Thread.Sleep(200); } }); peer.IsBackground=true; peer.Start();
   host.StartFixture("127.0.0.1:"+port,"123456789","test-pass");
   Check(SpinWait.SpinUntil(()=>host.Snapshot.State==SupportState.RelayWaiting,4000),"WAIT observed");Check(host.Snapshot.RelayReady,"handshake records actual relay readiness");
   Check(SpinWait.SpinUntil(()=>host.Snapshot.State==SupportState.Reconnecting,4000),"EOF clears green");Check(!host.Snapshot.RelayReady,"EOF clears actual relay readiness");
   host.Stop(); Check(host.Snapshot.State==SupportState.Stopped,"stopped"); listener.Stop(); peer.Join(2000);
  }
  var gate=new SessionGate(); Check(gate.TryEnter(),"gate enter"); Check(!gate.TryEnter(),"gate busy"); gate.Exit();
  Console.WriteLine("PASS transport/readiness");
 }
}
