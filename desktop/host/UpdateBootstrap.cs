using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// A fresh same-user pipe carries the live receiving OTP; it is never put in argv or files.
internal static class UpdateBootstrap {
 internal static async Task<string> ReceiveAsync(string pipe,CancellationToken token){
  if(string.IsNullOrEmpty(pipe))return null;
  if(!Guid.TryParseExact(pipe,"N",out _))throw new ArgumentException("Invalid update handoff pipe.");
  using var client=new NamedPipeClientStream(".","Remsup.Update."+pipe,PipeDirection.In,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
  await client.ConnectAsync(10000,token);
  using var reader=new StreamReader(client,Encoding.UTF8,false,1024,true);
  var secret=await reader.ReadLineAsync(token);
  if(secret==null||secret.Length<1||secret.Length>128)throw new IOException("Update handoff unavailable.");
  return secret;
 }
 internal static async Task SendAsync(NamedPipeServerStream pipe,string secret,CancellationToken token){
  await pipe.WaitForConnectionAsync(token);
  using var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};
  await writer.WriteLineAsync(secret.AsMemory(),token);
 }
 internal static void Ready(string name){
  if(string.IsNullOrEmpty(name))return;
  if(!name.StartsWith(@"Local\Remsup.Update.Ready.",StringComparison.Ordinal))throw new ArgumentException("Invalid update readiness event.");
  try{using var ready=EventWaitHandle.OpenExisting(name);ready.Set();}catch(WaitHandleCannotBeOpenedException){}
 }
}
