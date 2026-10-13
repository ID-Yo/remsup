using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

internal static class ParallelChannelTests
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static bool Closed(NetworkStream stream)
    {
        try{return stream.ReadByte()==-1;}
        catch(IOException ex) when(ex.InnerException is SocketException socket&&socket.SocketErrorCode==SocketError.ConnectionReset){return true;}
    }
    static (TcpClient Client,TcpClient Server) LoopbackPair()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var client=new TcpClient();client.Connect((IPEndPoint)listener.LocalEndpoint);return(client,listener.AcceptTcpClient());
    }
    static object InvokeCore(SupportHost host,string name,params object[] args)
    {
        return typeof(SupportHost).GetMethod(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(host,args);
    }
    static async Task LanPolicyAndCrossGate(SupportHost host,ScreenPeer peer,Guid lease)
    {
        int generation=host.Snapshot.Generation;
        var busy=LoopbackPair();using(busy.Client)using(busy.Server)
        {
            busy.Client.ReceiveTimeout=1000;
            InvokeCore(host,"Serve",busy.Server,generation,false,CancellationToken.None,true,false,null);
            Check(RemsupProtocol.ReadLine(busy.Client.GetStream(),512)=="ERR busy","LAN screen cannot bypass active relay screen gate");
        }

        var pendingLan=LoopbackPair();using(pendingLan.Client)using(pendingLan.Server)
        {
            typeof(SupportHost).GetField("lanReady",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(host,true);
            var lanCancellation=new CancellationTokenSource();
            typeof(SupportHost).GetField("lanCancellation",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(host,lanCancellation);
            InvokeCore(host,"Track",pendingLan.Server,generation,true,false);
            using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            InvokeCore(host,"DisableLan",generation,listener);
            pendingLan.Client.GetStream().ReadTimeout=1000;
            Check(lanCancellation.IsCancellationRequested&&Closed(pendingLan.Client.GetStream()),"LAN policy revocation cancels and closes accepted LAN sockets");
            await Status(peer,lease);
        }

        var postRevoke=LoopbackPair();using(postRevoke.Client)using(postRevoke.Server)
        {
            RemsupProtocol.WriteLine(postRevoke.Client.GetStream(),"REMSUP1 EXEC invalid");
            InvokeCore(host,"Serve",postRevoke.Server,generation,false,CancellationToken.None,false,false,null);
            postRevoke.Server.Close();postRevoke.Client.GetStream().ReadTimeout=1000;
            Check(Closed(postRevoke.Client.GetStream()),"revoked LAN route cannot dispatch a handed-off request");
        }
    }
    static TcpClient Open(int port,string password="synthetic-only",bool screen=false)
    {
        var client=new TcpClient();client.Connect("127.0.0.1",port);client.ReceiveTimeout=3000;
        RemsupProtocol.WriteLine(client.GetStream(),"OPERATOR 111222333 "+password+" "+RemsupProtocol.GenerateKey()+(screen?" SCREEN":""));return client;
    }
    static async Task Status(ScreenPeer peer,Guid lease)
    {
        var state=await Task.Run(()=>peer.Call("status"));Check(state.GetProperty("state").GetString()=="Granted"&&peer.Lease==lease,"screen grant survives independent command lifecycle");
    }
    static Task<CommandResult> Execute(int port,string command,Action<string> progress=null)=>Task.Run(()=>OperatorConnection.ExecuteFixture("127.0.0.1",port,"111222333","synthetic-only",command,15,1024,CancellationToken.None,progress));
    [STAThread] static int Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Startup+=async(_,__)=>
        {
            using var relay=new SyntheticPairingRelay();using var support=new SupportHost();using var screen=new ScreenWorkspace(true);using var peer=new ScreenPeer();support.ScreenHandler=screen.Serve;
            int readinessNotifications=0;support.RelayRegistrationsReady+=()=>throw new InvalidOperationException("synthetic closed readiness observer");support.RelayRegistrationsReady+=()=>Interlocked.Increment(ref readinessNotifications);
            int port=relay.Port;
            if(args.Length==2&&args[0]=="--owned-relay-port")port=int.Parse(args[1]);
            try
            {
                support.StartFixture("127.0.0.1:"+port,"111222333","synthetic-only");await Task.Delay(1300);
                Check(support.RelayRegistrationReady&&Volatile.Read(ref readinessNotifications)>0,"throwing readiness observer preserves both registrations and later observers");
                using(var wrong=await Task.Run(()=>Open(port,"wrong-synthetic",true)))Check(RemsupProtocol.ReadLine(wrong.GetStream(),512)=="ERR no-agent","wrong screen password cannot consume a channel");
                await Task.Run(()=>peer.ConnectFixture("127.0.0.1",port,"111222333","synthetic-only",CancellationToken.None));
                await Task.Run(()=>peer.Call("request",new{monitor_id="synthetic",control=false,operator_label="parallel-owned-test"}));await Task.Delay(300);Guid lease=peer.Lease;await Status(peer,lease);
                await Task.Run(()=>peer.Call("capture"));
                var result=await Execute(port,"echo parallel-command");Check(result.ExitCode==0&&result.Stdout.Contains("parallel-command"),"active screen plus successful command");
                await Task.Delay(1100);await Status(peer,lease);await Task.Run(()=>peer.Call("capture"));Check(peer.Png!=null,"screenshot remains available after command");
                await LanPolicyAndCrossGate(support,peer,lease);
                using(var second=await Task.Run(()=>Open(port,screen:true)))Check(RemsupProtocol.ReadLine(second.GetStream(),512)=="ERR busy","second screen refused without interrupting first");
                Check(support.TryReserveSettings(),"settings network reservation acquired while command idle");using(var blocked=await Task.Run(()=>Open(port))){Check(RemsupProtocol.ReadLine(blocked.GetStream(),512)=="OK","fixture relay pairs before receiver admission");Check(RemsupProtocol.ReadLine(blocked.GetStream(),512)=="ERR busy","settings reservation rejects new command admission");}support.ReleaseSettingsReservation();await Task.Delay(1300);await Status(peer,lease);
                bool running=false;var longCommand=Execute(port,"ping -n 7 127.0.0.1 >nul & echo long-command",value=>{if(value=="Running")Volatile.Write(ref running,true);});
                for(int i=0;i<100&&!Volatile.Read(ref running);i++)await Task.Delay(20);Check(running&&!longCommand.IsCompleted,"bounded long command is executing");Check(!support.TryReserveSettings()&&!support.TryStopForSettings()&&!longCommand.IsCompleted,"settings reservation/reconnect cannot stop an active command");
                await Status(peer,lease);await Task.Delay(1100);await Task.Run(()=>peer.Call("capture"));Check(!longCommand.IsCompleted,"status and screenshot complete while EXEC runs");
                using(var second=await Task.Run(()=>Open(port)))Check(RemsupProtocol.ReadLine(second.GetStream(),512)=="ERR busy","second command refused without interrupting first");
                Check((await longCommand).ExitCode==0,"long command completes independently");await Task.Delay(1300);await Status(peer,lease);
                using(var wrongChannel=await Task.Run(()=>Open(port)))
                {
                    Check(RemsupProtocol.ReadLine(wrongChannel.GetStream(),512)=="OK","command route paired");RemsupProtocol.WriteLine(wrongChannel.GetStream(),"REMSUPSCREEN1");
                    Check(RemsupProtocol.ReadLine(wrongChannel.GetStream(),512).Contains("wrong-channel"),"screen request on command route rejected before dispatch");
                }
                await Task.Delay(1300);await Status(peer,lease);
                using(var interrupted=await Task.Run(()=>Open(port)))
                {
                    Check(RemsupProtocol.ReadLine(interrupted.GetStream(),512)=="OK","independent command channel available");RemsupProtocol.WriteLine(interrupted.GetStream(),"REMSUP1 EXEC 10 1024 "+RemsupProtocol.Encode("ping -n 4 127.0.0.1 >nul & echo interrupted"));await Task.Delay(200);
                }
                for(int i=0;i<6;i++){await Task.Delay(1000);await Status(peer,lease);}Check((await Execute(port,"echo reconnected")).ExitCode==0,"command disconnect and re-registration preserve screen");
                await Task.Delay(1300);running=false;var stopping=Execute(port,"ping -n 10 127.0.0.1 >nul",value=>{if(value=="Running")Volatile.Write(ref running,true);});
                for(int i=0;i<100&&!Volatile.Read(ref running);i++)await Task.Delay(20);Check(running,"shutdown exercised during EXEC");support.Stop();
                try{await stopping;throw new Exception("Stopped EXEC unexpectedly completed");}catch(IOException ex){Check(ex.Message.Contains("unknown"),"interrupted command result remains unknown without replay");}
                try{await Task.Run(()=>peer.Call("status"));throw new Exception("Stopped screen remained connected");}catch(IOException){Console.WriteLine("PASS Stop closes active screen socket");}
                var sockets=(System.Collections.IList)typeof(SupportHost).GetField("sockets",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(support);Check(sockets.Count==0&&support.Snapshot.State==SupportState.Stopped,"Stop closes all tracked sockets while command runs");
            }
            catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}
            finally{support.Stop();screen.Dispose();relay.Dispose();app.Shutdown();}
        };app.Run();return Environment.ExitCode;
    }
}
