using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal sealed class FileTransferFailure:IOException
{
 internal readonly string Code;
 internal FileTransferFailure(string code):base(code){Code=code;}
}
internal sealed class FileTransferInfo
{
 internal long Size,Completed;
 internal string Sha256="",State="Idle",Direction="",Path="",Error="";
}
internal static class FileTransferProtocol
{
 internal const int ChunkBytes=65536;
 internal const long MaxBytes=1073741824;
 internal static string Hash(Stream stream,Action check=null){using(var hash=SHA256.Create()){var bytes=new byte[ChunkBytes];int n;while(true){if(check!=null)check();n=stream.Read(bytes,0,bytes.Length);if(n==0)break;hash.TransformBlock(bytes,0,n,bytes,0);}if(check!=null)check();hash.TransformFinalBlock(new byte[0],0,0);return Hex(hash.Hash);}}
 internal static string Hex(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
 internal static void Validate(long size,string sha){if(size<0||size>MaxBytes||sha==null||sha.Length!=64)throw new FileTransferFailure("invalid-metadata");foreach(char c in sha)if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')))throw new FileTransferFailure("invalid-metadata");}
 internal static string LocalPath(string path){
  if(string.IsNullOrWhiteSpace(path)||path.Length>1024||path.Length<3||!char.IsLetter(path[0])||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')||path.IndexOf(':',2)>=0||path.IndexOf('\0')>=0)throw new FileTransferFailure("invalid-path");
  string full;try{full=System.IO.Path.GetFullPath(path);}catch(ArgumentException){throw new FileTransferFailure("invalid-path");}
  for(string current=full;!string.IsNullOrEmpty(current);current=System.IO.Path.GetDirectoryName(current)){
   if(File.Exists(current)||Directory.Exists(current))if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new FileTransferFailure("reparse-path");
   if(current==System.IO.Path.GetPathRoot(current))break;
  }
  if(Directory.Exists(full))throw new FileTransferFailure("invalid-path");return full;
 }
 internal static string DecodePath(string value){try{var b=Convert.FromBase64String(value);if(b.Length>4096)throw new FileTransferFailure("invalid-path");return LocalPath(new UTF8Encoding(false,true).GetString(b));}catch(FormatException){throw new FileTransferFailure("invalid-path");}catch(DecoderFallbackException){throw new FileTransferFailure("invalid-path");}}
 internal static long Number(string value){long n;if(!long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out n)||n<0)throw new FileTransferFailure("invalid-metadata");return n;}
 internal static string[] Line(Stream stream){return RemsupProtocol.ReadLine(stream,8192).Split(' ');}
 internal static void Write(Stream stream,string value){RemsupProtocol.WriteLine(stream,value);stream.Flush();}
 internal static void Exact(Stream stream,byte[] bytes,int count){int offset=0;var clock=Stopwatch.StartNew();int original=stream.ReadTimeout;try{while(offset<count){int remaining=20000-(int)clock.ElapsedMilliseconds;if(remaining<=0)throw new FileTransferFailure("transfer-timeout");stream.ReadTimeout=remaining;int n=stream.Read(bytes,offset,count-offset);if(n<=0)throw new FileTransferFailure("interrupted");offset+=n;}}finally{try{stream.ReadTimeout=original;}catch(ObjectDisposedException){}}}
 internal static string Failure(Exception ex){var known=ex as FileTransferFailure;if(known!=null)return known.Code;if(ex is UnauthorizedAccessException)return "access-denied";if(ex is DirectoryNotFoundException)return "parent-missing";if(ex is FileNotFoundException)return "source-missing";if(ex is OperationCanceledException)return "cancelled";if(ex is ArgumentException||ex is NotSupportedException||ex is PathTooLongException)return "invalid-path";if((ex.HResult&65535)==112||(ex.HResult&65535)==39)return "insufficient-space";return "interrupted";}
}

// One owned same-directory partial. Only a verified complete file can become the destination.
internal sealed class FileTransferPartial:IDisposable
{
 readonly string destination,temporary;readonly bool replace;readonly long expected;readonly string sha;
 readonly SHA256 hash=SHA256.Create();FileStream file;FileTransferPaths binding;long count;bool committed;
 internal FileTransferPartial(string path,long size,string expectedSha,bool overwrite){
  FileTransferProtocol.Validate(size,expectedSha);destination=FileTransferProtocol.LocalPath(path);expected=size;sha=expectedSha;
  string parent=System.IO.Path.GetDirectoryName(destination);if(!Directory.Exists(parent))throw new FileTransferFailure("parent-missing");
  replace=File.Exists(destination);if(replace&&!overwrite)throw new FileTransferFailure("destination-exists");
  if(new DriveInfo(System.IO.Path.GetPathRoot(destination)).AvailableFreeSpace<size)throw new FileTransferFailure("insufficient-space");
  temporary=System.IO.Path.Combine(parent,".remsup-"+Guid.NewGuid().ToString("N")+".partial");
  binding=new FileTransferPaths(destination);try{file=binding.CreatePartial(temporary);}catch{binding.Dispose();throw;}
 }
 internal void Write(long offset,byte[] bytes,int length){if(offset!=count||length<1||length>FileTransferProtocol.ChunkBytes||length>bytes.Length||length>expected-count)throw new FileTransferFailure("invalid-offset");file.Write(bytes,0,length);hash.TransformBlock(bytes,0,length,bytes,0);count+=length;}
 internal void Prepare(){
  if(count!=expected)throw new FileTransferFailure("size-mismatch");hash.TransformFinalBlock(new byte[0],0,0);if(FileTransferProtocol.Hex(hash.Hash)!=sha)throw new FileTransferFailure("integrity-failed");
  file.Flush(true);
 }
 internal void Place(){binding.RenameOwned(file.SafeFileHandle,destination,replace);committed=true;}
 public void Dispose(){try{if(file!=null){try{if(!committed)FileTransferPaths.DeleteOwned(file.SafeFileHandle);}finally{file.Dispose();file=null;}}}finally{hash.Dispose();if(binding!=null)binding.Dispose();}}
}

internal sealed class FileTransferReceiver:IDisposable
{
 readonly Func<bool> allowed;readonly Action<FileTransferInfo> progress;readonly object sync=new object();Stream active;int generation;
 internal Action BeforeCommit;
 internal FileTransferReceiver(Func<bool> permission,Action<FileTransferInfo> changed){allowed=permission;progress=changed;}
 internal void Stop(){Stream close;lock(sync){generation++;close=active;}if(close!=null)try{close.Close();}catch(IOException){}}
 void Notify(FileTransferInfo state){if(progress!=null)progress(state);}
 internal void Serve(Stream stream,CancellationToken token){
  var info=new FileTransferInfo();bool started=false;int epoch;var lifetime=Stopwatch.StartNew();
  lock(sync){if(active!=null)throw new FileTransferFailure("busy");active=stream;epoch=generation;}
  using(var cancel=token.Register(Stop))using(var timer=new Timer(_=>{if(lifetime.Elapsed.TotalMinutes>=30||(started&&!allowed()))Stop();},null,1000,1000)){
   try{
    stream.ReadTimeout=20000;stream.WriteTimeout=20000;
    // SupportHost has already consumed REMSUPFILE1; direct fixtures call the same handler.
    FileTransferProtocol.Write(stream,"REMSUPFILE1 READY 65536 1073741824");
    var request=FileTransferProtocol.Line(stream);if(!allowed())throw new FileTransferFailure("permission-required");token.ThrowIfCancellationRequested();started=true;
    if(request.Length==5&&request[0]=="PUT"&&(request[4]=="0"||request[4]=="1")){
     info.Path=FileTransferProtocol.DecodePath(request[1]);info.Size=FileTransferProtocol.Number(request[2]);info.Sha256=request[3];info.Direction="Upload";info.State="Receiving";
     using(var partial=new FileTransferPartial(info.Path,info.Size,info.Sha256,request[4]=="1")){
      Notify(info);FileTransferProtocol.Write(stream,"ACK 0");var buffer=new byte[FileTransferProtocol.ChunkBytes];
      while(true){token.ThrowIfCancellationRequested();if(!allowed())throw new FileTransferFailure("permission-revoked");var command=FileTransferProtocol.Line(stream);
       if(command.Length==1&&command[0]=="CANCEL"){info.State="Cancelled";FileTransferProtocol.Write(stream,"CANCELLED");return;}
       if(command.Length==1&&command[0]=="COMMIT"){partial.Prepare();if(BeforeCommit!=null)BeforeCommit();lock(sync){if(epoch!=generation||token.IsCancellationRequested||!allowed())throw new FileTransferFailure("cancelled");partial.Place();info.State="Complete";}break;}
       if(command.Length!=3||command[0]!="DATA")throw new FileTransferFailure("invalid-request");
       long offset=FileTransferProtocol.Number(command[1]),n=FileTransferProtocol.Number(command[2]);if(offset!=info.Completed||n<1||n>buffer.Length||n>info.Size-info.Completed)throw new FileTransferFailure("invalid-offset");
       FileTransferProtocol.Exact(stream,buffer,(int)n);token.ThrowIfCancellationRequested();if(!allowed())throw new FileTransferFailure("permission-revoked");partial.Write(offset,buffer,(int)n);info.Completed+=n;Notify(info);FileTransferProtocol.Write(stream,"ACK "+info.Completed);
      }
     }
    }else if(request.Length==2&&request[0]=="GET"){
     info.Path=FileTransferProtocol.DecodePath(request[1]);info.Direction="Download";info.State="Sending";
     using(var binding=new FileTransferPaths(info.Path))using(var file=binding.OpenSource(info.Path)){
      if(file.Length>FileTransferProtocol.MaxBytes)throw new FileTransferFailure("file-too-large");info.Size=file.Length;info.Sha256=FileTransferProtocol.Hash(file,()=>{token.ThrowIfCancellationRequested();lock(sync)if(epoch!=generation||!allowed()||lifetime.Elapsed.TotalMinutes>=30)throw new FileTransferFailure("cancelled");});file.Position=0;Notify(info);FileTransferProtocol.Write(stream,"META "+info.Size+" "+info.Sha256);var buffer=new byte[FileTransferProtocol.ChunkBytes];
      while(true){token.ThrowIfCancellationRequested();if(!allowed())throw new FileTransferFailure("permission-revoked");var command=FileTransferProtocol.Line(stream);
       if(command.Length==1&&command[0]=="CANCEL"){info.State="Cancelled";FileTransferProtocol.Write(stream,"CANCELLED");return;}
       if(command.Length==1&&command[0]=="COMMIT"){if(info.Completed!=info.Size)throw new FileTransferFailure("size-mismatch");break;}
       if(command.Length!=3||command[0]!="READ")throw new FileTransferFailure("invalid-request");long offset=FileTransferProtocol.Number(command[1]),n=FileTransferProtocol.Number(command[2]);if(offset!=info.Completed||n<1||n>buffer.Length||n>info.Size-info.Completed)throw new FileTransferFailure("invalid-offset");
       int done=0;while(done<n){int read=file.Read(buffer,done,(int)n-done);if(read==0)throw new FileTransferFailure("source-changed");done+=read;}
       FileTransferProtocol.Write(stream,"DATA "+offset+" "+n);stream.Write(buffer,0,(int)n);stream.Flush();info.Completed+=n;Notify(info);
      }
     }
    }else throw new FileTransferFailure("invalid-request");
    info.State="Complete";FileTransferProtocol.Write(stream,"DONE "+info.Size+" "+info.Sha256);
   }catch(Exception ex){if(info.State=="Complete"){info.Error="acknowledgement-lost";}else{info.Error=FileTransferProtocol.Failure(ex);info.State=info.Error=="cancelled"?"Cancelled":"Failed";try{FileTransferProtocol.Write(stream,"ERROR "+info.Error);}catch(IOException){}catch(ObjectDisposedException){}}}
   finally{lock(sync){if(active==stream)active=null;}Notify(info);}
  }
 }
 public void Dispose(){Stop();}
}

internal static class FileTransferClient
{
 static string[] Response(Stream stream){var p=FileTransferProtocol.Line(stream);if(p.Length==2&&p[0]=="ERROR")throw new FileTransferFailure(p[1]);return p;}
 internal static void Hello(Stream stream){var p=Response(stream);if(p.Length!=4||p[0]!="REMSUPFILE1"||p[1]!="READY"||p[2]!="65536"||p[3]!="1073741824")throw new FileTransferFailure("unsupported-transfer");}
 internal static void BeginUpload(Stream stream,string path,long size,string sha,bool overwrite){FileTransferProtocol.Validate(size,sha);FileTransferProtocol.Write(stream,"PUT "+RemsupProtocol.Encode(path)+" "+size+" "+sha+" "+(overwrite?"1":"0"));var p=Response(stream);if(p.Length!=2||p[0]!="ACK"||p[1]!="0")throw new FileTransferFailure("invalid-response");}
 internal static FileTransferInfo BeginDownload(Stream stream,string path){FileTransferProtocol.Write(stream,"GET "+RemsupProtocol.Encode(path));var p=Response(stream);if(p.Length!=3||p[0]!="META")throw new FileTransferFailure("invalid-response");var info=new FileTransferInfo{Size=FileTransferProtocol.Number(p[1]),Sha256=p[2]};FileTransferProtocol.Validate(info.Size,info.Sha256);return info;}
 internal static void UploadChunk(Stream stream,long offset,byte[] bytes,int length){if(length<1||length>FileTransferProtocol.ChunkBytes||length>bytes.Length)throw new FileTransferFailure("invalid-chunk");FileTransferProtocol.Write(stream,"DATA "+offset+" "+length);stream.Write(bytes,0,length);stream.Flush();var p=Response(stream);if(p.Length!=2||p[0]!="ACK"||FileTransferProtocol.Number(p[1])!=offset+length)throw new FileTransferFailure("invalid-response");}
 internal static byte[] DownloadChunk(Stream stream,long offset,int count){if(count<1||count>FileTransferProtocol.ChunkBytes)throw new FileTransferFailure("invalid-chunk");FileTransferProtocol.Write(stream,"READ "+offset+" "+count);var p=Response(stream);if(p.Length!=3||p[0]!="DATA"||FileTransferProtocol.Number(p[1])!=offset||FileTransferProtocol.Number(p[2])!=count)throw new FileTransferFailure("invalid-response");var bytes=new byte[count];FileTransferProtocol.Exact(stream,bytes,count);return bytes;}
 internal static FileTransferInfo Finish(Stream stream){FileTransferProtocol.Write(stream,"COMMIT");var p=Response(stream);if(p.Length!=3||p[0]!="DONE")throw new FileTransferFailure("invalid-response");var info=new FileTransferInfo{Size=FileTransferProtocol.Number(p[1]),Sha256=p[2],State="Complete"};FileTransferProtocol.Validate(info.Size,info.Sha256);return info;}
 internal static void Cancel(Stream stream){FileTransferProtocol.Write(stream,"CANCEL");var p=Response(stream);if(p.Length!=1||p[0]!="CANCELLED")throw new FileTransferFailure("invalid-response");}
}
