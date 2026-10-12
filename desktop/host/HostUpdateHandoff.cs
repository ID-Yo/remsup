using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

internal sealed class HostUpdateHandoff:IUpdateHandoff {
 internal static ApplicationInstance Instance=null;
 readonly Func<bool> suspend;readonly Func<Task<bool>> resume;readonly Action complete;readonly Func<string> secret;readonly bool client;readonly Dispatcher dispatcher;
 internal HostUpdateHandoff(Func<bool> suspend,Func<Task<bool>> resume,Action complete,Func<string> secret,bool client){this.suspend=suspend;this.resume=resume;this.complete=complete;this.secret=secret;this.client=client;dispatcher=Dispatcher.CurrentDispatcher;}
 public async Task<bool> TrySuspendIfIdleAsync(CancellationToken token){token.ThrowIfCancellationRequested();return await dispatcher.InvokeAsync(()=>{if(!suspend())return false;try{Instance?.ReleaseForUpdate();return true;}catch{_=resume();throw;}});}
 public async Task<bool> StartCandidateAndWaitForReadyAsync(string exePath,string readyEventName,TimeSpan timeout,CancellationToken token){
  string pipeId=Guid.NewGuid().ToString("N");using var pipe=new NamedPipeServerStream("Remsup.Update."+pipeId,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
  using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,readyEventName);using var bounded=CancellationTokenSource.CreateLinkedTokenSource(token);bounded.CancelAfter(timeout);
  var start=new ProcessStartInfo(exePath){UseShellExecute=false};if(client)start.ArgumentList.Add("--client");start.ArgumentList.Add("--update-pipe");start.ArgumentList.Add(pipeId);start.ArgumentList.Add("--update-ready");start.ArgumentList.Add(readyEventName);
  string receiving=await dispatcher.InvokeAsync(()=>secret());using var process=Process.Start(start);bool accepted=false;
  try{
   await UpdateBootstrap.SendAsync(pipe,receiving,bounded.Token);receiving=null;
   while(!bounded.IsCancellationRequested){if(process.HasExited)throw new InvalidOperationException("Update candidate exited before relay readiness (exit code "+process.ExitCode+").");if(ready.WaitOne(0)){accepted=true;return true;}await Task.Delay(100,bounded.Token);}return false;
  }catch(OperationCanceledException){return false;}
  finally{receiving=null;if(!accepted&&!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
 }
 public async Task<bool> ResumeOldRegistrationsAsync(CancellationToken token){token.ThrowIfCancellationRequested();await dispatcher.InvokeAsync(()=>Instance?.ReacquireAfterUpdate());return await (await dispatcher.InvokeAsync(()=>resume()));}
 public async Task CompleteOldProcessAsync(CancellationToken token){token.ThrowIfCancellationRequested();await dispatcher.InvokeAsync(()=>complete());}
}
