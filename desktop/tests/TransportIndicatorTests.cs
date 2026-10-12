using System;
internal static class TransportIndicatorTests {
 static void Expect(SupportState state,bool lan,bool relay,string expected,bool registered=false){var live=new SupportSnapshot(1,state,lan,relay,"Test state",registered);var lamp=TransportIndicator.From(live);if(lamp.color!=expected||string.IsNullOrWhiteSpace(lamp.label))throw new Exception(state+" -> "+lamp.color+" expected "+expected);}
 static void Main(){
  Expect(SupportState.Stopped,false,false,"red");Expect(SupportState.Stopped,true,true,"red",true);
  Expect(SupportState.Connecting,false,false,"red");Expect(SupportState.Reconnecting,false,false,"red");Expect(SupportState.Failed,false,false,"red");
  Expect(SupportState.Connecting,true,false,"yellow");Expect(SupportState.Reconnecting,true,false,"yellow");Expect(SupportState.Failed,true,false,"yellow");
  Expect(SupportState.RelayWaiting,false,false,"green");Expect(SupportState.SessionActive,false,true,"green");Expect(SupportState.SessionActive,true,false,"green",true);
  Expect(SupportState.SessionActive,true,false,"yellow");Expect(SupportState.SessionActive,false,false,"red");
  if(TransportIndicator.From(new SupportSnapshot(1,SupportState.RelayWaiting,true,false,"Ready"),true).color!="red")throw new Exception("Disposed/renderer-failed green");
  Console.WriteLine("PASS actual snapshot relay/LAN/stopped/connecting/paired/renderer-failed lamp mapping");
 }
}
