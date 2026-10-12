using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

internal sealed class SyntheticPairingRelay:IDisposable
{
    readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);readonly object gate=new object();readonly List<TcpClient> sockets=new List<TcpClient>();readonly Dictionary<string,TcpClient> agents=new Dictionary<string,TcpClient>();readonly HashSet<string> active=new HashSet<string>();bool closed;
    internal int Port;readonly string password;
    internal SyntheticPairingRelay(string password="synthetic-only"){this.password=password;listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;_=Task.Run(async()=>{while(!closed){try{var peer=await listener.AcceptTcpClientAsync();lock(gate)sockets.Add(peer);_=Task.Run(()=>Serve(peer));}catch(ObjectDisposedException){break;}catch(SocketException){break;}}});}
    void Serve(TcpClient peer)
    {
        try
        {
            peer.ReceiveTimeout=10000;var stream=peer.GetStream();string header=RemsupProtocol.ReadLine(stream,512);
            var fields=header.Split(' ');string route=fields.Length==4?"COMMAND":fields.Length==5&&fields[4]=="SCREEN"?"SCREEN":"";
            if(route==""||fields[1]!="111222333"||fields[2]!=password||!RemsupProtocol.ValidKey(fields[3])){RemsupProtocol.WriteLine(stream,"ERR no-agent");peer.Dispose();return;}
            if(fields[0]=="AGENT"){lock(gate){if(active.Contains(route)||agents.ContainsKey(route)){RemsupProtocol.WriteLine(stream,"ERR busy");peer.Dispose();return;}agents[route]=peer;RemsupProtocol.WriteLine(stream,"WAIT");}return;}
            TcpClient target;lock(gate){if(active.Contains(route)){RemsupProtocol.WriteLine(stream,"ERR busy");peer.Dispose();return;}agents.TryGetValue(route,out target);if(target!=null){agents.Remove(route);active.Add(route);}}
            if(target==null||fields[0]!="OPERATOR"){RemsupProtocol.WriteLine(stream,"ERR no-agent");peer.Dispose();return;}
            RemsupProtocol.WriteLine(stream,"OK");RemsupProtocol.WriteLine(target.GetStream(),"OK");
            try{Task a=Task.Run(()=>target.GetStream().CopyTo(stream)),b=Task.Run(()=>stream.CopyTo(target.GetStream()));Task.WhenAny(a,b).GetAwaiter().GetResult();}finally{peer.Dispose();target.Dispose();lock(gate)active.Remove(route);}
        }
        catch{peer.Dispose();}
    }
    public void Dispose(){closed=true;listener.Stop();lock(gate){foreach(var peer in sockets)peer.Dispose();sockets.Clear();}}
}
internal static class ScreenLoopbackTests
{
    [STAThread] static int Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};string output=args[0];Directory.CreateDirectory(output);
        app.Startup+=async(_,__)=>
        {
            using var relay=new SyntheticPairingRelay();using var support=new SupportHost();using var screens=new ScreenWorkspace(true);support.ScreenHandler=screens.Serve;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};string approved="";DateTime grant=DateTime.MinValue;
            timer.Tick+=(_,__)=>{var receiving=JsonSerializer.SerializeToElement(screens.Receiving);string label=receiving.GetProperty("operatorLabel").GetString();if(receiving.GetProperty("state").GetString()=="Pending"&&label!="synthetic-no-consent"&&label!=approved){screens.CaptureConsentFixture(Path.Combine(output,label+"-pending.png"));screens.ApproveFixture(label=="synthetic-control");approved=label;grant=DateTime.UtcNow;}if(label=="synthetic-stop"&&(DateTime.UtcNow-grant).TotalSeconds>2)screens.StopReceiver();};
            try
            {
                support.StartFixture("127.0.0.1:"+relay.Port,"111222333","synthetic-only");await Task.Delay(1200);
                screens.Enable(false);using(var disabled=new ScreenPeer()){try{await Task.Run(()=>disabled.ConnectFixture("127.0.0.1",relay.Port,"111222333","synthetic-only",CancellationToken.None));throw new Exception("Screen accepted with viewing disabled");}catch(IOException ex){if(!ex.Message.Contains("screen access"))throw;}}
                await Task.Delay(1500);var command=await Task.Run(()=>OperatorConnection.ExecuteFixture("127.0.0.1",relay.Port,"111222333","synthetic-only","echo screen-command-regression",5,1024,CancellationToken.None,null));if(command.ExitCode!=0||!command.Stdout.Contains("screen-command-regression"))throw new Exception("command regression");
                await Task.Delay(1500);screens.Enable(true,true,false);timer.Start();
                var start=new ProcessStartInfo("python"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("-B");start.ArgumentList.Add(Path.GetFullPath("tests/mcp/screen_end_to_end.py"));start.ArgumentList.Add(relay.Port.ToString());start.ArgumentList.Add(output);
                start.Environment["REMSUP_MCP_TOKEN"]="synthetic-test-token";start.Environment.Remove("REMSUP_MCP_CLIENTS_JSON");start.Environment["REMSUP_RELAY_HOST"]="127.0.0.1";start.Environment["REMSUP_RELAY_PORT"]=relay.Port.ToString();start.Environment["REMSUP_SCREEN_TEST_MODE"]="1";start.Environment["REMSUP_AUDIT_LOG"]=Path.Combine(output,"synthetic-audit.jsonl");start.Environment["PYTHONUTF8"]="1";
                using var process=Process.Start(start);var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(90000);
                try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(true);await process.WaitForExitAsync();throw;}
                string result=await stdout;File.WriteAllText(Path.Combine(output,"result.json"),result);if(process.ExitCode!=0)throw new Exception(await stderr);Console.WriteLine(result.Trim());
                timer.Stop();await screens.Request("race",new DeviceEndpoint("127.0.0.1",1),"111222333","synthetic-only","",true);await Task.Delay(250);screens.ApproveFixture(true);await screens.Operate("race","status");await screens.Operate("race","capture");
                try{await screens.Operate("race","capture");throw new Exception("Early capture was not refused");}catch(ScreenRefusalException ex){if(!ex.Message.Contains("capture-rate-limit"))throw;}
                await screens.Operate("race","status");if(!JsonSerializer.SerializeToElement(screens.Snapshot("race")).GetProperty("canView").GetBoolean())throw new Exception("Capture refusal dropped GUI channel");
                Console.WriteLine("PASS GUI recoverable capture rate refusal keeps granted channel and image");
                var policyPeers=(Dictionary<string,ScreenPeer>)typeof(ScreenWorkspace).GetField("peers",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(screens);await policyPeers["race"].Gate.WaitAsync();var queuedPolicyMouse=screens.Operate("race","mouse");screens.Enable(true,false,false);policyPeers["race"].Gate.Release();try{await queuedPolicyMouse;throw new Exception("queued mouse ignored disabled policy");}catch(InvalidOperationException ex){if(!ex.Message.Contains("disabled in Settings"))throw;}if(JsonSerializer.SerializeToElement(screens.Snapshot("race")).GetProperty("canControl").GetBoolean())throw new Exception("operator policy not reflected");
                try{await screens.Operate("race","mouse");throw new Exception("operator mouse ignored Settings");}catch(InvalidOperationException ex){if(!ex.Message.Contains("disabled in Settings"))throw;}
                screens.Enable(true,true,false);screens.Close("race");await Task.Delay(1100);await screens.Request("race",new DeviceEndpoint("127.0.0.1",1),"111222333","synthetic-only","",true);await Task.Delay(250);screens.ApproveFixture(true);await screens.Operate("race","status");await screens.Operate("race","capture");
                Console.WriteLine("PASS operator Settings disable prevents input and fresh consent restores control");
                screens.Close("race");screens.Enable(true,true,true);await Task.Delay(1100);await screens.Request("auto",new DeviceEndpoint("127.0.0.1",1),"111222333","synthetic-only","",true);await Task.Delay(300);await screens.Operate("auto","status");if(!JsonSerializer.SerializeToElement(screens.Snapshot("auto")).GetProperty("canControl").GetBoolean())throw new Exception("autoaccept did not grant control");screens.CaptureConsentFixture(Path.Combine(output,"autoaccepted-active.png"));var consent=(ScreenConsentWindow)typeof(ScreenWorkspace).GetField("consent",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(screens);if(!consent.IsVisible||consent.WindowState!=WindowState.Minimized)throw new Exception("workspace autoaccept card not minimized");consent.ShowCurrentRequest();consent.Refresh();if(consent.WindowState!=WindowState.Normal)throw new Exception("workspace restore minimized again");await screens.Operate("auto","status");if(!JsonSerializer.SerializeToElement(screens.Snapshot("auto")).GetProperty("canControl").GetBoolean())throw new Exception("workspace restore revoked grant");Console.WriteLine("PASS workspace configured autoaccept minimizes once and restore retains grant");
                screens.Enable(true,true,false);await screens.Operate("auto","status");if(!JsonSerializer.SerializeToElement(screens.Snapshot("auto")).GetProperty("canControl").GetBoolean())throw new Exception("autoaccept toggle revoked current session");screens.Close("auto");await screens.Request("manual",new DeviceEndpoint("127.0.0.1",1),"111222333","synthetic-only","",true);await Task.Delay(300);await screens.Operate("manual","status");if(JsonSerializer.SerializeToElement(screens.Snapshot("manual")).GetProperty("state").GetString()!="Pending")throw new Exception("autoaccept false did not await local consent");screens.Close("manual");
                screens.Enable(true,false,true);await screens.Request("view-auto",new DeviceEndpoint("127.0.0.1",1),"111222333","synthetic-only","",false);await Task.Delay(300);await screens.Operate("view-auto","status");var autoView=JsonSerializer.SerializeToElement(screens.Snapshot("view-auto"));if(!autoView.GetProperty("canView").GetBoolean()||autoView.GetProperty("canControl").GetBoolean())throw new Exception("autoaccept view-only policy");screens.Enable(false,false,true);if(JsonSerializer.SerializeToElement(screens.Receiving).GetProperty("canView").GetBoolean())throw new Exception("disabled view autoaccept not revoked");
                screens.Enable(true,true,false);await Task.Delay(1100);await screens.Request("race",new DeviceEndpoint("127.0.0.1",1),"111222333","synthetic-only","",true);await Task.Delay(250);screens.ApproveFixture(true);await screens.Operate("race","status");await screens.Operate("race","capture");Console.WriteLine("PASS auto/manual policy, active grant survives autoaccept disable, view-only and disabled view");
                var displayed=JsonSerializer.SerializeToElement(screens.Snapshot("race"));string oldFrame=displayed.GetProperty("frameId").GetString();int oldGeometry=displayed.GetProperty("geometryVersion").GetInt32();await Task.Delay(1100);
                var peers=(Dictionary<string,ScreenPeer>)typeof(ScreenWorkspace).GetField("peers",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(screens);var racePeer=peers["race"];await racePeer.Gate.WaitAsync();
                var captureQueued=screens.Operate("race","capture");var mouseQueued=screens.Operate("race","mouse","click",320,175,0,oldFrame,oldGeometry);racePeer.Gate.Release();await captureQueued;
                try{await mouseQueued;throw new Exception("Old displayed frame rebound to new capture");}catch(InvalidOperationException ex){if(ex.InnerException?.Message!="displayed-frame-stale")throw;}
                Console.WriteLine("PASS queued capture ahead of old-image mouse action rejects immutable displayed frame");
                screens.Enable(false);await Task.Delay(1500);command=await Task.Run(()=>OperatorConnection.ExecuteFixture("127.0.0.1",relay.Port,"111222333","synthetic-only","echo after-screen",5,1024,CancellationToken.None,null));if(command.ExitCode!=0)throw new Exception("command after screen");Console.WriteLine("PASS real SupportHost loopback pairing, settings-disable, local native button consent, own-visual capture/input and post-screen command");
            }
            catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}
            finally{timer.Stop();support.Stop();screens.Dispose();relay.Dispose();app.Shutdown();}
        };
        app.Run();return Environment.ExitCode;
    }
}
