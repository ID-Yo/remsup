using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
internal static class LanSupportTests {
 static void Check(bool b,string m){if(!b)throw new Exception(m);}
 public static void Run(){
  if(Environment.GetEnvironmentVariable("REMSUP_TEST_LAN_FIREWALL")!="1"){Console.WriteLine("SKIP LAN integration: use -AllowLanFirewall in an authorized Private network");return;}
  Check(!SupportHost.PrivateAddress(IPAddress.Loopback),"loopback not advertised");
  Check(!SupportHost.PrivateAddress(IPAddress.Parse("8.8.8.8")),"public not advertised");
  var ip=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address).Where(SupportHost.PrivateAddress).FirstOrDefault(LanAccessPolicy.IsPrivateProfile);
  if(ip==null)throw new Exception("LAN test requires an active private IPv4 interface.");
  int port;var probe=new TcpListener(ip,0);probe.Start();port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
  LanAccessPolicy.AddRule(ip,port);
  try { using(var host=new SupportHost()){
   host.Start("","123456789","test-pass",ip,port);Check(host.Snapshot.ColorName=="Yellow","actual LAN ready");
   var r=OperatorConnection.Execute(ip.ToString(),port,"123456789","test-pass","echo lan-ok",3,1024,CancellationToken.None,null,LanTls.OwnPin,true);
   Check(r.ExitCode==0&&r.Stdout.Contains("lan-ok"),"LAN exec");
   bool denied=false;try{OperatorConnection.Execute(ip.ToString(),port,"123456789","wrong","echo must-not-execute",3,1024,CancellationToken.None,null,LanTls.OwnPin,true);}catch(IOException){denied=true;}Check(denied,"wrong password");
   using(var first=new TcpClient()){
    first.Connect(ip,port);first.ReceiveTimeout=2000;var stream=RelayTls.OpenStream(first,ip.ToString(),LanTls.OwnPin,CancellationToken.None);
    RemsupProtocol.WriteLine(stream,"OPERATOR 123456789 test-pass "+RemsupProtocol.GenerateKey());Check(RemsupProtocol.ReadLine(stream,512)=="OK","first handshake");
    using(var second=new TcpClient()){second.Connect(ip,port);second.ReceiveTimeout=2000;var s=RelayTls.OpenStream(second,ip.ToString(),LanTls.OwnPin,CancellationToken.None);RemsupProtocol.WriteLine(s,"OPERATOR 123456789 test-pass "+RemsupProtocol.GenerateKey());Check(RemsupProtocol.ReadLine(s,512)=="ERR busy","second handshake busy before OK");}
   }
   host.Stop();Check(host.Snapshot.ColorName=="Red","LAN stop red");
   Check(SpinWait.SpinUntil(()=>{try{host.Start("","123456789","rotated-pass",ip,port);return true;}catch(InvalidOperationException){return false;}},8000),"previous handshake releases gate before restart");
   denied=false;try{OperatorConnection.Execute(ip.ToString(),port,"123456789","test-pass","echo old-secret",3,1024,CancellationToken.None,null,LanTls.OwnPin,true);}catch(IOException){denied=true;}Check(denied,"old password invalid after restart");
   using(var slow=new TcpClient()){
    slow.Connect(ip,port);slow.ReceiveTimeout=2000;var stream=RelayTls.OpenStream(slow,ip.ToString(),LanTls.OwnPin,CancellationToken.None);RemsupProtocol.WriteLine(stream,"OPERATOR 123456789 rotated-pass "+RemsupProtocol.GenerateKey());Check(RemsupProtocol.ReadLine(stream,512)=="OK","restart handshake");
    RemsupProtocol.WriteLine(stream,"REMSUP1 EXEC 60 1024 "+RemsupProtocol.Encode("ping -n 55 127.0.0.1 > nul"));Check(SpinWait.SpinUntil(()=>host.Snapshot.State==SupportState.SessionActive,2000),"long command active");host.Stop();
    bool restarted=false;Check(SpinWait.SpinUntil(()=>{try{host.Start("","123456789","new-pass",ip,port);restarted=true;return true;}catch(InvalidOperationException){return false;}},8000)&&restarted,"Stop cancels task-owned command and permits restart");
   }
   host.Stop();
  } } finally { LanAccessPolicy.RemoveRule(ip,port); }
  var bounded=CommandExecutor.Execute("for /L %i in (1,1,2000) do @echo abcdefghijklmnop",3,1024);
  Check(bounded.Truncated&&System.Text.Encoding.UTF8.GetByteCount(bounded.Stdout+bounded.Stderr)<=1024,"bounded output");
  Console.WriteLine("PASS private-interface LAN/auth/busy/output");
 }
}
