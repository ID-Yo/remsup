using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

internal static class LanTls
{
    static readonly object sync=new object();
    static X509Certificate2 identity;
    internal static X509Certificate2 Identity
    {
        get {lock(sync){return identity??(identity=LoadIdentity(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Remsup","LanIdentity")));}}
    }
    internal static string OwnPin {get{return RelayTls.ComputePin(Identity);}}

    internal static X509Certificate2 LoadIdentity(string directory)
    {
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"identity.dpapi");
        if(!File.Exists(path))
        {
            byte[] pfx=null,encrypted=null;
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var key=RSA.Create())
                {
                    key.KeySize=2048;
                    var request=new CertificateRequest("CN=Remsup LAN",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
                    using(var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(10)))
                        pfx=certificate.Export(X509ContentType.Pfx,"");
                }
                encrypted=ProtectedData.Protect(pfx,null,DataProtectionScope.CurrentUser);
                File.WriteAllBytes(temporary,encrypted);
                try{File.Move(temporary,path);}catch(IOException){if(!File.Exists(path))throw;}
            }
            finally
            {
                if(pfx!=null)Array.Clear(pfx,0,pfx.Length);
                if(encrypted!=null)Array.Clear(encrypted,0,encrypted.Length);
                if(File.Exists(temporary))File.Delete(temporary);
            }
        }
        if(new FileInfo(path).Length>65536)throw new CryptographicException("LAN identity storage is invalid; the existing key was preserved.");
        byte[] protectedBytes=File.ReadAllBytes(path),clear=null;
        try
        {
            clear=ProtectedData.Unprotect(protectedBytes,null,DataProtectionScope.CurrentUser);
#if NET9_0_OR_GREATER
            var loaded=X509CertificateLoader.LoadPkcs12(clear,"",X509KeyStorageFlags.UserKeySet);
#else
            var loaded=new X509Certificate2(clear,"",X509KeyStorageFlags.UserKeySet);
#endif
            if(!loaded.HasPrivateKey){loaded.Dispose();throw new CryptographicException("LAN identity has no private key.");}
            return loaded;
        }
        finally{Array.Clear(protectedBytes,0,protectedBytes.Length);if(clear!=null)Array.Clear(clear,0,clear.Length);}
    }

    internal static SslStream Accept(TcpClient client,X509Certificate2 certificate,CancellationToken token)
    {
        var stream=new SslStream(client.GetStream(),false);
        using(token.Register(()=>{try{client.Close();}catch{}}))
        {
            try
            {
                var authentication=System.Threading.Tasks.Task.Run(()=>stream.AuthenticateAsServer(certificate,false,SslProtocols.Tls12|(SslProtocols)12288,false));
                if(!authentication.Wait(10000))throw new IOException("LAN TLS handshake timed out.");
                authentication.GetAwaiter().GetResult();token.ThrowIfCancellationRequested();return stream;
            }
            catch{client.Close();stream.Dispose();throw;}
        }
    }
}
