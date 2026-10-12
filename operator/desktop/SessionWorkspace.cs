using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
internal sealed class RemoteSession {
 internal readonly DeviceRecord Target;
 internal readonly string Identity;
 internal DeviceEndpoint Endpoint;
 internal string State="Last check succeeded at "+DateTime.Now.ToString("g");
 internal CommandResult Result;
 internal readonly List<SessionResult> History=new List<SessionResult>();
 internal bool IsBusy,IsClosed,LastAttemptFailed;
 internal string NavigationState {get{return IsBusy?"Running":LastAttemptFailed?"Failed":"Idle";}}
 internal int HistoryRevision;internal bool HistoryEvicted;
 internal CancellationTokenSource Operation;
 string credential;
 internal RemoteSession(DeviceRecord target,DeviceEndpoint endpoint,string identity,string password){Target=target.Clone();Target.PermanentPassword="";Endpoint=endpoint;Identity=identity;credential=password;}
 internal bool HasCredential {get{return !string.IsNullOrEmpty(credential);}}
 internal string Credential {get{return credential;}}
 internal void Clear(){credential=null;Target.PermanentPassword="";}
 internal void Record(string command,CommandResult result){Result=result;LastAttemptFailed=result.ExitCode!=0;History.Add(new SessionResult(command,result,null,Endpoint));TrimHistory();}
 internal void RecordFailure(string command,string outcome){Result=null;LastAttemptFailed=true;History.Add(new SessionResult(command,null,outcome,Endpoint));TrimHistory();}
 void TrimHistory(){HistoryRevision++;while(History.Count>1&&(History.Count>20||History.Sum(item=>item.Result==null?0:System.Text.Encoding.UTF8.GetByteCount((item.Result.Stdout??"")+(item.Result.Stderr??"")))>8*1024*1024)){History.RemoveAt(0);HistoryEvicted=true;}}
}
internal sealed class SessionResult {
 internal readonly string Command,Outcome;internal readonly CommandResult Result;internal readonly DeviceEndpoint Endpoint;internal readonly DateTime At=DateTime.Now;
 internal SessionResult(string command,CommandResult result,string outcome,DeviceEndpoint endpoint){Command=command;Result=result;Outcome=outcome;Endpoint=endpoint;}
 public override string ToString(){return At.ToString("HH:mm:ss")+" · "+Command+(Result==null?" · result unknown":" · exit "+Result.ExitCode);}
}
internal sealed class SessionWorkspace:IDisposable {
 internal delegate CommandResult CommandCall(DeviceEndpoint endpoint,DeviceRecord target,string password,string command,int seconds,int bytes,CancellationToken cancellation);
 readonly Func<RelayConfiguration> relay;readonly CommandCall execute;readonly Func<string,bool> confirm;
 readonly List<RemoteSession> sessions=new List<RemoteSession>();readonly HashSet<string> busy=new HashSet<string>();
 readonly List<CancellationTokenSource> probes=new List<CancellationTokenSource>();
 readonly Dictionary<string,Task<RemoteSession>> pending=new Dictionary<string,Task<RemoteSession>>();bool disposed;
 internal event Action Changed;
 internal IList<RemoteSession> Sessions {get{return sessions.AsReadOnly();}}
 internal bool HasPendingChecks {get{return probes.Count>0;}}
 internal SessionWorkspace(Func<RelayConfiguration> configuration,CommandCall call,Func<string,bool> confirmation){relay=configuration;execute=call;confirm=confirmation;}
 static string Key(DeviceRecord target,DeviceEndpoint endpoint){return DeviceRecord.NormalizeDeviceId(target.DeviceId)+"|"+target.Transport+"|"+endpoint.Host.ToLowerInvariant()+"|"+endpoint.Port+"|"+endpoint.Pin;}
 internal bool IsDeviceBusy(string id){return busy.Contains(DeviceRecord.NormalizeDeviceId(id));}
 internal Task<RemoteSession> ConnectAsync(DeviceRecord target,Func<string> getPassword){
  if(disposed)throw new ObjectDisposedException("SessionWorkspace");
  var recordSession=sessions.FirstOrDefault(s=>!s.IsClosed&&s.Target.RecordId==target.RecordId);if(recordSession!=null)return Task.FromResult(recordSession);
  var endpoint=DeviceEndpoint.Resolve(target,relay);string key=Key(target,endpoint);
  var existing=sessions.FirstOrDefault(s=>!s.IsClosed&&(s.Target.RecordId==target.RecordId||s.Identity==key));
  if(existing!=null)return Task.FromResult(existing);
  Task<RemoteSession> connecting;if(pending.TryGetValue(key,out connecting))return connecting;
  string id=DeviceRecord.NormalizeDeviceId(target.DeviceId);if(busy.Contains(id))throw new InvalidOperationException("A command/check is already running for this Device ID. Wait or cancel that operation.");
  if(!confirm(Preview(target,endpoint,"hostname & whoami",30,262144)))return Task.FromResult<RemoteSession>(null);
  string password=getPassword();if(string.IsNullOrEmpty(password))return Task.FromResult<RemoteSession>(null);
  RemsupProtocol.ValidateCredentials(target.DeviceId,password);
  var copy=target.Clone();copy.PermanentPassword="";var cancellation=new CancellationTokenSource();probes.Add(cancellation);busy.Add(id);Notify();
  connecting=CheckAsync(copy,endpoint,key,password,cancellation);pending[key]=connecting;return connecting;
 }
 async Task<RemoteSession> CheckAsync(DeviceRecord target,DeviceEndpoint endpoint,string key,string password,CancellationTokenSource cancel){
  try{
   var result=await Task.Run(()=>execute(endpoint,target,password,"hostname & whoami",30,262144,cancel.Token));
   cancel.Token.ThrowIfCancellationRequested();if(disposed)return null;
   if(result.ExitCode!=0)throw new InvalidOperationException("Target check failed (exit "+result.ExitCode+"). "+result.Stderr);
   if(string.IsNullOrWhiteSpace(result.Stdout))throw new InvalidOperationException("Target check returned no identity output; no checked tab was opened.");
   var session=new RemoteSession(target,endpoint,key,password);session.Record("hostname & whoami",result);sessions.Add(session);return session;
  }finally{password=null;busy.Remove(DeviceRecord.NormalizeDeviceId(target.DeviceId));probes.Remove(cancel);pending.Remove(key);cancel.Dispose();Notify();}
 }
 internal async Task RunAsync(RemoteSession session,string command,int seconds,int bytes){
  if(disposed||session.IsClosed||!session.HasCredential)return;
  if(string.IsNullOrWhiteSpace(command))throw new ArgumentException("Enter an authorized command.");
  if(seconds<1||seconds>300||bytes<1024||bytes>1048576)throw new ArgumentException("Limits: 1–300 seconds and 1024–1048576 bytes.");
  string id=DeviceRecord.NormalizeDeviceId(session.Target.DeviceId);if(busy.Contains(id))throw new InvalidOperationException("A command/check is already running for this Device ID.");
  var endpoint=DeviceEndpoint.Resolve(session.Target,relay);if(!confirm(Preview(session.Target,endpoint,command,seconds,bytes)))return;
  var cancel=new CancellationTokenSource();session.Operation=cancel;session.IsBusy=true;session.Endpoint=endpoint;session.State="Command running… Cancel requests disconnect; remote completion may be unknown.";busy.Add(id);Notify();
  string password=session.Credential;
  try{
   var result=await Task.Run(()=>execute(endpoint,session.Target.Clone(),password,command,seconds,bytes,cancel.Token));
   if(disposed||session.IsClosed)return;
   cancel.Token.ThrowIfCancellationRequested();
   session.Record(command,result);session.State="Completed · exit "+result.ExitCode+(result.Truncated?" · Output was shortened at the configured limit":"");
  }catch(Exception ex){if(!disposed&&!session.IsClosed){session.State=(cancel.IsCancellationRequested?"Cancelled: remote result may be unknown. ":"Connection ended: result may be unknown. ")+ex.Message;session.RecordFailure(command,session.State);}}
  finally{password=null;busy.Remove(id);session.Operation=null;session.IsBusy=false;cancel.Dispose();Notify();}
 }
 static string Preview(DeviceRecord target,DeviceEndpoint endpoint,string command,int seconds,int bytes){return "Target: "+target.DisplayName+"\nDevice ID: "+target.DeviceId+"\n"+target.Transport+" "+endpoint.Display+"\nCommand: "+command+"\nLimits: "+seconds+" seconds · "+bytes+" bytes\nOnly proceed with device-owner authorization.";}
 internal void Cancel(RemoteSession session){if(session.Operation!=null)session.Operation.Cancel();}
 internal void CancelChecks(){foreach(var probe in probes.ToArray())probe.Cancel();}
 internal void Close(RemoteSession session,bool allowActive){if(session.IsBusy&&!allowActive)throw new InvalidOperationException("Confirm cancel/disconnect before closing an active tab; stopping the remote process is not guaranteed.");session.IsClosed=true;Cancel(session);session.Clear();session.History.Clear();session.Result=null;sessions.Remove(session);Notify();}
 void Notify(){if(!disposed){var handler=Changed;if(handler!=null)handler();}}
 public void Dispose(){if(disposed)return;disposed=true;CancelChecks();foreach(var session in sessions.ToArray()){session.IsClosed=true;Cancel(session);session.Clear();session.History.Clear();session.Result=null;}sessions.Clear();}
}
