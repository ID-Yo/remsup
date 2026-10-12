using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

internal static class LanTlsTests
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"remsup-lan-tls-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyFastConnect();
            using(var identity=LanTls.LoadIdentity(root))
            {
                string pin=RelayTls.ComputePin(identity);
                using(var reopened=LanTls.LoadIdentity(root))Check(pin==RelayTls.ComputePin(reopened)&&reopened.HasPrivateKey,"DPAPI identity persists without rotating its key");
                Check(RelayTls.PinFor("127.0.0.1",443,pin,true)==pin,"explicit LAN profile keeps receiver pin");
                try{RelayTls.PinFor("127.0.0.1",443,null,true);throw new Exception("missing LAN pin accepted");}catch(InvalidOperationException){Check(true,"missing LAN pin refused");}
                VerifyPinnedCommand(identity,pin,false);
                VerifyPinnedCommand(identity,pin,true);
                VerifyPinnedScreen(identity,pin,false);VerifyPinnedScreen(identity,pin,true);
                try{OperatorConnection.Execute("127.0.0.1",1,"123456789","synthetic-secret","echo test",5,1024,CancellationToken.None,null,null,true);throw new Exception("missing command pin connected");}catch(InvalidOperationException){Check(true,"missing command pin refuses before connecting");}
                using(var peer=new ScreenPeer()){try{peer.Connect("127.0.0.1",1,"123456789","synthetic-secret",CancellationToken.None,null,true);throw new Exception("missing screen pin connected");}catch(InvalidOperationException){Check(true,"missing screen pin refuses before connecting");}}
            }
            string path=Path.Combine(root,"identity.dpapi");
            File.WriteAllBytes(path,new byte[]{1,2,3});
            try{using(var invalid=LanTls.LoadIdentity(root)){}throw new Exception("corrupt identity regenerated");}catch(System.Security.Cryptography.CryptographicException){Check(File.ReadAllBytes(path).Length==3,"corrupt identity fails without replacing its key");}
        }
        finally{Directory.Delete(root,true);}
    }

    static void VerifyFastConnect()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        Exception failure=null;var server=new Thread(()=>{try{for(int i=0;i<300;i++){using(var connection=listener.AcceptTcpClient()){} }}catch(Exception error){failure=error;}}){IsBackground=true};server.Start();
        try{for(int i=0;i<300;i++){using(var client=new TcpClient())RemsupProtocol.Connect(client,"127.0.0.1",port,CancellationToken.None);}Check(server.Join(5000)&&failure==null,"fast completed connections retain framework-owned wait handles");}
        finally{listener.Stop();server.Join(5000);}
    }

    static void VerifyPinnedScreen(X509Certificate2 identity,string pin,bool wrong)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        int credentialBytes=0;Exception serverFailure=null;
        var server=new Thread(()=>{try{using(var client=listener.AcceptTcpClient())using(var stream=LanTls.Accept(client,identity,CancellationToken.None)){string header=RemsupProtocol.ReadLine(stream,512);credentialBytes=header.Length;if(wrong)return;Check(header.EndsWith(" SCREEN"),"screen header is inside LAN TLS");RemsupProtocol.WriteLine(stream,"OK");Check(RemsupProtocol.ReadLine(stream,512)=="REMSUPSCREEN1","screen protocol uses same TLS stream");RemsupProtocol.WriteLine(stream,"REMSUPSCREEN1 OK");}}catch(Exception ex){serverFailure=ex;}}){IsBackground=true};server.Start();
        try
        {
            using(var peer=new ScreenPeer())
            {
                string expected=wrong?(pin[0]=='0'?"1"+pin.Substring(1):"0"+pin.Substring(1)):pin;
                try{peer.Connect("127.0.0.1",port,"123456789","synthetic-secret",CancellationToken.None,expected,true);if(wrong)throw new Exception("wrong screen pin accepted");}
                catch(IOException){if(!wrong)throw;}
            }
            Check(server.Join(5000),"bounded LAN screen fixture exits");
            if(wrong)Check(credentialBytes==0,"wrong screen pin sends no credential header");else if(serverFailure!=null)throw serverFailure;
        }
        finally{listener.Stop();server.Join(11000);}
    }
    static void VerifyPinnedCommand(X509Certificate2 identity,string pin,bool wrong)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        int credentialBytes=0;Exception serverFailure=null;
        var server=new Thread(()=>{try{using(var peer=listener.AcceptTcpClient())using(var stream=LanTls.Accept(peer,identity,CancellationToken.None)){string header=RemsupProtocol.ReadLine(stream,512);credentialBytes=header.Length;if(wrong)return;RemsupProtocol.WriteLine(stream,"OK");RemsupProtocol.ReadLine(stream,100000);RemsupProtocol.WriteLine(stream,"REMSUP1 RESULT 0 0 1 "+RemsupProtocol.Encode("lan-tls-ok")+" -");}}catch(Exception ex){serverFailure=ex;}}){IsBackground=true};server.Start();
        try
        {
            if(wrong)
            {
                string rejected=pin[0]=='0'?"1"+pin.Substring(1):"0"+pin.Substring(1);
                try{OperatorConnection.Execute("127.0.0.1",port,"123456789","synthetic-secret","echo test",5,1024,CancellationToken.None,null,rejected,true);throw new Exception("wrong LAN pin accepted");}catch(IOException){}
            }
            else
            {
                var result=OperatorConnection.Execute("127.0.0.1",port,"123456789","synthetic-secret","echo test",5,1024,CancellationToken.None,null,pin,true);
                Check(result.ExitCode==0,"LAN command uses pinned TLS");
            }
            Check(server.Join(5000),"bounded LAN server exits");
            if(wrong)Check(credentialBytes==0,"wrong LAN pin sends no credential header");else if(serverFailure!=null)throw serverFailure;
        }
        catch(Exception failure){server.Join(11000);throw new AggregateException("LAN TLS fixture failed",serverFailure??failure,failure);}
        finally{listener.Stop();server.Join(11000);}
    }
}
