using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

internal sealed class FileWorkspace:IDisposable
{
 readonly object sync=new object();readonly FileTransferReceiver receiver;
 readonly Dictionary<string,CancellationTokenSource> operations=new();readonly Dictionary<string,FileTransferInfo> states=new();
 FileTransferInfo incoming=new();internal event Action Changed;
 internal FileWorkspace(Func<bool> allowed){receiver=new FileTransferReceiver(allowed,info=>{lock(sync)incoming=Copy(info);Changed?.Invoke();});}
 internal bool Busy {get{lock(sync)return operations.Count>0||incoming.State=="Receiving"||incoming.State=="Sending";}}
 static FileTransferInfo Copy(FileTransferInfo p)=>new(){Size=p.Size,Completed=p.Completed,Sha256=p.Sha256,State=p.State,Direction=p.Direction,Path=p.Path,Error=p.Error};
 static object View(FileTransferInfo p)=>new{state=p.State,bytes=p.Completed,size=p.Size,sha256=p.Sha256,direction=p.Direction,path=p.Path,error=p.Error};
 internal object Receiving {get{lock(sync)return View(incoming);}}
 internal object Snapshot(string key){lock(sync)return View(states.TryGetValue(key,out var state)?state:new FileTransferInfo());}
 internal void Serve(System.IO.Stream stream,CancellationToken token)=>receiver.Serve(stream,token);
 internal void StopReceiver()=>receiver.Stop();
 internal async Task Start(string key,RemoteSession session,string local,string remote,bool upload,bool overwrite){
  if(session.IsClosed||!session.HasCredential)throw new InvalidOperationException("Authenticate this target before transferring files.");
  CancellationTokenSource cancel;lock(sync){if(operations.ContainsKey(key)||operations.Count>=4)throw new InvalidOperationException("A file transfer is already active or capacity is full.");operations[key]=cancel=new();states[key]=new(){State="Connecting",Direction=upload?"Upload":"Download"};}Changed?.Invoke();
  // Endpoint and credential are captured from this authenticated session, never from renderer input.
  var endpoint=session.Endpoint;string password=session.Credential,id=session.Target.DeviceId;
  try{
   var result=await Task.Run(()=>FileTransferConnection.Transfer(endpoint.Host,endpoint.Port,id,password,endpoint.Pin,endpoint.IsLan,local,remote,upload,overwrite,cancel.Token,info=>{lock(sync)states[key]=Copy(info);Changed?.Invoke();}));
   result.Completed=result.Size;lock(sync)states[key]=Copy(result);
  }catch(Exception ex){string err=FileTransferProtocol.Failure(ex);lock(sync)states[key]=new(){State=err=="cancelled"?"Cancelled":"Failed",Error=err};}
  finally{password=null;lock(sync)operations.Remove(key);cancel.Dispose();Changed?.Invoke();}
 }
 internal void Cancel(string key){lock(sync){if(operations.TryGetValue(key,out var operation))operation.Cancel();}}
 internal void Close(string key){Cancel(key);lock(sync)if(!operations.ContainsKey(key))states.Remove(key);}
 public void Dispose(){receiver.Dispose();lock(sync)foreach(var operation in operations.Values)operation.Cancel();}
}
