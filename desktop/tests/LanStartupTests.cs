using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
internal static class LanStartupTests {
 sealed class Dialogs:IHostDialogs {public bool Confirm(string t,string x)=>true;public string Credential(string t,string x)=>null;public void Reveal(string t,string x){}public void Copy(string x){}public void Error(string x){}}
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
 static JsonElement Payload(object value)=>JsonSerializer.SerializeToElement(value);
 [STAThread]static int Main(string[] args){RuntimeSetup.Initialize();var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Startup+=async(_,__)=>{
  try{
   Directory.CreateDirectory(args[0]);await VerifyDisabledLanDraft(args[0]);using var relay=new SyntheticPairingRelay("fixture-password");
   using(var first=new ApplicationInstance("Local\\Remsup.OwnActivationTest."+Guid.NewGuid(),true)){
    var signaled=new System.Threading.ManualResetEventSlim();first.OnActivate(()=>signaled.Set());
    await Task.Run(()=>{using var second=new ApplicationInstance(first.Name,true);Check(!second.IsOwner,"duplicate startup cannot acquire owner mutex");});
    Check(signaled.Wait(2000),"duplicate startup signals existing window without a dialog");
   }
   string legacy=Path.Combine(args[0],"legacy-logs");Directory.CreateDirectory(legacy);File.WriteAllText(Path.Combine(legacy,"state.jsonl.1"),"{\"utc\":\"2026-10-12T12:00:00Z\",\"event\":\"update-check\",\"data\":{\"result\":\"Current\",\"version\":\"v1.48\",\"error\":\"\"}}\n");File.WriteAllText(Path.Combine(legacy,"state.jsonl"),"{}\n");Check(new RuntimeDiagnostics(legacy).Updates(true).last_check.version=="v1.48","bounded rotated legacy last-check is imported without inventing install history");
   var diagnostics=new RuntimeDiagnostics(Path.Combine(args[0],"logs"));
   var logState=new SupportSnapshot(1,SupportState.RelayWaiting,false,false,"not logged",true,"LAN test PASSWORD_SENTINEL");
   diagnostics.State(logState,true,true,"RolledBack PASSWORD_SENTINEL","PASSWORD_SENTINEL");diagnostics.State(logState,true,true,"RolledBack PASSWORD_SENTINEL","PASSWORD_SENTINEL");
   diagnostics.UpdateCheck("Current","v1.48","","PASSWORD_SENTINEL");
   string log=File.ReadAllText(Path.Combine(args[0],"logs","state.jsonl"));Check(!log.Contains("PASSWORD_SENTINEL")&&log.Contains("lan_warning")&&log.Contains("update-check")&&File.ReadAllLines(Path.Combine(args[0],"logs","state.jsonl")).Length==2,"diagnostics records exact states/checks, deduplicates and redacts secret");
   File.WriteAllText(Path.Combine(args[0],"logs","state.jsonl"),new string('x',262145));diagnostics.UpdateCheck("Error","","owned failure","");Check(File.Exists(Path.Combine(args[0],"logs","state.jsonl.1"))&&new FileInfo(Path.Combine(args[0],"logs","state.jsonl")).Length<4096,"diagnostics rotates to one bounded prior log");
   diagnostics.Download("v1.49",new string('a',64));diagnostics.Install("Deferred","");diagnostics.Install("RolledBack","readiness-failed");
   for(int i=0;i<12;i++)diagnostics.Failure("check-failed");
   var restored=new RuntimeDiagnostics(Path.Combine(args[0],"logs"));var metadata=JsonSerializer.SerializeToElement(restored.Updates(false));
   Check(metadata.GetProperty("last_install").GetProperty("error_code").GetString()=="readiness-failed"&&metadata.GetProperty("last_download").GetProperty("sha256").GetString()==new string('a',64)&&metadata.GetProperty("latest_errors").GetArrayLength()==8&&!metadata.GetProperty("enabled").GetBoolean(),"updater check/download/rollback survives reopening with bounded safe error codes");
   diagnostics.Install("Installed","");Check(restored.Updates(true).last_install.result=="Installed","candidate reads predecessor committed outcome after startup");
   Check(RuntimeDiagnostics.SafeLanWarning("PASSWORD_SENTINEL path output")=="LAN diagnostics unavailable.","diagnostics reject arbitrary warning text");
   await VerifyLanRecovery();
   string owned="Remsup LAN ABCDEF012345 192.0.2.34:8444";
   Check(LanAccessPolicy.ShouldRemoveRule(owned,"old.exe","new.exe",false),"update/install selects old executable rule for cleanup");
   Check(!LanAccessPolicy.ShouldRemoveRule(owned,"new.exe","new.exe",false),"migration keeps current executable rule");
   Check(LanAccessPolicy.ShouldRemoveRule(owned,"new.exe","new.exe",true),"Remove LAN rule removes current and historical owned rules");
   Check(!LanAccessPolicy.ShouldRemoveRule("Remsup LAN unrelated","old.exe","new.exe",true),"unrelated firewall names preserved");
   var ip=IPAddress.Parse("192.0.2.34");
   Check(LanAccessPolicy.UnavailableReason(ip,8444,_=>true,_=>"Public",(_,__)=>true).Contains("Public"),"Public profile warning identifies Windows Settings action");
   Check(LanAccessPolicy.UnavailableReason(ip,8444,_=>true,_=>"Private",(_,__)=>false).Contains("Prepare LAN firewall"),"missing scoped rule warning names Prepare action");
   Check(LanAccessPolicy.UnavailableReason(ip,8444,_=>true,_=>"Private",(_,__)=>true)=="","ready LAN requires active Private interface and scoped rule");
   File.WriteAllText(Path.Combine(args[0],"settings.txt"),"RelayPort=8443\nLanEnabled=True\nLanAddress=192.0.2.254\nLanPort=8444\nPermanentPassword=synthetic-only\n");
   using var host=new HostController(new HostOptions(new[]{"--fixture-root",args[0],"--fixture-relay","127.0.0.1:"+relay.Port}),new Dialogs());await host.ReadyAsync();await Task.Delay(500);
   var state=JsonSerializer.SerializeToElement(host.Snapshot());Check(state.GetProperty("settings").GetProperty("relayPort").GetInt32()==443,"legacy8443 migrates to pinned TLS443");
   Check(state.TryGetProperty("lanWarning",out var warning)&&warning.GetString().Contains("inactive"),"stale LAN address produces independent specific warning");
   Check(state.GetProperty("receivingState").GetString()=="RelayWaiting","invalid optional LAN cannot stop relay registration");
   var diagnostic=await Task.Run(()=>ReadDiagnostic(relay.Port));Check(diagnostic.GetProperty("schema_version").GetInt32()==1&&!diagnostic.GetRawText().Contains("fixture-password")&&!diagnostic.GetRawText().Contains("111222333")&&!diagnostic.GetRawText().Contains("172.16."),"authenticated diagnostic wire projects state without identity/credentials/address");
   Check(diagnostic.GetProperty("lan").GetProperty("enabled").GetBoolean()&&!diagnostic.GetProperty("lan").GetProperty("ready").GetBoolean()&&diagnostic.GetProperty("lan").GetProperty("warning").GetString().Contains("inactive"),"diagnostic wire distinguishes configured unavailable LAN from ready relay");
   var registrationDeadline=DateTime.UtcNow.AddSeconds(8);while(JsonSerializer.SerializeToElement(host.Snapshot()).GetProperty("receivingState").GetString()!="RelayWaiting"&&DateTime.UtcNow<registrationDeadline)await Task.Delay(100);
   var result=await Task.Run(()=>OperatorConnection.ExecuteFixture("127.0.0.1",relay.Port,"111222333","fixture-password","echo relay-with-stale-lan",5,1024,CancellationToken.None,null));Check(result.ExitCode==0&&result.Stdout.Contains("relay-with-stale-lan"),"real relay command succeeds with stale LAN settings");
   await host.HandleAsync("settings.save",Payload(new{relayHost="91.227.77.177",relayPort=443,relayPin="",autoUpdates=true,autoStart=false,lanEnabled=true,lanAddress="192.0.2.253",lanPort=8444,allowScreenView=true,allowScreenControl=true,autoAcceptScreenSessions=true}));
   state=JsonSerializer.SerializeToElement(host.Snapshot());Check(state.GetProperty("settings").GetProperty("lanEnabled").GetBoolean()&&state.GetProperty("settings").GetProperty("lanAddress").GetString()=="192.0.2.253","LAN opt-in/draft saved without blocking relay or changing Windows network");
   using var reopened=new HostController(new HostOptions(new[]{"--fixture-root",args[0]}),new Dialogs());Check(JsonSerializer.SerializeToElement(reopened.Snapshot()).GetProperty("settings").GetProperty("lanEnabled").GetBoolean(),"unavailable LAN preference remains saved after reopen");
  }catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}finally{app.Shutdown();}
 };app.Run();return Environment.ExitCode;}
 static JsonElement ReadDiagnostic(int port){using var client=new System.Net.Sockets.TcpClient();client.Connect(IPAddress.Loopback,port);client.ReceiveTimeout=5000;var stream=client.GetStream();RemsupProtocol.WriteLine(stream,"OPERATOR 111222333 fixture-password "+RemsupProtocol.GenerateKey());Check(RemsupProtocol.ReadLine(stream,512)=="OK","diagnostics pairs using existing authenticated COMMAND channel");RemsupProtocol.WriteLine(stream,"REMSUPDIAG1");string line=RemsupProtocol.ReadLine(stream,24000);Check(line.StartsWith("REMSUPDIAG1 "),"diagnostic bounded wire reply");return JsonDocument.Parse(Convert.FromBase64String(line.Substring(12))).RootElement.Clone();}
 static async Task VerifyLanRecovery(){
  using var relay=new SyntheticPairingRelay("fixture-password");bool permitted=false;
  var probe=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
  using var key=System.Security.Cryptography.RSA.Create(2048);var request=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=owned LAN test",key,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
  using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
  using var host=new SupportHost((_,__)=>permitted?"":"LAN test rule missing",()=>certificate);
  host.StartFixture("127.0.0.1:"+relay.Port,"111222333","fixture-password",IPAddress.Loopback,port);
  Check(!host.Snapshot.LanReady,"startup missing rule does not listen");permitted=true;var deadline=DateTime.UtcNow.AddSeconds(15);while(!host.Snapshot.LanReady&&DateTime.UtcNow<deadline)await Task.Delay(100);
  Check(host.Snapshot.LanReady&&host.Snapshot.LanWarning=="","corrected LAN policy starts listener without restarting relay");
  using(var client=new System.Net.Sockets.TcpClient()){await client.ConnectAsync(IPAddress.Loopback,port);}
  permitted=false;host.RefreshLan();Check(!host.Snapshot.LanReady,"revoked policy stops LAN only");
  permitted=true;host.RefreshLan();Check(host.Snapshot.LanReady,"restored policy recovers LAN listener");
  Check(host.TrySuspendRelayForUpdate(),"idle update suspends listener");host.RefreshLan();Check(!host.Snapshot.LanReady,"LAN refresh cannot undo update suspension");host.ResumeRelayAfterUpdate();Check(host.Snapshot.LanReady,"update rollback restores configured LAN listener");
  host.Stop();host.RefreshLan();Check(!host.Snapshot.LanReady,"Stop cannot be undone by a stale LAN refresh");
 }
 static async Task VerifyDisabledLanDraft(string root){
  string folder=Path.Combine(root,"disabled-lan-save");Directory.CreateDirectory(folder);using var relay=new SyntheticPairingRelay("fixture-password");
  using var host=new HostController(new HostOptions(new[]{"--fixture-root",folder,"--fixture-relay","127.0.0.1:"+relay.Port}),new Dialogs());await host.ReadyAsync();await Task.Delay(500);
  var command=Task.Run(()=>OperatorConnection.ExecuteFixture("127.0.0.1",relay.Port,"111222333","fixture-password","powershell -NoProfile -Command \"Start-Sleep -Milliseconds 1400; Write-Output stable-startup-save\"",5,1024,CancellationToken.None,null));await Task.Delay(350);
  await host.HandleAsync("settings.save",Payload(new{relayHost="91.227.77.177",relayPort=443,relayPin="",autoUpdates=true,autoStart=true,lanEnabled=false,lanAddress="192.168.250.2",lanPort=8445,allowScreenView=true,allowScreenControl=true,autoAcceptScreenSessions=true}));
  var result=await command;Check(result.ExitCode==0&&result.Stdout.Contains("stable-startup-save"),"General startup Save with disabled LAN draft preserves the active command");
 }
}
