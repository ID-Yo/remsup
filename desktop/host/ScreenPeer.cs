using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

internal sealed class ScreenRefusalException:IOException {internal ScreenRefusalException(string message):base(message){}}

// Persistent controlled-test channel. Local owner configuration/consent grants rights after pairing.
internal sealed class ScreenPeer:IDisposable
{
    readonly TcpClient client=new TcpClient();internal readonly SemaphoreSlim Gate=new SemaphoreSlim(1,1);Stream stream;internal Guid Lease;internal string State="Off";internal bool View,Control;internal JsonElement Frame;internal byte[] Png;
    internal void Connect(string host,int port,string id,string password,CancellationToken token,string pin=null,bool isLan=false)
    {ConnectCore(host,port,id,password,token,pin,isLan,false);}
    internal void ConnectFixture(string host,int port,string id,string password,CancellationToken token)
    {RelayTls.ValidateLoopbackFixture(host);ConnectCore(host,port,id,password,token,null,false,true);}
    void ConnectCore(string host,int port,string id,string password,CancellationToken token,string pin,bool isLan,bool fixture)
    {
        RemsupProtocol.ValidateCredentials(id,password);string expectedPin=fixture?null:RelayTls.PinFor(host,port,pin,isLan);RemsupProtocol.Connect(client,host,port,token);client.ReceiveTimeout=10000;client.SendTimeout=10000;stream=fixture?(Stream)client.GetStream():RelayTls.OpenStream(client,host,expectedPin,token);
        RemsupProtocol.WriteLine(stream,"OPERATOR "+RemsupProtocol.NormalizeId(id)+" "+password+" "+RemsupProtocol.GenerateKey()+" SCREEN");if(RemsupProtocol.ReadLine(stream,512)!="OK")throw new IOException("Screen pairing refused: unavailable, busy or credentials do not match.");
        RemsupProtocol.WriteLine(stream,"REMSUPSCREEN1");if(RemsupProtocol.ReadLine(stream,512)!="REMSUPSCREEN1 OK")throw new IOException("Remote screen access is disabled or unsupported.");
    }
    internal JsonElement Call(string operation,object fields=null)
    {
        var request=new System.Collections.Generic.Dictionary<string,object>{{"version",1},{"request_id",Guid.NewGuid().ToString("D")},{"operation",operation}};
        if(operation!="capabilities"&&operation!="request")request["session_id"]=Lease.ToString("D");
        if(fields!=null)foreach(var property in JsonSerializer.SerializeToElement(fields).EnumerateObject())request[property.Name]=property.Value;
        byte[] json=JsonSerializer.SerializeToUtf8Bytes(request);Write(json);var response=JsonDocument.Parse(Read(4096)).RootElement.Clone();
        if(response.GetProperty("request_id").GetString()!=(string)request["request_id"]||response.GetProperty("version").GetInt32()!=1)throw new IOException("Screen response mismatch.");
        if(response.TryGetProperty("dispatched",out var dispatched)&&dispatched.ValueKind==JsonValueKind.False&&response.TryGetProperty("error",out var refusal)){if(response.GetProperty("session_id").GetString()!=Lease.ToString("D"))throw new IOException("Screen response mismatch.");throw new ScreenRefusalException("Screen operation refused before dispatch: "+refusal.GetString());}
        if(response.TryGetProperty("error",out var inputError)){
            if(response.GetProperty("session_id").GetString()!=Lease.ToString("D"))throw new IOException("Screen response mismatch.");
            string code=inputError.GetString();if(code!="input-not-applied"&&code!="outcome-unknown")throw new IOException("Invalid screen input response.");
            Png=null;Frame=default;
            throw new ScreenRefusalException("Screen input: "+code+"; dispatch="+response.GetProperty("dispatched").ToString()+". Do not retry automatically; take and inspect a new capture.");
        }
        if(operation=="request")Lease=Guid.Parse(response.GetProperty("session_id").GetString());
        if(operation=="capture")
        {
            int width=response.GetProperty("width").GetInt32(),height=response.GetProperty("height").GetInt32();if(width<2||height<2||width>1920||height>1920||response.GetProperty("session_id").GetString()!=Lease.ToString("D")||response.GetProperty("mime_type").GetString()!="image/png")throw new IOException("Invalid screen image.");
            Png=Read(4194304);if(Png.Length<33||Png[0]!=137||Png[1]!=80||Png[2]!=78||Png[3]!=71||Size(Png,16)!=width||Size(Png,20)!=height)throw new IOException("Invalid screen PNG.");Frame=response;
        }
        else if(response.TryGetProperty("state",out var state)){State=state.GetString();View=response.GetProperty("can_view").GetBoolean();Control=response.GetProperty("can_control").GetBoolean();}
        return response;
    }
    static int Size(byte[] data,int i){return (data[i]<<24)|(data[i+1]<<16)|(data[i+2]<<8)|data[i+3];}
    byte[] Read(int cap){byte[] header=Exact(4);int count=Size(header,0);if(count<2||count>cap)throw new IOException("Screen size limit.");return Exact(count);}
    byte[] Exact(int count){var data=new byte[count];int offset=0;var watch=System.Diagnostics.Stopwatch.StartNew();while(offset<count){int left=10000-(int)watch.ElapsedMilliseconds;if(left<=0)throw new IOException("Screen deadline exceeded.");stream.ReadTimeout=left;int n=stream.Read(data,offset,count-offset);if(n==0)throw new IOException("Screen disconnected.");offset+=n;}stream.ReadTimeout=10000;return data;}
    void Write(byte[] bytes){int n=bytes.Length;stream.Write(new[]{(byte)(n>>24),(byte)(n>>16),(byte)(n>>8),(byte)n});stream.Write(bytes);}
    public void Dispose(){client.Close();Png=null;View=Control=false;State="Off";}
}
