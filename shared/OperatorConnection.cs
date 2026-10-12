using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
internal static class OperatorConnection
{
    public static CommandResult Execute(string host,int port,string id,string password,string command,int timeout,int maxOutput,CancellationToken cancel,Action<string> progress,string pin=null,bool isLan=false)
    {
        return ExecuteCore(host,port,id,password,command,timeout,maxOutput,cancel,progress,pin,isLan,false);
    }
    internal static CommandResult ExecuteFixture(string host,int port,string id,string password,string command,int timeout,int maxOutput,CancellationToken cancel,Action<string> progress)
    {
        RelayTls.ValidateLoopbackFixture(host);
        return ExecuteCore(host,port,id,password,command,timeout,maxOutput,cancel,progress,null,false,true);
    }
    static CommandResult ExecuteCore(string host,int port,string id,string password,string command,int timeout,int maxOutput,CancellationToken cancel,Action<string> progress,string pin,bool isLan,bool fixture)
    {
        RemsupProtocol.ValidateCredentials(id,password);
        string expectedPin=fixture?null:RelayTls.PinFor(host,port,pin,isLan);
        if(port<1||port>65535||timeout<1||timeout>300||maxOutput<1024||maxOutput>1048576||string.IsNullOrWhiteSpace(command)||command.Length>65536)throw new ArgumentException("Invalid endpoint, command or execution limits.");
        bool sent=false;
        using(var client=new TcpClient())
        using(cancel.Register(()=>client.Close()))
        {
            try
            {
                if(progress!=null)progress("Connecting");
                RemsupProtocol.Connect(client,host,port,cancel);
                client.ReceiveTimeout=10000;
                client.SendTimeout=10000;
                var stream=fixture?(Stream)client.GetStream():RelayTls.OpenStream(client,host,expectedPin,cancel);
                RemsupProtocol.WriteLine(stream,"OPERATOR "+RemsupProtocol.NormalizeId(id)+" "+password+" "+RemsupProtocol.GenerateKey());
                var response=RemsupProtocol.ReadLine(stream,512);
                if(response!="OK")throw new IOException(response=="ERR busy"?"Device is busy.":response=="ERR no-agent"?"Device unavailable or credentials do not match.":"Connection refused.");
                cancel.ThrowIfCancellationRequested();
                client.ReceiveTimeout=(timeout+10)*1000;
                sent=true;
                // A failed write may still have delivered a complete request.
                RemsupProtocol.WriteLine(stream,"REMSUP1 EXEC "+timeout+" "+maxOutput+" "+RemsupProtocol.Encode(command));
                if(progress!=null)progress("Running");
                var line=RemsupProtocol.ReadLine(stream,2800000);
                if(line=="REMSUP1 ERROR busy"||line=="ERR busy")
                {
                    sent=false;
                    throw new IOException("Device is busy; command was not executed.");
                }
                if(line.StartsWith("REMSUP1 ERROR ",StringComparison.Ordinal))
                {
                    sent=false;
                    throw new IOException("Device rejected the command request.");
                }
                return RemsupProtocol.ParseResult(line);
            }
            catch(Exception ex)
            {
                if(sent)throw new IOException("Result unknown - the command may have executed. Cancel/disconnect does not stop the remote process. Do not retry automatically.",ex);
                if(cancel.IsCancellationRequested)throw new OperationCanceledException(cancel);
                throw;
            }
        }
    }
}
