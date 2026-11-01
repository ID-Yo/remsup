using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static class FileTransferTests
{
 static int checks;
 static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;Console.WriteLine("PASS "+name);}
 static void Refuse(Action action,string code){try{action();throw new Exception("Accepted "+code);}catch(FileTransferFailure ex){Check(ex.Code==code,"refuse "+code);}}
 static string Hash(string path){using(var h=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(h.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
 static int Main(string[] args){
  string root=Path.GetFullPath(args[0]);if(Directory.Exists(root))throw new Exception("Fresh fixture required");Directory.CreateDirectory(root);
  try{
   string source=Path.Combine(root,"synthetic-source.exe");using(var f=File.Create(source)){var block=new byte[65536];new Random(98).NextBytes(block);for(int i=0;i<1600;i++)f.Write(block,0,block.Length);}
   string sha=Hash(source);long length=new FileInfo(source).Length;Check(length==100L*1024*1024,"100MiB synthetic executable fixture, never executed");
   string target=Path.Combine(root,"received.exe");
   using(var receiver=new FileTransferReceiver(()=>false,null))using(var pair=new Pair(receiver)){
    FileTransferClient.Hello(pair.Client);Refuse(()=>FileTransferClient.BeginUpload(pair.Client,target,length,sha,false),"permission-required");
   }
   using(var receiver=new FileTransferReceiver(()=>true,null))using(var pair=new Pair(receiver)){
    FileTransferClient.Hello(pair.Client);FileTransferClient.BeginUpload(pair.Client,target,length,sha,false);
    using(var f=File.OpenRead(source)){var buffer=new byte[FileTransferProtocol.ChunkBytes];long offset=0;int n;while((n=f.Read(buffer,0,buffer.Length))!=0){FileTransferClient.UploadChunk(pair.Client,offset,buffer,n);offset+=n;}}
    var done=FileTransferClient.Finish(pair.Client);Check(done.Size==length&&done.Sha256==sha&&Hash(target)==sha,"100MiB upload exact size/SHA");
   }
   using(var receiver=new FileTransferReceiver(()=>true,null))using(var pair=new Pair(receiver)){
    FileTransferClient.Hello(pair.Client);var meta=FileTransferClient.BeginDownload(pair.Client,target);Check(meta.Size==length&&meta.Sha256==sha,"download metadata stable");
    using(var h=SHA256.Create()){long offset=0;while(offset<meta.Size){var b=FileTransferClient.DownloadChunk(pair.Client,offset,(int)Math.Min(65536,meta.Size-offset));CheckBuffer(b);h.TransformBlock(b,0,b.Length,b,0);offset+=b.Length;}h.TransformFinalBlock(new byte[0],0,0);Check(BitConverter.ToString(h.Hash).Replace("-","").ToLowerInvariant()==sha,"100MiB download raw chunks/SHA");}
    Check(FileTransferClient.Finish(pair.Client).Size==length,"download completed");
   }
   using(var receiver=new FileTransferReceiver(()=>true,null))using(var pair=new Pair(receiver)){FileTransferClient.Hello(pair.Client);Refuse(()=>FileTransferClient.BeginUpload(pair.Client,target,0,new string('0',64),false),"destination-exists");}
   string bad=Path.Combine(root,"bad.exe");
   using(var receiver=new FileTransferReceiver(()=>true,null))using(var pair=new Pair(receiver)){FileTransferClient.Hello(pair.Client);FileTransferClient.BeginUpload(pair.Client,bad,3,new string('0',64),false);FileTransferClient.UploadChunk(pair.Client,0,new byte[]{1,2,3},3);Refuse(()=>FileTransferClient.Finish(pair.Client),"integrity-failed");}
   Check(!File.Exists(bad)&&Directory.GetFiles(root,"*.partial").Length==0,"integrity failure never exposes final or partial");
   using(var receiver=new FileTransferReceiver(()=>true,null))using(var pair=new Pair(receiver)){FileTransferClient.Hello(pair.Client);FileTransferClient.BeginUpload(pair.Client,bad,10,new string('0',64),false);FileTransferClient.Cancel(pair.Client);}
   Check(!File.Exists(bad)&&Directory.GetFiles(root,"*.partial").Length==0,"cancel cleans only owned partial");
   Refuse(()=>FileTransferProtocol.LocalPath("relative.txt"),"invalid-path");Refuse(()=>FileTransferProtocol.LocalPath(@"\\server\share\file"),"invalid-path");Refuse(()=>FileTransferProtocol.LocalPath(target+":stream"),"invalid-path");
   string parent=Path.Combine(root,"pinned-parent");Directory.CreateDirectory(parent);string bound=Path.Combine(parent,"file.bin");File.WriteAllText(bound,"original");
   using(var binding=new FileTransferPaths(bound))using(var input=binding.OpenSource(bound)){
    bool blocked=false;try{Directory.Move(parent,parent+"-swapped");}catch(IOException){blocked=true;}Check(blocked&&Directory.Exists(parent),"checked ancestor cannot be renamed after source open");
    blocked=false;try{File.Move(bound,bound+"-foreign");}catch(IOException){blocked=true;}Check(blocked,"opened source cannot be replaced while hashing/streaming");
   }
   int hashChecks=0;bool hashCancelled=false;try{using(var stream=File.OpenRead(source))FileTransferProtocol.Hash(stream,()=>{if(++hashChecks==3)throw new OperationCanceledException();});}catch(OperationCanceledException){hashCancelled=true;}Check(hashCancelled&&hashChecks==3,"hashing cancellation checked at bounded64KiB intervals");
   string emptySha=FileTransferProtocol.Hash(new MemoryStream());
   using(var partial=new FileTransferPartial(bound,0,emptySha,true)){
    partial.Prepare();bool blocked=false;try{Directory.Move(parent,parent+"-swapped");}catch(IOException){blocked=true;}Check(blocked,"ancestor swap immediately before placement prevented");partial.Place();
   }
   Check(new FileInfo(bound).Length==0,"explicit overwrite uses atomic native rename within pinned parent");
   string cancelled=Path.Combine(root,"cancel-boundary.bin");bool permission=true;
   using(var receiver=new FileTransferReceiver(()=>permission,null)){
    receiver.BeforeCommit=()=>{permission=false;receiver.Stop();permission=true;};
    using(var pair=new Pair(receiver)){FileTransferClient.Hello(pair.Client);FileTransferClient.BeginUpload(pair.Client,cancelled,0,emptySha,false);bool refused=false;try{FileTransferClient.Finish(pair.Client);}catch(IOException){refused=true;}Check(refused,"Stop/revoke-reenable at empty COMMIT refuses completion");}
   }
   Check(!File.Exists(cancelled)&&Directory.GetFiles(root,"*.partial").Length==0,"cancel winner places no final and handle cleanup removes only owned partial");
   FileTransferRaceTests.Run(root);
   Console.WriteLine("PASS "+checks+" file transfer checks plus 7 reviewer race regressions; bounded64KiB buffers; no remote target");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
 }
 static void CheckBuffer(byte[] bytes){if(bytes.Length<1||bytes.Length>65536)throw new Exception("Unbounded chunk");}
 sealed class Pair:IDisposable{
  readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);readonly TcpClient socket=new TcpClient();readonly Task server;internal readonly Stream Client;
  internal Pair(FileTransferReceiver receiver){listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;server=Task.Run(()=>{using(var accepted=listener.AcceptTcpClient()){accepted.NoDelay=true;accepted.ReceiveTimeout=5000;accepted.SendTimeout=5000;receiver.Serve(accepted.GetStream(),CancellationToken.None);}});socket.NoDelay=true;socket.Connect(IPAddress.Loopback,port);socket.ReceiveTimeout=5000;socket.SendTimeout=5000;Client=socket.GetStream();}
  public void Dispose(){socket.Dispose();listener.Stop();if(!server.Wait(10000))throw new Exception("Owned server did not stop");}
 }
}
