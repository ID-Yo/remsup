using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
internal static class OperatorConnectionTests {
 static void Check(bool v,string m){if(!v)throw new Exception(m);}
 public static void Run(){
  foreach(string handshake in new[]{"ERR no-agent","ERR busy","disconnect"}){
   var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
   var peer=new Thread(()=>{try{using(var c=listener.AcceptTcpClient()){c.ReceiveTimeout=2000;var s=c.GetStream();RemsupProtocol.ReadLine(s,512);if(handshake!="disconnect")RemsupProtocol.WriteLine(s,handshake);}}catch{}}){IsBackground=true};peer.Start();
   try{bool rejected=false;try{OperatorConnection.ExecuteFixture("127.0.0.1",port,"123456789","test-pass","echo must-not-run",1,1024,CancellationToken.None,null);}catch(IOException ex){rejected=!ex.Message.Contains("Result unknown");}Check(rejected,"handshake rejection is before command send");peer.Join(2000);}finally{listener.Stop();}
  }
  foreach(string response in new[]{"REMSUP1 RESULT 7 1 12 b3V0 ZXJy","REMSUP1 ERROR busy","broken-result","disconnect"}){
   var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;int requests=0;
   var peer=new Thread(()=>{try{using(var c=listener.AcceptTcpClient()){c.ReceiveTimeout=3000;var s=c.GetStream();RemsupProtocol.ReadLine(s,512);RemsupProtocol.WriteLine(s,"OK");RemsupProtocol.ReadLine(s,100000);Interlocked.Increment(ref requests);if(response!="disconnect")RemsupProtocol.WriteLine(s,response);}}catch{}}){IsBackground=true};peer.Start();
   try{
    try{var r=OperatorConnection.ExecuteFixture("127.0.0.1",port,"123456789","test-pass","echo probe",1,1024,CancellationToken.None,null);Check(response.StartsWith("REMSUP1 RESULT")&&r.ExitCode==7&&r.Truncated&&r.Stdout=="out"&&r.Stderr=="err","result parsing");}
    catch(IOException ex){Check(response=="REMSUP1 ERROR busy"?ex.Message.Contains("not executed"):ex.Message.Contains("Result unknown"),"known busy vs unknown outcome");}
    Check(requests==1,"no duplicate send");peer.Join(2000);
   }finally{listener.Stop();}
  }
  var timeout=CommandExecutor.Execute("ping -n 10 127.0.0.1 > nul",1,1024);
  Check(timeout.ExitCode==124&&timeout.DurationMs<8000,"remote timeout");
  foreach(bool wait in new[]{false,true}){
   string pidFile=Path.Combine(Path.GetTempPath(),"RemsupOwned-"+Guid.NewGuid().ToString("N")+".txt");
   try{
    string command="powershell.exe -NoProfile -Command \"$p=Start-Process ping.exe -ArgumentList '-n 30 127.0.0.1' -WindowStyle Hidden -PassThru; $p.Id | Set-Content -LiteralPath '"+pidFile+"'; "+(wait?"Start-Sleep -Seconds 30":"")+"\"";
    var tree=CommandExecutor.Execute(command,5,1024);
    Check(File.Exists(pidFile),"descendant fixture started");
    int pid=int.Parse(File.ReadAllText(pidFile).Trim());bool alive=false;
    try{using(var child=System.Diagnostics.Process.GetProcessById(pid)){alive=!child.HasExited;}}catch(ArgumentException){}
    Check(!alive,"owned descendant terminated on shell exit or timeout");
    Check(tree.ExitCode==(wait?124:0),"owned tree result");
   }finally{if(File.Exists(pidFile))File.Delete(pidFile);}
  }
  using(var cancel=new CancellationTokenSource()){
   cancel.Cancel();
   bool canceled=false;try{OperatorConnection.ExecuteFixture("127.0.0.1",1,"123456789","test-pass","echo no",1,1024,cancel.Token,null);}catch(OperationCanceledException){canceled=true;}Check(canceled,"pre-send cancellation");
  }
  using(var cancel=new CancellationTokenSource()){
   var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
   var peer=new Thread(()=>{try{using(var c=listener.AcceptTcpClient()){c.ReceiveTimeout=2000;var s=c.GetStream();RemsupProtocol.ReadLine(s,512);RemsupProtocol.WriteLine(s,"OK");RemsupProtocol.ReadLine(s,100000);cancel.Cancel();}}catch{}}){IsBackground=true};peer.Start();
   try{bool unknown=false;try{OperatorConnection.ExecuteFixture("127.0.0.1",port,"123456789","test-pass","echo maybe",1,1024,cancel.Token,null);}catch(IOException ex){unknown=ex.Message.Contains("Result unknown");}Check(unknown,"cancel after send is unknown outcome");peer.Join(2000);}finally{listener.Stop();}
  }
  Console.WriteLine("PASS operator result/errors/no-retry/cancel/timeout");
 }
}
