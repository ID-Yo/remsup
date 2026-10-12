using System;
internal static class BridgePolicyTests {
 static void Check(bool v,string name){if(!v)throw new Exception(name);}
 static void Main(){
  Check(!BridgePolicy.Authorize("https://evil.example/index.html","snapshot",false,50),"reject foreign origin");
  Check(!BridgePolicy.Authorize("https://remsup.local.evil/index.html","snapshot",false,50),"reject lookalike origin");
  Check(!BridgePolicy.Authorize(BridgePolicy.Document,"shell",false,50),"no local shell proxy");
  Check(!BridgePolicy.Authorize(BridgePolicy.Document,"eval",false,50),"no eval proxy");
  Check(!BridgePolicy.Authorize(BridgePolicy.Document,"profile.save",true,50),"client role denies operator profiles");
  Check(!BridgePolicy.Authorize(BridgePolicy.Document,"session.run",true,50),"client role denies remote runs");
  Check(!BridgePolicy.Authorize(BridgePolicy.Document,"snapshot",false,65537),"bounded payload");
  Check(BridgePolicy.Authorize(BridgePolicy.Document,"snapshot",true,100),"client snapshot allowed");
  Check(BridgePolicy.Authorize(BridgePolicy.Document,"session.run",false,100),"explicit bounded remote action allowed");
  Check(BridgePolicy.Authorize(BridgePolicy.Document,"help.copy",true,100)&&BridgePolicy.Authorize(BridgePolicy.Document,"help.releases",true,100),"client help allowed");Check(!BridgePolicy.Authorize("https://evil.example/index.html","help.copy",false,100),"help foreign origin denied");
  Console.WriteLine("Bridge origin/capability/payload negative cases PASS");
 }
}
