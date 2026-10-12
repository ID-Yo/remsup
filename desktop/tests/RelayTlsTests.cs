using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

internal static class RelayTlsTests
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}

    static void Main()
    {
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN=localhost",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        using var certificate=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx,"relay-test"),"relay-test",X509KeyStorageFlags.Exportable|X509KeyStorageFlags.PersistKeySet);
        string pin=RelayTls.ComputePin(certificate);
        Check(RelayTls.NormalizePin(pin)==pin,"SPKI pin normalization");
        Check(RelayTls.PinFor(RelayTls.DefaultHost,RelayTls.DefaultPort,null,false)==RelayTls.DefaultPin,"built-in endpoint pin");
        try{RelayTls.PinFor("custom.example.test",443,null,false);throw new Exception("unpinned custom endpoint accepted");}
        catch(InvalidOperationException ex){Check(ex.Message.Contains("pin"),"custom relay without pin refused");}
        ValidPinSendsCredentialsOnlyOverPinnedStream(certificate,pin);
        WrongPinNeverSendsCredentials(certificate,pin);
        HandshakeHasTenSecondBound();
        Console.WriteLine("Relay TLS checks passed");
    }

    static void ValidPinSendsCredentialsOnlyOverPinnedStream(X509Certificate2 certificate,string pin)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        string header=null;Exception serverError=null;Exception clientError=null;var completed=new ManualResetEventSlim();
        var server=new Thread(()=>{try{using var client=listener.AcceptTcpClient();using var stream=new SslStream(client.GetStream(),false);stream.AuthenticateAsServer(certificate,false,SslProtocols.Tls12|SslProtocols.Tls13,false);stream.ReadTimeout=3000;header=RemsupProtocol.ReadLine(stream,512);RemsupProtocol.WriteLine(stream,"ERR no-agent");}catch(Exception ex){serverError=ex;}finally{completed.Set();}}){IsBackground=true};server.Start();
        try
        {
            try{OperatorConnection.Execute("127.0.0.1",port,"123456789","synthetic-secret","echo must-not-run",1,1024,CancellationToken.None,null,pin);}
            catch(IOException ex){clientError=ex;}
            Check(completed.Wait(5000),"valid-pin relay fixture completes");
            Check(clientError!=null&&clientError.Message.Contains("unavailable"),"valid pin reaches encrypted relay handshake: "+(clientError?.Message??"no client error")+"; server="+(serverError?.GetType().Name+": "+serverError?.Message??"none"));
            Check(serverError==null&&header!=null&&header.StartsWith("OPERATOR 123456789 synthetic-secret ",StringComparison.Ordinal),"credentials are sent only after valid pin negotiation");
        }
        finally{listener.Stop();server.Join(3500);}
    }

    static void WrongPinNeverSendsCredentials(X509Certificate2 certificate,string pin)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        int applicationBytes=0;var completed=new ManualResetEventSlim();
        var server=new Thread(()=>{try{using var client=listener.AcceptTcpClient();using var stream=new SslStream(client.GetStream(),false);try{stream.AuthenticateAsServer(certificate,false,SslProtocols.Tls12|SslProtocols.Tls13,false);stream.ReadTimeout=1500;byte[] buffer=new byte[512];applicationBytes=stream.Read(buffer,0,buffer.Length);}catch(AuthenticationException){}catch(IOException){}}finally{completed.Set();}}){IsBackground=true};server.Start();
        string wrong=pin[0]=='0'?"1"+pin.Substring(1):"0"+pin.Substring(1);
        try
        {
            try{OperatorConnection.Execute("127.0.0.1",port,"123456789","wrong-pin-secret","echo must-not-run",1,1024,CancellationToken.None,null,wrong);throw new Exception("wrong pin was accepted");}
            catch(IOException ex){Check(ex.Message.Contains("does not match"),"wrong SPKI pin is rejected");}
            Check(completed.Wait(5000),"wrong-pin relay fixture completes");
            Check(applicationBytes==0,"no protocol header or credentials cross a wrong-pin connection");
        }
        finally{listener.Stop();server.Join(3500);}
    }

    static void HandshakeHasTenSecondBound()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;var accepted=new ManualResetEventSlim();
        var server=new Thread(()=>{try{using var client=listener.AcceptTcpClient();accepted.Set();Thread.Sleep(12000);}catch{} }){IsBackground=true};server.Start();
        using var client=new TcpClient();var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            RemsupProtocol.Connect(client,"127.0.0.1",port,CancellationToken.None);
            Check(accepted.Wait(2000),"handshake timeout fixture accepted TCP");
            try{RelayTls.OpenStream(client,"localhost",RelayTls.DefaultPin,CancellationToken.None);throw new Exception("silent TLS peer did not time out");}
            catch(IOException ex){Check(ex.Message.Contains("timed out"),"TLS handshake has bounded timeout");}
            Check(watch.Elapsed.TotalSeconds>=9&&watch.Elapsed.TotalSeconds<12.5,"TLS handshake timeout is approximately ten seconds");
        }
        finally{listener.Stop();server.Join(1000);}
    }
}
