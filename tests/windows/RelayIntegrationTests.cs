using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
internal static class RelayIntegrationTests {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 public static void Run(){
  string endpoint=Environment.GetEnvironmentVariable("REMSUP_TEST_RELAY");string pin=Environment.GetEnvironmentVariable("REMSUP_TEST_RELAY_PIN");
  if(string.IsNullOrEmpty(endpoint)||string.IsNullOrWhiteSpace(pin)){Console.WriteLine("SKIP live relay: REMSUP_TEST_RELAY and its pin are not set");return;}
  string address;int port;RemsupProtocol.ParseEndpoint(endpoint,out address,out port);
  using(var host=new SupportHost()){
   host.Start(endpoint,"123456789","test-pass",null,0,pin);
   Check(SpinWait.SpinUntil(()=>host.Snapshot.State==SupportState.RelayWaiting,10000),"actual relay WAIT");
   var result=OperatorConnection.Execute(address,port,"123456789","test-pass","echo relay-ok",3,1024,CancellationToken.None,null,pin);
   Check(result.ExitCode==0&&result.Stdout.Contains("relay-ok"),"actual relay EXEC");
   Check(SpinWait.SpinUntil(()=>host.Snapshot.State==SupportState.RelayWaiting,10000),"actual relay re-register");
   var legacy=Environment.GetEnvironmentVariable("REMSUP_LEGACY_OPERATOR");
   if(!string.IsNullOrEmpty(legacy)){
    using(var cli=Process.Start(new ProcessStartInfo(legacy,"exec --relay "+endpoint+" --id 123456789 --otp test-pass --command \"echo legacy-ok\" --timeout 3 --max-output 1024"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){
     Check(WaitForTestProcess(cli)&&cli.ExitCode==0&&cli.StandardOutput.ReadToEnd().Contains("legacy-ok"),"v1.4 CLI compatibility");
    }
    Check(SpinWait.SpinUntil(()=>host.Snapshot.State==SupportState.RelayWaiting,10000),"legacy re-register");
    Console.WriteLine("PASS v1.4 CLI with new host");
   }
   var mcpScript=Path.Combine(Path.GetTempPath(),"remsup-mcp-"+Guid.NewGuid().ToString("N")+".py");
   try {
    File.WriteAllText(mcpScript,"import sys\nsys.path.insert(0, sys.argv[1])\nfrom remsup_relay_client import exec_command_over_relay\nr=exec_command_over_relay(sys.argv[2],int(sys.argv[3]),'123456789','test-pass','echo mcp-ok',3,1024)\nassert r['exit_code']==0 and 'mcp-ok' in r['stdout']\nprint('PASS existing Python MCP relay client with new host')\n");
    string mcp=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","mcp"));
    using(var process=Process.Start(new ProcessStartInfo("python","-B \""+mcpScript+"\" \""+mcp+"\" "+address+" "+port){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){
     Check(WaitForTestProcess(process)&&process.ExitCode==0,"MCP relay client compatibility");Console.WriteLine(process.StandardOutput.ReadToEnd().Trim());
    }
   }finally{File.Delete(mcpScript);}
   Check(SpinWait.SpinUntil(()=>host.Snapshot.State==SupportState.RelayWaiting,10000),"MCP re-register");
   if(Environment.GetEnvironmentVariable("REMSUP_TEST_BLACKHOLE")=="1"){
    // Only the dedicated local WSL test relay port is affected; always remove the rule.
    string rule="INPUT -p tcp --dport "+port+" -m comment --comment remsup-epic-blackhole -j DROP";
    try{
     Wsl("sudo -n iptables -I "+rule);
     var watch=Stopwatch.StartNew();
     Check(SpinWait.SpinUntil(()=>host.Snapshot.ColorName!="Green",45000),"blackhole must clear green within 45s");
     Console.WriteLine("PASS real relay idle blackhole detected in "+watch.ElapsedMilliseconds+"ms");
    }finally{Wsl("sudo -n iptables -D "+rule);}
    Check(SpinWait.SpinUntil(()=>host.Snapshot.State==SupportState.RelayWaiting,15000),"relay recovery after blackhole");
    using(var client=new TcpClient()){
     client.Connect(address,port);client.ReceiveTimeout=10000;var stream=client.GetStream();
     RemsupProtocol.WriteLine(stream,"OPERATOR 123456789 test-pass "+RemsupProtocol.GenerateKey());Check(RemsupProtocol.ReadLine(stream,512)=="OK","active loss pairing");
     RemsupProtocol.WriteLine(stream,"REMSUP1 EXEC 60 1024 "+RemsupProtocol.Encode("ping -n 55 127.0.0.1 > nul"));
     Check(SpinWait.SpinUntil(()=>host.Snapshot.State==SupportState.SessionActive,3000),"active command");
     try{
      Wsl("sudo -n iptables -I "+rule);var watch=Stopwatch.StartNew();
      Check(SpinWait.SpinUntil(()=>host.Snapshot.ColorName!="Green",45000),"active blackhole clears green");
      Check(host.Snapshot.Description.Contains("Busy"),"active lost connection retains busy");
      Console.WriteLine("PASS active-command blackhole detected in "+watch.ElapsedMilliseconds+"ms");
     }finally{Wsl("sudo -n iptables -D "+rule);host.Stop();}
    }
   }
   host.Stop();
  }
  var oldClient=Environment.GetEnvironmentVariable("REMSUP_LEGACY_CLIENT");
  if(!string.IsNullOrEmpty(oldClient)){
   var assembly=System.Reflection.Assembly.LoadFile(oldClient);var type=assembly.GetType("RelayAgent",true);var ready=new ManualResetEvent(false);
   var legacy=Activator.CreateInstance(type,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,new object[]{address,port,"987654321","test-pass",(Action<string>)(message=>{if(message=="Waiting for operator")ready.Set();})},null);
   try{type.GetMethod("Start").Invoke(legacy,null);Check(ready.WaitOne(10000),"v1.4 client registered");var result=OperatorConnection.Execute(address,port,"987654321","test-pass","echo old-client-ok",3,1024,CancellationToken.None,null,pin);Check(result.ExitCode==0&&result.Stdout.Contains("old-client-ok"),"new operator with old client");Console.WriteLine("PASS new operator with old client RelayAgent");}
   finally{type.GetMethod("Stop").Invoke(legacy,null);ready.Dispose();}
  }
  Console.WriteLine("PASS actual Linux relay handshake/command/re-register");
 }
 static void Wsl(string args){
  using(var process=Process.Start(new ProcessStartInfo("wsl.exe","-d Debian -- "+args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){
   if(!WaitForTestProcess(process)||process.ExitCode!=0)throw new Exception("Test firewall command failed.");
  }
 }
 static bool WaitForTestProcess(Process process){if(process.WaitForExit(10000))return true;process.Kill();process.WaitForExit(5000);return false;}
}
