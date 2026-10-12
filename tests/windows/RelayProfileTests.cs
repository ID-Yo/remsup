using System;
using System.IO;
internal static class RelayProfileTests {
 static readonly string TestPin=RelayTls.DefaultPin;
 public static void Run(){
  var root=Path.Combine(Path.GetTempPath(),"RemsupRelayProfiles-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
   CheckOwnerDefaults(root);
   var global=new RelayConfiguration("relay.example.test",9443,TestPin);
   var inherited=new DeviceRecord{DisplayName="Default device",DeviceId="123456789",UseDefaultRelay=true,Host="obsolete.example.test",Port=8443};
   var endpoint=DeviceEndpoint.Resolve(inherited,()=>global);
   Check(endpoint.Host=="relay.example.test"&&endpoint.Port==9443,"default inherits host and port");
   global=new RelayConfiguration("changed.example.test",10443,TestPin);
   Check(endpoint.Host=="relay.example.test"&&endpoint.Port==9443,"confirmed endpoint stays immutable");
   Check(DeviceEndpoint.Resolve(inherited,()=>global).Port==10443,"next attempt sees global change");
   var custom=new DeviceRecord{DisplayName="Custom",DeviceId="987654321",Host="custom.example.test",Port=9444,Pin=RelayTls.DefaultPin};
   Check(DeviceEndpoint.Resolve(custom,()=>{throw new Exception("must not read global");}).Host==custom.Host,"custom independent");
   custom.Transport=DeviceTransport.Lan;custom.Host="192.168.1.25";custom.Port=8444;
   Check(DeviceEndpoint.Resolve(custom,()=>null).Host==custom.Host,"LAN independent");
   custom.ValidateAndNormalize();Check(custom.Pin==TestPin,"LAN receiver pin survives profile normalization");custom.Pin="";custom.ValidateAndNormalize();Fail(()=>DeviceEndpoint.Resolve(custom,()=>null),"pin","legacy LAN profile is editable but missing pin refuses connection");custom.Pin=TestPin;
   foreach(var bad in new[]{new RelayConfiguration("",0,TestPin),new RelayConfiguration("http://relay.example.test",443,TestPin),new RelayConfiguration("host",0,TestPin),new RelayConfiguration("bad host",443,TestPin)})
    Fail(()=>DeviceEndpoint.Resolve(inherited,()=>bad),"Settings", "invalid global fails actionably");
   Check(DeviceEndpoint.Resolve(new DeviceRecord{Host="123456789",Port=8443,Pin=TestPin},()=>null).Host=="123456789","numeric hostname compatibility");
   foreach(string value in new[]{"workstation","relay.example.test","192.0.2.10","[::1]"})Check(!string.IsNullOrEmpty(DeviceEndpoint.Resolve(new DeviceRecord{Host=value,Port=9443,Pin=RelayTls.DefaultPin},()=>null).Host),"hostname/IP/bracketed address input");
   Fail(()=>DeviceEndpoint.Resolve(new DeviceRecord{Host="custom.example.test",Port=9443},()=>null),"pin","custom relay without pin fails closed");
   var parsed=RelayConfiguration.Parse(new[]{"RelayHost=relay.example.test","RelayPort=not-a-port","PermanentPassword=synthetic"});
   Fail(()=>DeviceEndpoint.Resolve(inherited,()=>parsed),"Settings","bad configured port not silently defaulted");
   var path=Path.Combine(root,"devices.json");var store=new DeviceStore(path);var data=store.Load();data.Upsert(inherited);store.Save(data);
   var raw=File.ReadAllText(path);Check(raw.Contains("\"version\":2")&&!raw.Contains("obsolete")&&!raw.Contains("changed.example"),"default stores mode, no inherited snapshot");
   var restored=store.Load().Records[0];Check(restored.UseDefaultRelay&&restored.Host==""&&restored.Port==0,"default reopen retains inheritance");
   var legacy=new DeviceRecord{DisplayName="Legacy",DeviceId="111222333",Host="111222333",Port=9443,SavePermanentPassword=true,PermanentPassword="synthetic-legacy"};
   var legacyPath=Path.Combine(root,"legacy.json");var legacyStore=new DeviceStore(legacyPath);var legacyData=legacyStore.Load();legacyData.Upsert(legacy);legacyStore.Save(legacyData);
   var loaded=legacyStore.Load();Check(!loaded.Records[0].UseDefaultRelay&&loaded.Records[0].PermanentPassword=="synthetic-legacy","custom DPAPI roundtrip");
   var current=File.ReadAllText(legacyPath);var old=current.Replace("\"version\":2","\"version\":1").Replace(",\"relayMode\":\"Custom\"", "");File.WriteAllText(legacyPath,old);
   Fail(()=>legacyStore.Load(),"unsupported","explicit old-file format error");Fail(()=>legacyStore.Save(loaded),"no data was changed","old format blocks save");Check(File.ReadAllText(legacyPath)==old,"old format preserved without migration");
   var missingMode=current.Replace(",\"relayMode\":\"Custom\"", "");File.WriteAllText(legacyPath,missingMode);
   Fail(()=>legacyStore.Load(),"no data was changed","current format still requires a mode");Fail(()=>legacyStore.Save(loaded),"no data was changed","missing mode blocks save");Check(File.ReadAllText(legacyPath)==missingMode,"missing-mode vault preserved");
   var badMode=current.Replace("\"relayMode\":\"Custom\"","\"relayMode\":\"Unknown\"");File.WriteAllText(legacyPath,badMode);
   Fail(()=>legacyStore.Load(),"no data was changed","unknown mode blocks load");Fail(()=>legacyStore.Save(loaded),"no data was changed","unknown mode blocks save");Check(File.ReadAllText(legacyPath)==badMode,"failed save preserves vault");
   Console.WriteLine("PASS inherited/custom/LAN immutable endpoints and current vault schema");
  }finally{Directory.Delete(root,true);}
 }
 static void CheckOwnerDefaults(string root){
  foreach(var lines in new[]{new string[0],new[]{"PermanentPassword=synthetic"},new[]{"RelayHost=","RelayPort=443"},new[]{"RelayHost=   ","RelayPort="}}){
   var relay=RelayConfiguration.Parse(lines);Check(relay.Host=="relay.example.invalid"&&relay.Port==443&&relay.Pin==RelayTls.DefaultPin,"fresh/missing/blank relay uses configured relay default");
  }
  var migrated=RelayConfiguration.Parse(new[]{"RelayHost=relay.example.invalid","RelayPort=8443"});Check(migrated.Port==443&&migrated.Pin==RelayTls.DefaultPin,"saved owner endpoint migrates to TLS 443 and default pin");
  var changed=RelayConfiguration.Parse(new[]{"RelayHost=custom.example.test","RelayPort=10443"});Check(changed.Host=="custom.example.test"&&changed.Port==10443,"explicit global host/custom port preserved");
  changed=RelayConfiguration.Parse(new[]{"RelayHost=","RelayPort=10443"});Check(changed.Host=="relay.example.invalid"&&changed.Port==10443,"blank host preserves explicit valid port");
  changed=RelayConfiguration.Parse(new[]{"RelayHost=custom.example.test"});Check(changed.Host=="custom.example.test"&&changed.Port==443,"missing port gets TLS default");
  changed=RelayConfiguration.Parse(new[]{"RelayHost=custom.example.test","RelayPin="});Check(changed.Host=="custom.example.test"&&changed.Pin=="","custom relay does not inherit owner pin");
  changed=RelayConfiguration.Parse(new[]{"RelayPort=broken"});Check(changed.Host=="relay.example.invalid"&&changed.Port==0,"malformed explicit port never replaced");
  var path=Path.Combine(root,"missing-settings.txt");var settings=SupportActions.LoadSettings(path);Check(settings.RelayHost=="relay.example.invalid"&&settings.RelayPort==443&&settings.RelayPin==RelayTls.DefaultPin&&!File.Exists(path),"fresh receiving/settings values without disk mutation");
  foreach(var lines in new[]{new[]{"RelayHost=","RelayPort=443"},new[]{"RelayHost=custom.example.test","RelayPort=10443","RelayPin="+RelayTls.DefaultPin},new[]{"RelayHost=custom.example.test","RelayPort="}}){
   File.WriteAllLines(path,lines);var before=File.ReadAllBytes(path);settings=SupportActions.LoadSettings(path);var relay=RelayConfiguration.Parse(lines);
   Check(settings.RelayHost==relay.Host&&settings.RelayPort==relay.Port,"receiving and profile share effective policy");Check(Convert.ToBase64String(before)==Convert.ToBase64String(File.ReadAllBytes(path)),"effective defaults do not write settings");
  }
  settings=SupportActions.LoadSettings(Path.Combine(root,"never-created.txt"));using(var dialog=new RemsupSettingsForm(settings)){
   var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
   Check(((System.Windows.Forms.TextBox)typeof(RemsupSettingsForm).GetField("relayHostBox",flags).GetValue(dialog)).Text=="relay.example.invalid","Settings displays effective default host");
   Check(((System.Windows.Forms.TextBox)typeof(RemsupSettingsForm).GetField("relayPortBox",flags).GetValue(dialog)).Text=="443","Settings displays effective default port");
   Check(((System.Windows.Forms.TextBox)typeof(RemsupSettingsForm).GetField("relayHostBox",flags).GetValue(dialog)).Enabled&&!((System.Windows.Forms.TextBox)typeof(RemsupSettingsForm).GetField("relayHostBox",flags).GetValue(dialog)).ReadOnly,"effective relay remains editable");
  }
 }
 static void Check(bool ok,string message){if(!ok)throw new Exception("RelayProfileTests: "+message);}
 static void Fail(Action action,string text,string message){try{action();}catch(Exception ex){if(text!=null)Check(ex.Message.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0,message+" text");return;}throw new Exception("Expected failure: "+message);}
}
