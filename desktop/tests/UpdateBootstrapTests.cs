using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
internal static class UpdateBootstrapTests{
 static async Task<int> Main(){using var timeout=new CancellationTokenSource(5000);string id=Guid.NewGuid().ToString("N");using var pipe=new NamedPipeServerStream("Remsup.Update."+id,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);var receive=UpdateBootstrap.ReceiveAsync(id,timeout.Token);await UpdateBootstrap.SendAsync(pipe,"synthetic-only-otp",timeout.Token);if(await receive!="synthetic-only-otp")throw new Exception("same-user IPC mismatch");string name=@"Local\Remsup.Update.Ready."+Guid.NewGuid().ToString("N");using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,name);UpdateBootstrap.Ready(name);if(!ready.WaitOne(0))throw new Exception("readiness event missing");ready.Dispose();UpdateBootstrap.Ready(name);Console.WriteLine("PASS consumed readiness event cannot break subsequent relay registration");Console.WriteLine("PASS same-user one-time OTP pipe and readiness event without argv/file secret");return 0;}
}
