using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
internal sealed class CommandResult
{
    public int ExitCode;
    public bool Truncated;
    public int DurationMs;
    public string Stdout = "";
    public string Stderr = "";
}
internal static class RemsupProtocol
{
    const string Mask="LDLDDLLDLDLLDDLDLLDLDLDDLLDLDL";
    public static string NormalizeId(string id)
    {
        return new string((id??"").Where(c=>c>='0'&&c<='9').ToArray());
    }
    public static void ValidateCredentials(string id,string password)
    {
        if(NormalizeId(id).Length!=9||(id??"").Any(c=>!(c>='0'&&c<='9')&&!char.IsWhiteSpace(c)&&c!='-'&&c!='('&&c!=')'&&c!='.')) throw new ArgumentException("Device ID must contain nine digits and formatting only.");
        if(string.IsNullOrEmpty(password)||password.Length>128||password.Any(c=>c<=32||c>126)) throw new ArgumentException("Password must contain 1-128 printable ASCII characters without spaces.");
    }
    public static string GenerateKey()
    {
        var bytes=new byte[Mask.Length];
        using(var rng=RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        var chars=new char[Mask.Length];
        for(int i=0;i<chars.Length;i++)
        {
            string a=Mask[i]=='D'?"0123456789":"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            chars[i]=a[bytes[i]%a.Length];
        }
        return new string(chars);
    }
    public static bool ValidKey(string key)
    {
        if(key==null||key.Length!=Mask.Length)return false;
        for(int i=0;i<key.Length;i++)if(Mask[i]=='D' ? key[i]<'0'||key[i]>'9' : !((key[i]>='A'&&key[i]<='Z')||(key[i]>='a'&&key[i]<='z')))return false;
        return true;
    }
    public static string ReadLine(Stream stream,int limit)
    {
        var b=new StringBuilder();
        int timeout=stream.ReadTimeout;
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while(true)
            {
                if(timeout>0)
                {
                    int left=timeout-(int)elapsed.ElapsedMilliseconds;
                    if(left<=0)throw new IOException("Protocol deadline exceeded.");
                    stream.ReadTimeout=left;
                }
                int c=stream.ReadByte();
                if(c<0)throw new IOException("Connection closed.");
                if(c==10)return b.ToString();
                if(c!=13)b.Append((char)c);
                if(b.Length>limit)throw new IOException("Protocol message exceeds limit.");
            }
        }
        finally
        {
            try
            {
                stream.ReadTimeout=timeout;
            }
            catch(ObjectDisposedException)
            {
            }
        }
    }
    public static void WriteLine(Stream stream,string line)
    {
        var b=Encoding.ASCII.GetBytes(line+"\n");
        stream.Write(b,0,b.Length);
    }
    public static string Encode(string s)
    {
        return string.IsNullOrEmpty(s)?"-":Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    }
    public static CommandResult ParseResult(string line)
    {
        var p=line.Split(new[]
        {
            ' '
        }
        ,7);
        int code,ms;
        if(p.Length!=7||p[0]!="REMSUP1"||p[1]!="RESULT"||!int.TryParse(p[2],out code)||!int.TryParse(p[4],out ms)||ms<0||(p[3]!="0"&&p[3]!="1"))throw new IOException("Invalid command response.");
        try
        {
            return new CommandResult
            {
                ExitCode=code,Truncated=p[3]=="1",DurationMs=ms,Stdout=p[5]=="-"?"":Encoding.UTF8.GetString(Convert.FromBase64String(p[5])),Stderr=p[6]=="-"?"":Encoding.UTF8.GetString(Convert.FromBase64String(p[6]))
            }
            ;
        }
        catch(FormatException)
        {
            throw new IOException("Invalid command response encoding.");
        }
    }
    public static void Connect(TcpClient client,string host,int port,CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        // APM can expose a Task-owned/shared completion handle on modern runtimes.
        // Dispose only our own signal, never the framework's AsyncWaitHandle.
        using(var completed=new ManualResetEvent(false))
        {
            var pending=client.BeginConnect(host,port,delegate(IAsyncResult result)
            {
                try{completed.Set();}catch(ObjectDisposedException){ /* Completion after cancellation/timeout. */ }
            },null);
            int outcome=WaitHandle.WaitAny(new[]
            {
                completed,cancel.WaitHandle
            }
            ,10000);
            if(outcome==1)throw new OperationCanceledException(cancel);
            if(outcome==WaitHandle.WaitTimeout)throw new TimeoutException("Connection timed out.");
            client.EndConnect(pending);
        }
    }
    public static void KeepAlive(TcpClient client)
    {
        var b=new byte[12];
        Array.Copy(BitConverter.GetBytes((uint)1),0,b,0,4);
        Array.Copy(BitConverter.GetBytes((uint)15000),0,b,4,4);
        Array.Copy(BitConverter.GetBytes((uint)1000),0,b,8,4);
        client.Client.IOControl(IOControlCode.KeepAliveValues,b,null);
    }
    public static void ParseEndpoint(string endpoint,out string host,out int port)
    {
        var p=(endpoint??"").Split(':');
        host=p[0].Trim();
        port=443;
        if(p.Length>2||host.Length==0||(p.Length==2&&!int.TryParse(p[1],out port))||port<1||port>65535)throw new ArgumentException("Use host:port (IPv4 or DNS name).");
    }
}
