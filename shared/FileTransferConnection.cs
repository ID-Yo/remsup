using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

internal static class FileTransferConnection
{
 internal sealed class CommitGate:IDisposable {
  readonly object gate=new object();readonly CancellationTokenRegistration registration;readonly CancellationToken token;bool cancelled;
  internal CommitGate(CancellationToken token){this.token=token;registration=token.Register(()=>{lock(gate)cancelled=true;});}
  internal void Place(FileTransferPartial partial){lock(gate){if(cancelled||token.IsCancellationRequested)throw new FileTransferFailure("cancelled");partial.Place();}}
  public void Dispose(){registration.Dispose();}
 }
 internal static FileTransferInfo Transfer(string host,int port,string id,string password,string pin,bool lan,string localPath,string remotePath,bool upload,bool overwrite,CancellationToken cancel,Action<FileTransferInfo> progress){
  RemsupProtocol.ValidateCredentials(id,password);string expectedPin=RelayTls.PinFor(host,port,pin,lan);string local=FileTransferProtocol.LocalPath(localPath);bool commitSent=false;
  using(var overall=new CancellationTokenSource(TimeSpan.FromMinutes(30)))
  using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancel,overall.Token))
  using(var client=new TcpClient())
  using(linked.Token.Register(()=>client.Close())){
   try{
    client.NoDelay=true;RemsupProtocol.Connect(client,host,port,cancel);client.ReceiveTimeout=20000;client.SendTimeout=20000;
    using(var stream=RelayTls.OpenStream(client,host,expectedPin,cancel)){
     RemsupProtocol.WriteLine(stream,"OPERATOR "+RemsupProtocol.NormalizeId(id)+" "+password+" "+RemsupProtocol.GenerateKey());
     string paired=RemsupProtocol.ReadLine(stream,512);if(paired!="OK")throw new FileTransferFailure(paired=="ERR busy"?"busy":"device-unavailable");
     RemsupProtocol.WriteLine(stream,"REMSUPFILE1");FileTransferClient.Hello(stream);
     if(upload){
      using(var binding=new FileTransferPaths(local))using(var file=binding.OpenSource(local)){
       if(file.Length>FileTransferProtocol.MaxBytes)throw new FileTransferFailure("file-too-large");string sha=FileTransferProtocol.Hash(file,()=>cancel.ThrowIfCancellationRequested());file.Position=0;
       var info=new FileTransferInfo{Size=file.Length,Sha256=sha,Direction="Upload",State="Sending"};FileTransferClient.BeginUpload(stream,remotePath,info.Size,sha,overwrite);var bytes=new byte[FileTransferProtocol.ChunkBytes];int n;
       while((n=file.Read(bytes,0,bytes.Length))!=0){cancel.ThrowIfCancellationRequested();FileTransferClient.UploadChunk(stream,info.Completed,bytes,n);info.Completed+=n;if(progress!=null)progress(info);}
       cancel.ThrowIfCancellationRequested();commitSent=true;var result=FileTransferClient.Finish(stream);if(result.Size!=info.Size||result.Sha256!=sha)throw new FileTransferFailure("integrity-failed");return result;
      }
     }
     var meta=FileTransferClient.BeginDownload(stream,remotePath);meta.Direction="Download";meta.State="Receiving";
     using(var commit=new CommitGate(cancel))using(var partial=new FileTransferPartial(local,meta.Size,meta.Sha256,overwrite)){
      while(meta.Completed<meta.Size){cancel.ThrowIfCancellationRequested();byte[] bytes=FileTransferClient.DownloadChunk(stream,meta.Completed,(int)Math.Min(FileTransferProtocol.ChunkBytes,meta.Size-meta.Completed));partial.Write(meta.Completed,bytes,bytes.Length);meta.Completed+=bytes.Length;if(progress!=null)progress(meta);}
      cancel.ThrowIfCancellationRequested();var result=FileTransferClient.Finish(stream);if(result.Size!=meta.Size||result.Sha256!=meta.Sha256)throw new FileTransferFailure("integrity-failed");partial.Prepare();commit.Place(partial);return result;
     }
    }
   }catch(FileTransferFailure ex){if(commitSent&&(ex.Code=="interrupted"||ex.Code=="invalid-response"||ex.Code=="transfer-timeout"))throw new FileTransferFailure("outcome-unknown");throw;}catch(Exception ex){if(commitSent)throw new FileTransferFailure("outcome-unknown");if(cancel.IsCancellationRequested)throw new FileTransferFailure("cancelled");if(overall.IsCancellationRequested)throw new FileTransferFailure("transfer-timeout");throw new FileTransferFailure(FileTransferProtocol.Failure(ex));}
  }
 }
}
