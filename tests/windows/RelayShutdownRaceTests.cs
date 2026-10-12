using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
internal static class RelayShutdownRaceTests {
 public static void Run(){
  int port;var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
  using(var host=new SupportHost())using(var stopped=new ManualResetEvent(false)){
   host.Changed+=state=>{if(state.Description=="Session ended; reconnecting."){host.Stop();stopped.Set();}};
   host.StartFixture("127.0.0.1:"+port,"123456789","fixture-password");
   if(!stopped.WaitOne(5000))throw new Exception("Relay shutdown race fixture did not reach reconnect notification");
   Thread.Sleep(300);if(host.Snapshot.State!=SupportState.Stopped)throw new Exception("Stopped host reconnected");
  }
  Console.WriteLine("PASS Stop during reconnect notification does not crash disposed-token worker");
 }
 public static int Main(){Run();return 0;}
}
