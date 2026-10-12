using System;
using System.Linq;
internal static class ConfirmationTests {
 static void Main(){var target=new DeviceRecord{DisplayName="Fixture",DeviceId="123456789",UseDefaultRelay=true};var session=new RemoteSession(target,new DeviceEndpoint("relay.test",8443),"fixture","synthetic");string preview="";using(var work=new SessionWorkspace(()=>new RelayConfiguration("relay.test",8443),(e,d,p,c,s,b,t)=>new CommandResult(),message=>{preview=message;return false;})){work.RunAsync(session,"echo once",17,2048).GetAwaiter().GetResult();if(!preview.Contains("17 seconds")||!preview.Contains("2048 bytes"))throw new Exception("Confirmed limits missing");}Console.WriteLine("PASS immutable confirmation includes timeout/output bounds");}
}
