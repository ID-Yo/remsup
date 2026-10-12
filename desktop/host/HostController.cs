using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
internal sealed class HostController:IDisposable {
 readonly HostOptions options;readonly IHostDialogs dialogs;readonly DeviceStore store;DeviceStoreSnapshot profiles;readonly SessionWorkspace workspace;readonly SupportHost support=new SupportHost();
 readonly Dictionary<string,string> draftSecrets=new Dictionary<string,string>();readonly Dictionary<RemoteSession,string> sessionIds=new Dictionary<RemoteSession,string>();
 readonly ScreenWorkspace screens;string ownLanPin="",lanIdentityError="";
 readonly CancellationTokenSource updateCancellation=new CancellationTokenSource();string updateStatus="";
 RemsupSettings settings,settingsDraft;string receivingSecret="",error="",checkStatus="";string confirmationTitle="Run authorized command";bool disposed,started;int lifecycle;readonly object startLock=new object();
 UpdateManager updater;VerifiedUpdate pendingUpdate;bool updateBusy,updateHandoffActive,exitAfterUpdate,updateReadySignaled;
 readonly UserInstallation installation=new UserInstallation();readonly RuntimeDiagnostics diagnostics;
 internal bool Dark;internal event Action Changed;internal event Action NoticesRequested;internal event Action CloseRequested;internal event Action MinimizeRequested;internal event Action UpdateSettled;internal bool UpdateHandoffActive=>updateHandoffActive;
 internal static readonly JsonSerializerOptions Json=new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 internal HostController(HostOptions opts,IHostDialogs ui,SessionWorkspace.CommandCall command=null){
  options=opts;dialogs=ui;diagnostics=new RuntimeDiagnostics(options.Fixture?Path.Combine(options.DataRoot,"logs"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Remsup","logs"));if(options.Fixture)Directory.CreateDirectory(options.DataRoot);
  screens=new ScreenWorkspace(options.Fixture);
  try{ownLanPin=options.Fixture?RelayTls.DefaultPin:LanTls.OwnPin;}catch(Exception ex) when(ex is System.Security.Cryptography.CryptographicException||ex is IOException||ex is UnauthorizedAccessException){lanIdentityError="LAN identity is unavailable. Restore this Windows user’s protected identity before using LAN; it was not regenerated.";}screens.Changed+=Notify;support.ScreenHandler=screens.Serve;
  store=options.Fixture?new DeviceStore(DeviceStore.ResolveStorePath(Path.Combine(options.DataRoot,"vault"))):new DeviceStore();
  try{profiles=store.Load();}catch(Exception ex){error="Saved devices unavailable: "+ex.Message;}
  settings=Load();settingsDraft=Clone(settings);screens.Enable(settings.AllowScreenView,settings.AllowScreenControl,settings.AutoAcceptScreenSessions);Dark=File.Exists(ThemePath)&&File.ReadAllText(ThemePath).Trim()=="dark";
  workspace=new SessionWorkspace(()=>new RelayConfiguration(settings.RelayHost,settings.RelayPort,settings.RelayPin),(e,d,p,c,s,b,t)=>command!=null?command(e,d,p,c,s,b,t):options.Fixture?FixtureCommand(e,d,c,t):OperatorConnection.Execute(e.Host,e.Port,d.DeviceId,p,c,s,b,t,null,e.Pin,e.IsLan),text=>options.Fixture||dialogs.Confirm(confirmationTitle,text));
  workspace.Changed+=Notify;support.RelayRegistrationsReady+=()=>{if(options.UpdateReady!=null&&!updateReadySignaled){updateReadySignaled=true;UpdateBootstrap.Ready(options.UpdateReady);}};
  support.Changed+=s=>{if(s.State==SupportState.Stopped)screens.StopReceiver();Notify();};
  if(options.Fixture&&profiles!=null&&profiles.Records.Count==0){foreach(var item in new[]{("Studio workstation","123456789"),("Support laptop","222333444"),("Office computer","333444555")}){var d=new DeviceRecord{DisplayName=item.Item1,DeviceId=item.Item2,UseDefaultRelay=true,IsFavorite=item.Item1=="Studio workstation"};profiles.Upsert(d);}store.Save(profiles);}
 }
 string ThemePath=>options.Fixture?Path.Combine(options.DataRoot,"theme.txt"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Remsup","theme.txt");
 RemsupSettings Load(){return SupportActions.LoadSettings(options.SettingsPath,true);}
 static RemsupSettings Clone(RemsupSettings s)=>new RemsupSettings{PermanentPassword=s.PermanentPassword,RelayHost=s.RelayHost,RelayPort=s.RelayPort,RelayPin=s.RelayPin,AutoUpdates=s.AutoUpdates,AutoStartEnabled=s.AutoStartEnabled,LanEnabled=s.LanEnabled,LanAddress=s.LanAddress,LanPort=s.LanPort,AllowScreenView=s.AllowScreenView,AllowScreenControl=s.AllowScreenControl,AutoAcceptScreenSessions=s.AutoAcceptScreenSessions};
 internal string OwnId=>options.Fixture?"111222333":DeviceRecord.NormalizeDeviceId(SupportActions.GetStatus().DeviceId);
 internal string[] Addresses=>options.Fixture?new[]{"192.168.1.25","10.8.0.7"}:SupportHost.LocalIPv4Addresses().ToArray();
 internal object Snapshot(){
  var live=support.Snapshot;var lamp=TransportIndicator.From(live,disposed);
  return new {installed=!options.Fixture&&installation.IsInstalled(Environment.ProcessPath),installPath=installation.Executable,version=HelpContent.Version,fixture=options.Fixture,client=options.Client,dark=Dark,ownId=OwnId,addresses=Addresses,hasReceivingPassword=receivingSecret.Length>0,receiving=options.Fixture&&options.FixtureRelay==null?"Fixture · receiving stopped":live.Description,receivingState=options.Fixture&&options.FixtureRelay==null?"Stopped":live.State.ToString(),transport=lamp,ownLanPin,lanIdentityError,lanWarning=live.LanWarning,help=new{version=HelpContent.Version,mcpConfig=HelpContent.McpConfig,tunnel=HelpContent.Tunnel,probe=HelpContent.Probe,scope=HelpContent.Scope,report=HelpContent.Report(options.Client,live)},error,checkStatus,updateStatus,checking=workspace.HasPendingChecks,
   screenReceiving=screens.Receiving,settings=new{relayHost=settingsDraft.RelayHost,relayPort=settingsDraft.RelayPort,relayPin=settingsDraft.RelayPin,autoUpdates=settingsDraft.AutoUpdates,autoStart=settingsDraft.AutoStartEnabled,lanEnabled=settingsDraft.LanEnabled,lanAddress=settingsDraft.LanAddress,lanPort=settingsDraft.LanPort,hasPassword=settingsDraft.PermanentPassword.Length>0,allowScreenView=settingsDraft.AllowScreenView,allowScreenControl=settingsDraft.AllowScreenControl,autoAcceptScreenSessions=settingsDraft.AutoAcceptScreenSessions},
   profiles=profiles?.Records.Select(d=>new{recordId=d.RecordId,displayName=d.DisplayName,deviceId=d.DeviceId,mode=d.Transport==DeviceTransport.Lan?"LAN":d.UseDefaultRelay?"Default":"Custom",host=d.Host,port=d.Port,pin=d.Pin,note=d.Note,isFavorite=d.IsFavorite,lastCheck=d.LastSuccessfulConnectionUtc,hasPassword=d.PermanentPassword.Length>0,savePassword=d.SavePermanentPassword}).ToArray(),
   sessions=workspace.Sessions.Select(s=>new{id=Id(s),name=s.Target.DisplayName,deviceId=s.Target.DeviceId,mode=s.Target.Transport.ToString(),endpoint=s.Endpoint.Display,state=!s.IsBusy&&s.History.Count>0&&s.History.Last().Result==null?"Unknown":s.NavigationState,operation=s.State,busy=s.IsBusy,evicted=s.HistoryEvicted,screen=screens.Snapshot(Id(s)),
    history=s.History.Select(h=>new{id=h.At.Ticks.ToString(),command=h.Command,at=h.At,endpoint=h.Endpoint.Display,exit=h.Result==null?(int?)null:h.Result.ExitCode,duration=h.Result==null?0:h.Result.DurationMs,truncated=h.Result!=null&&h.Result.Truncated,unknown=h.Result==null}).ToArray()}).ToArray()};
 }
 static string Preview(string text)=>text==null?"":text.Length>65536?text.Substring(0,65536):text;
 string Id(RemoteSession session){string id;if(!sessionIds.TryGetValue(session,out id)){id=Guid.NewGuid().ToString("D");sessionIds[session]=id;}return id;}
 RemoteSession Session(JsonElement p){string id=Text(p,"sessionId");return workspace.Sessions.FirstOrDefault(s=>Id(s)==id)??throw new InvalidOperationException("Session is closed or no longer exists.");}
 static string Text(JsonElement p,string key){if(!p.TryGetProperty(key,out var value)||value.ValueKind!=JsonValueKind.String)throw new ArgumentException("Missing "+key);return value.GetString();}
 static T Read<T>(JsonElement p)=>JsonSerializer.Deserialize<T>(p.GetRawText(),Json);
 void Notify(){if(disposed)return;if(settings!=null)diagnostics.State(support.Snapshot,settings.LanEnabled,settings.AutoUpdates,updateStatus.Replace(receivingSecret.Length>0?receivingSecret:"\u0000","[REDACTED]"),settings.PermanentPassword);Changed?.Invoke();}
 internal async Task ReadyAsync(){if(started)return;started=true;if(!options.Fixture||options.FixtureRelay!=null)await StartAsync();if(!options.Fixture){_=WatchUpdatesAsync();_=OfferLanCleanupAsync();}}
 async Task StartAsync(){
  if(options.Fixture){if(options.FixtureRelay==null)return;int fixtureVersion=Interlocked.Increment(ref lifecycle);await Task.Run(()=>{lock(startLock){if(disposed||fixtureVersion!=lifecycle)return;IPAddress fixtureAddress=null;if(settings.LanEnabled&&!IPAddress.TryParse(settings.LanAddress,out fixtureAddress))fixtureAddress=IPAddress.Any;support.StartFixture(options.FixtureRelay,OwnId,"fixture-password",fixtureAddress,settings.LanPort);}});if(!disposed&&fixtureVersion==lifecycle)receivingSecret="fixture-password";Notify();return;}
  int version=Interlocked.Increment(ref lifecycle);var current=Clone(settings);var relay=new RelayConfiguration(current.RelayHost,current.RelayPort,current.RelayPin).Validated();var secret=current.PermanentPassword.Length>0?current.PermanentPassword:options.UpdateSecret??(receivingSecret.Length>0?receivingSecret:SupportActions.GeneratePassword());
  IPAddress address=null;if(current.LanEnabled&&!IPAddress.TryParse(current.LanAddress,out address))address=IPAddress.Any;
  await Task.Run(()=>{lock(startLock){if(disposed||version!=lifecycle)return;support.Start(relay.Display,OwnId,secret,address,current.LanPort,relay.Pin);}});
  if(!disposed&&version==lifecycle){receivingSecret=secret;options.UpdateSecret=null;}Notify();
 }
 internal async Task<object> HandleAsync(string kind,JsonElement p){
  if(disposed)throw new ObjectDisposedException("HostController");ValidatePayload(kind,p);if(updateHandoffActive&&(kind=="session.run"||kind=="session.connect"||kind=="screen.request"||kind=="settings.save"||kind=="installation.uninstall"))throw new InvalidOperationException("Update replacement in progress; wait for registration or rollback.");error="";
  switch(kind){
   case "ready":await ReadyAsync();break;
   case "snapshot":break;
   case "update.check":await CheckUpdateAsync(true);break;
   case "screen.stop":screens.StopReceiver();break;
   case "screen.request":{if(!settings.AllowScreenView)throw new InvalidOperationException("Screen viewing is disabled in Settings.");if(p.GetProperty("control").GetBoolean()&&!settings.AllowScreenControl)throw new InvalidOperationException("Screen control is disabled in Settings.");var screenSession=Session(p);string password=screenSession.Target.PermanentPassword.Length>0?screenSession.Target.PermanentPassword:options.Fixture?"fixture-password":dialogs.Credential("Screen pairing password for "+screenSession.Target.DisplayName,null);if(password==null)break;await screens.Request(Id(screenSession),screenSession.Endpoint,screenSession.Target.DeviceId,password,p.GetProperty("monitorId").GetString(),p.GetProperty("control").GetBoolean());break;}
   case "screen.status":case "screen.capture":await screens.Operate(Id(Session(p)),kind.Substring(7));break;
   case "screen.mouse":await screens.Operate(Id(Session(p)),"mouse",Text(p,"action"),p.GetProperty("x").GetInt32(),p.GetProperty("y").GetInt32(),p.GetProperty("scrollSteps").GetInt32(),Text(p,"frameId"),p.GetProperty("geometryVersion").GetInt32());break;
   case "screen.text":case "screen.key":await screens.Operate(Id(Session(p)),kind.Substring(7),frameId:Text(p,"frameId"),geometryVersion:p.GetProperty("geometryVersion").GetInt32(),input:Text(p,kind.Substring(7)));break;
   case "screen.close":screens.Close(Id(Session(p)));break;
   case "screen.fixture.consent":if(!options.Fixture)throw new ArgumentException("Synthetic fixture only.");screens.ApproveFixture(p.GetProperty("control").GetBoolean());break;
   case "screen.fixture.paint":{string name=Text(p,"name");if(!options.Fixture||options.Artifacts==null||!System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-zA-Z0-9_-]{1,40}$"))throw new ArgumentException("Synthetic own-window evidence only.");screens.CaptureConsentFixture(Path.Combine(options.Artifacts,name+".png"));break;}
   case "licenses":NoticesRequested?.Invoke();break;
   case "help.copy":dialogs.Copy(HelpContent.Template(Text(p,"template"),options.Client,support.Snapshot));break;
   case "help.releases":if(!options.Fixture)System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HelpContent.Releases){UseShellExecute=true});return new{url=HelpContent.Releases,opened=!options.Fixture};
   case "window.close":CloseRequested?.Invoke();break;
   case "window.minimize":MinimizeRequested?.Invoke();break;
   case "theme":Dark=!Dark;Directory.CreateDirectory(Path.GetDirectoryName(ThemePath));File.WriteAllText(ThemePath,Dark?"dark":"light");break;
   case "identity.copy":{string field=Text(p,"field");if(field!="id"&&field!="password"&&field!="lanPin")throw new ArgumentException("Unknown clipboard field.");dialogs.Copy(field=="id"?OwnId:field=="lanPin"?ownLanPin:receivingSecret);break;}
   case "identity.reveal":dialogs.Reveal("This computer's receiving password",receivingSecret);break;
   case "diagnostics":dialogs.Error((options.Fixture?"Synthetic fixture":"Observed local state")+"\n"+support.Snapshot.Description+"\nRelay connections require pinned TLS."+" Direct LAN requires the verified receiver SPKI pin."+" No customer/VPS availability claim.");break;
   case "settings.secret":{var value=dialogs.Credential("Receiving password",settingsDraft.PermanentPassword);if(value!=null){if(value.Length>0)RemsupProtocol.ValidateCredentials(OwnId,value);settingsDraft.PermanentPassword=value;}break;}
   case "settings.clear":settingsDraft.PermanentPassword="";break;
   case "installation.uninstall":{if(options.Fixture)throw new InvalidOperationException("Fixture does not uninstall the real application.");if(workspace.Sessions.Any(x=>x.IsBusy)||workspace.HasPendingChecks||new[]{"Pending","Granted"}.Contains(JsonSerializer.SerializeToElement(screens.Receiving).GetProperty("state").GetString())||workspace.Sessions.Any(x=>new[]{"Connecting","Pending","Granted"}.Contains(JsonSerializer.SerializeToElement(screens.Snapshot(Id(x))).GetProperty("state").GetString())))throw new InvalidOperationException("Finish active support sessions before uninstalling Remsup.");var remove=dialogs.Uninstall();if(remove!=null){UninstallManager.Schedule(installation,remove.Value);CloseRequested?.Invoke();}break;}
   case "settings.discard":settings=Load();settingsDraft=Clone(settings);break;
   case "settings.save":{var value=Read<SettingsPayload>(p);var relay=new RelayConfiguration(string.IsNullOrWhiteSpace(value.relayHost)?RelayConfiguration.DefaultHost:value.relayHost,value.relayPort,value.relayPin).Validated();if(value.lanPort<1||value.lanPort>65535)throw new ArgumentException("LAN port must be1-65535.");
    if(updateBusy)throw new InvalidOperationException("Wait for the update check to finish before saving settings.");InstallChoice installChoice=null;if(value.autoStart&&!options.Fixture&&!installation.IsInstalled(Environment.ProcessPath)){installChoice=dialogs.Install(installation.DirectoryPath);if(installChoice==null)throw new InvalidOperationException("Installation cancelled; settings were not saved.");installChoice.Client=options.Client;}
    var copy=Clone(settingsDraft);copy.RelayHost=relay.Host;copy.RelayPort=relay.Port;copy.RelayPin=relay.Pin;copy.AutoUpdates=value.autoUpdates;copy.AutoStartEnabled=value.autoStart;copy.LanEnabled=value.lanEnabled;copy.LanAddress=value.lanAddress??"";copy.LanPort=value.lanPort;copy.AllowScreenView=value.allowScreenView;copy.AllowScreenControl=value.allowScreenControl;copy.AutoAcceptScreenSessions=value.autoAcceptScreenSessions;
    const string runKey=@"Software\Microsoft\Windows\CurrentVersion\Run";const string runName="Remsup Support";object previousRun=null;Microsoft.Win32.RegistryValueKind previousKind=Microsoft.Win32.RegistryValueKind.String;
    if(!options.Fixture){using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey);previousRun=key?.GetValue(runName,null,Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);if(previousRun!=null)previousKind=key.GetValueKind(runName);}
    var prior=Clone(settings);bool restart=prior.RelayHost!=copy.RelayHost||prior.RelayPort!=copy.RelayPort||prior.RelayPin!=copy.RelayPin||prior.PermanentPassword!=copy.PermanentPassword||prior.LanEnabled!=copy.LanEnabled||(copy.LanEnabled&&(prior.LanAddress!=copy.LanAddress||prior.LanPort!=copy.LanPort));
    await SettingsCommit.Apply(options.SettingsPath,path=>SupportActions.SaveSettings(copy,path),async()=>{if(!options.Fixture){using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey);if(copy.AutoStartEnabled)key.SetValue(runName,"\""+Environment.ProcessPath+"\""+(options.Client?" --client":""));else key.DeleteValue(runName,false);}settings=copy;if(installChoice!=null){var result=await InstallCopyAsync(installChoice);if(result.Status!=UpdateInstallStatus.Installed)throw new InvalidOperationException(result.Error);}if(installChoice==null&&restart&&started&&(!options.Fixture||options.FixtureRelay!=null))await StartAsync();},async()=>{settings=prior;if(!options.Fixture){using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey);if(previousRun==null)key.DeleteValue(runName,false);else key.SetValue(runName,previousRun,previousKind);if(started)await StartAsync();}});
    settingsDraft=Clone(settings);screens.Enable(settings.AllowScreenView,settings.AllowScreenControl,settings.AutoAcceptScreenSessions);break;}
   case "lan.prepare":case "lan.remove":await FirewallAsync(p,kind=="lan.remove");break;
   case "profile.secret":{string id=Text(p,"recordId");if(!Guid.TryParse(id,out _))throw new ArgumentException("Invalid record ID.");var prior=profiles?.Find(id);var value=options.Fixture?"fixture-password":dialogs.Credential("Remote device permanent password",draftSecrets.ContainsKey(id)?draftSecrets[id]:prior?.PermanentPassword);if(value!=null){if(value.Length>0)RemsupProtocol.ValidateCredentials("123456789",value);draftSecrets[id]=value;}Notify();return new {hasPassword=draftSecrets.ContainsKey(id)?draftSecrets[id].Length>0:prior?.PermanentPassword.Length>0};}
   case "profile.clear":{string id=Text(p,"recordId");if(!Guid.TryParse(id,out _))throw new ArgumentException("Invalid secret context.");draftSecrets[id]="";break;}
   case "profile.discard":draftSecrets.Remove(Text(p,"recordId"));break;
   case "error.clear":error="";break;
   case "profile.save":{EnsureProfiles();var value=Read<ProfilePayload>(p);var prior=profiles.Find(value.recordId);var d=Make(value.mode,value.deviceId,value.host,value.port,value.pin);d.RecordId=value.recordId;d.DisplayName=value.displayName;d.Note=value.note??"";d.IsFavorite=value.isFavorite;d.LastSuccessfulConnectionUtc=prior?.LastSuccessfulConnectionUtc;
    d.PermanentPassword=draftSecrets.TryGetValue(d.RecordId,out var staged)?staged:prior?.PermanentPassword??"";d.SavePermanentPassword=value.savePassword;d.ValidateAndNormalize();profiles.Upsert(d);store.Save(profiles);draftSecrets.Remove(d.RecordId);break;}
   case "profile.remove":EnsureProfiles();if(dialogs.Confirm("Remove saved device","Remove this local profile only? Remote computer is unchanged.")){profiles.Remove(Text(p,"recordId"));store.Save(profiles);}break;
   case "profile.favorite":EnsureProfiles();var favorite=profiles.Find(Text(p,"recordId"))??throw new ArgumentException("Record missing.");favorite.IsFavorite=!favorite.IsFavorite;profiles.Upsert(favorite);store.Save(profiles);break;
   case "session.connect":{var value=Read<ConnectPayload>(p);DeviceRecord d;if(!string.IsNullOrEmpty(value.recordId)){EnsureProfiles();d=profiles.Find(value.recordId)??throw new ArgumentException("Saved profile missing.");}else{d=Make(value.mode,value.deviceId,value.host,value.port,value.pin);d.DisplayName="Quick "+d.DeviceId;}
    confirmationTitle="Connect to device";checkStatus="Checking "+d.DisplayName;Notify();try{var connected=await workspace.ConnectAsync(d,()=>d.PermanentPassword.Length>0?d.PermanentPassword:options.Fixture?"fixture-password":dialogs.Credential("Password for "+d.DisplayName,null));checkStatus=connected==null?"Check cancelled":"Last check succeeded";if(connected!=null&&profiles?.Find(d.RecordId)!=null){d.LastSuccessfulConnectionUtc=DateTime.UtcNow;profiles.Upsert(d);store.Save(profiles);}Notify();return new{sessionId=connected==null?null:Id(connected)};}catch(Exception ex){checkStatus="Check failed: "+ex.Message;Notify();throw;} }
   case "check.cancel":workspace.CancelChecks();break;
   case "session.run":confirmationTitle="Run authorized command";var s=Session(p);await workspace.RunAsync(s,Text(p,"command"),p.GetProperty("seconds").GetInt32(),p.GetProperty("bytes").GetInt32());break;
   case "session.cancel":workspace.Cancel(Session(p));break;
   case "session.output":{var session=Session(p);var attempt=session.History.FirstOrDefault(h=>h.At.Ticks.ToString()==Text(p,"attemptId"))??throw new InvalidOperationException("Result is no longer retained.");int offset=p.GetProperty("offset").GetInt32();string output=attempt.Result==null?attempt.Outcome:(attempt.Result.Stdout??"")+(string.IsNullOrEmpty(attempt.Result.Stderr)?"":"\n"+attempt.Result.Stderr);output=output??"";if(offset<0||offset>output.Length)throw new ArgumentException("Invalid output page.");return new{sessionId=Id(session),attemptId=attempt.At.Ticks.ToString(),offset,total=output.Length,text=output.Substring(offset,Math.Min(32768,output.Length-offset))};}
   case "session.close":var closing=Session(p);if(closing.IsBusy&&!dialogs.Confirm("Close active session","Cancel/disconnect and close? Remote process may keep running; outcome unknown; no automatic retry. History in this tab will be discarded."))break;screens.Close(Id(closing));workspace.Close(closing,true);sessionIds.Remove(closing);break;
   default:throw new ArgumentException("Unknown action.");
  }
  Notify();return Snapshot();
 }
 void EnsureProfiles(){if(profiles==null)throw new InvalidOperationException("Current vault unavailable; file preserved. No reset or migration.");}
 static void ValidatePayload(string kind,JsonElement p){
  if(p.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Payload must be an object.");
  string fields=kind=="help.copy"?"template":kind=="identity.copy"?"field":kind.StartsWith("profile.")&&kind!="profile.save"?"recordId":kind=="profile.save"?"recordId,displayName,deviceId,mode,host,port,pin,note,isFavorite,savePassword":kind=="session.connect"?"recordId,deviceId,mode,host,port,pin":kind=="session.run"?"sessionId,command,seconds,bytes":kind=="session.output"?"sessionId,attemptId,offset":kind.StartsWith("session.")?"sessionId":kind=="settings.save"?"relayHost,relayPort,relayPin,autoUpdates,autoStart,lanEnabled,lanAddress,lanPort,allowScreenView,allowScreenControl,autoAcceptScreenSessions":kind.StartsWith("lan.")?"address,port":"";
  if(kind.StartsWith("screen."))fields=kind=="screen.stop"?"":kind=="screen.fixture.consent"?"control":kind=="screen.fixture.paint"?"name":kind=="screen.request"?"sessionId,monitorId,control":kind=="screen.text"?"sessionId,frameId,geometryVersion,text":kind=="screen.key"?"sessionId,frameId,geometryVersion,key":kind=="screen.mouse"?"sessionId,action,x,y,scrollSteps,frameId,geometryVersion":"sessionId";
  var allowed=fields.Split(',');var found=new HashSet<string>();foreach(var field in p.EnumerateObject()){if(!allowed.Contains(field.Name)||!found.Add(field.Name))throw new ArgumentException("Unknown or duplicate payload field.");}
 }
 static DeviceRecord Make(string mode,string id,string host,int port,string pin){if(mode!="Default"&&mode!="Custom"&&mode!="LAN")throw new ArgumentException("Invalid connection mode.");var d=new DeviceRecord{DeviceId=DeviceRecord.NormalizeDeviceId(id),Transport=mode=="LAN"?DeviceTransport.Lan:DeviceTransport.Relay,UseDefaultRelay=mode=="Default",Host=mode=="Default"?"":host??"",Port=mode=="Default"?0:port,Pin=mode!="Default"?pin??"":""};d.DisplayName="Quick "+d.DeviceId;d.ValidateAndNormalize();return d;}
 async Task OfferLanCleanupAsync(){
  try{
   await Task.Delay(3000,updateCancellation.Token);if(disposed||!LanAccessPolicy.HasObsoleteRules())return;
   if(!dialogs.Confirm("Old Remsup LAN rules","Remove firewall rules for previous Remsup executable paths? The current app's rules are kept. Windows will show UAC."))return;
   await RunLanHelperAsync("--lan-cleanup");support.RefreshLan();Notify();
  }catch(OperationCanceledException){}catch(Exception){checkStatus="Old LAN rules remain. Use Prepare LAN firewall or Remove LAN rule in Network settings and approve UAC.";Notify();}
 }
 async Task RunLanHelperAsync(string arguments){
  var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath){UseShellExecute=true,Verb="runas",Arguments=arguments};
  await Task.Run(()=>{using var process=System.Diagnostics.Process.Start(start);if(!process.WaitForExit(30000))throw new InvalidOperationException("LAN helper still running; outcome unconfirmed.");if(process.ExitCode!=0)throw new InvalidOperationException("LAN helper failed.");});
 }
 async Task FirewallAsync(JsonElement p,bool remove){
  if(options.Fixture)throw new InvalidOperationException("Firewall operations are disabled in fixture mode.");
  string address=Text(p,"address");int port=p.GetProperty("port").GetInt32();IPAddress ip=null;if(!remove&&(!IPAddress.TryParse(address,out ip)||!SupportHost.LocalPrivateAddress(ip)||port<1||port>65535))throw new ArgumentException("Choose an active private local IPv4 and valid port.");
  if(!dialogs.Confirm("Scoped LAN firewall",(remove?"Remove all Remsup LAN firewall rules":"Prepare this app's Private/local-subnet rule and remove rules for old Remsup paths")+"? Windows will show UAC. No other draft settings are applied."))return;
  await RunLanHelperAsync(remove?"--lan-remove-all":"--lan-prepare "+ip+" "+port);support.RefreshLan();Notify();
 }
 static CommandResult FixtureCommand(DeviceEndpoint e,DeviceRecord d,string command,CancellationToken token){int waits=command.Contains("slow")?200:15;for(int i=0;i<waits;i++){token.ThrowIfCancellationRequested();Thread.Sleep(30);}if(command.Contains("timeout"))throw new TimeoutException("Synthetic timeout: remote result unknown.");return new CommandResult{Stdout=command.Contains("large")?"<script>synthetic plain text</script>\n"+new string('L',1048000):command=="hostname & whoami"?"DEMO-PC\nDEMO-PC\\operator\n":"Synthetic local fixture output\n"+command+"\n"+e.Display,Stderr=command.Contains("fail")?"Synthetic nonzero result":"",ExitCode=command.Contains("fail")?1:0,DurationMs=450,Truncated=command.Contains("truncate")};}
 internal static string FriendlyError(Exception failure){string text=failure.Message;if(failure is ArgumentException){int parameter=text.IndexOf("(Parameter",StringComparison.Ordinal);if(parameter>0)text=text.Substring(0,parameter).TrimEnd();}return text;}
 internal void Fail(string message){error=message;Notify();}

 internal bool RequestUserExit(){if(!updateHandoffActive)return true;exitAfterUpdate=true;updateStatus="Closing after the update restores or verifies relay registration.";Notify();return false;}
 bool SuspendForUpdate(){if(!support.TrySuspendRelayForUpdate(()=>new[]{"Pending","Granted"}.Contains(JsonSerializer.SerializeToElement(screens.Receiving).GetProperty("state").GetString())))return false;updateHandoffActive=true;return true;}
 async Task WatchUpdatesAsync(){
  try{await Task.Delay(5000,updateCancellation.Token);while(!disposed){if(settings.AutoUpdates)await CheckUpdateAsync(false);await Task.Delay(TimeSpan.FromHours(6),updateCancellation.Token);}}catch(OperationCanceledException){}
 }
 async Task CheckUpdateAsync(bool manual){
  if(options.Fixture){updateStatus="Synthetic fixture: update network disabled.";Notify();return;}if(updateBusy){updateStatus="Update check already running.";Notify();return;}updateBusy=true;
  try{
   updater??=new UpdateManager(RelayTls.CreatePinnedHttpClient(UpdateManager.UpdateHost,UpdateManager.UpdatePin),"v"+typeof(HostController).Assembly.GetName().Version.Major+"."+typeof(HostController).Assembly.GetName().Version.Minor,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Remsup","Updates"));
   var check=await updater.CheckAsync(updateCancellation.Token);diagnostics.UpdateCheck(check.Status.ToString(),check.Version,check.Error,settings.PermanentPassword.Length>0?settings.PermanentPassword:receivingSecret);updateStatus=check.Status==UpdateCheckStatus.Current?"You have the current version.":check.Status==UpdateCheckStatus.Available?"Update available: "+check.Version:check.Error;Notify();
   if(check.Status!=UpdateCheckStatus.Available||!settings.AutoUpdates)return;
   if(pendingUpdate!=null&&pendingUpdate.Version!=check.Version)pendingUpdate=null;
   pendingUpdate??=await updater.DownloadAsync(check.Release,updateCancellation.Token);
   var handoff=CreateUpdateHandoff();
   InstallChoice choice=pendingInstallChoice;if(!installation.IsInstalled(Environment.ProcessPath)&&!portableInstallOffered&&!File.Exists(PortableDeclinePath)){portableInstallOffered=true;choice=dialogs.Install(installation.DirectoryPath);if(choice!=null){choice.Client=options.Client;pendingInstallChoice=choice;}else{Directory.CreateDirectory(Path.GetDirectoryName(PortableDeclinePath));File.WriteAllText(PortableDeclinePath,"Declined installation; update current file in place.");}}
   var result=await updater.InstallAsync(pendingUpdate,Environment.ProcessPath,handoff,updateCancellation.Token,choice!=null?installation.Executable:null,false,()=>{if(choice!=null)installation.Register(choice,pendingUpdate.Version,settings.AutoStartEnabled);else if(installation.IsInstalled(Environment.ProcessPath))installation.Register(installation.ReadChoice()??new InstallChoice{Client=options.Client},pendingUpdate.Version,settings.AutoStartEnabled);});updateStatus=result.Status==UpdateInstallStatus.Deferred?"Update downloaded; waiting for active support to finish.":result.Status.ToString()+(string.IsNullOrEmpty(result.Error)?"":" · "+result.Error);if(result.Status==UpdateInstallStatus.Deferred)_=RetryDownloadedUpdateAsync();else {pendingUpdate=null;pendingInstallChoice=null;}Notify();

  }catch(OperationCanceledException){}catch(Exception){updateStatus="Update failed; current version retained. Check the pinned release endpoint.";Notify();}finally{updateBusy=false;updateHandoffActive=false;UpdateSettled?.Invoke();if(exitAfterUpdate){exitAfterUpdate=false;CloseRequested?.Invoke();}}
 }
 InstallChoice pendingInstallChoice;
 string PortableDeclinePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Remsup","portable-install-declined.txt");
 bool portableInstallOffered;
 HostUpdateHandoff CreateUpdateHandoff()=>new HostUpdateHandoff(()=>!workspace.Sessions.Any(x=>x.IsBusy)&&!workspace.HasPendingChecks&&!new[]{"Pending","Granted"}.Contains(JsonSerializer.SerializeToElement(screens.Receiving).GetProperty("state").GetString())&&!workspace.Sessions.Any(x=>new[]{"Connecting","Pending","Granted"}.Contains(JsonSerializer.SerializeToElement(screens.Snapshot(Id(x))).GetProperty("state").GetString()))&&SuspendForUpdate(),async()=>{support.ResumeRelayAfterUpdate();var watch=System.Diagnostics.Stopwatch.StartNew();while(watch.Elapsed.TotalSeconds<60){if(support.RelayRegistrationReady)return true;await Task.Delay(100);}return false;},()=>{updateHandoffActive=false;CloseRequested?.Invoke();},()=>receivingSecret,options.Client);
 async Task<UpdateInstallResult> InstallCopyAsync(InstallChoice choice){
  using var manager=new UpdateManager(new System.Net.Http.HttpClient(),"v"+typeof(HostController).Assembly.GetName().Version.Major+"."+typeof(HostController).Assembly.GetName().Version.Minor,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Remsup","Updates"));
  string hash;using(var stream=File.OpenRead(Environment.ProcessPath))hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
  updateBusy=true;
  try{while(true){var result=await manager.InstallAsync(new VerifiedUpdate(Environment.ProcessPath,"v"+typeof(HostController).Assembly.GetName().Version.Major+"."+typeof(HostController).Assembly.GetName().Version.Minor,hash),Environment.ProcessPath,CreateUpdateHandoff(),updateCancellation.Token,installation.Executable,true,()=>installation.Register(choice,"v"+typeof(HostController).Assembly.GetName().Version.Major+"."+typeof(HostController).Assembly.GetName().Version.Minor,settings.AutoStartEnabled));if(result.Status!=UpdateInstallStatus.Deferred)return result;updateStatus="Installation is waiting for active support to finish.";Notify();await Task.Delay(10000,updateCancellation.Token);}}finally{updateBusy=false;updateHandoffActive=false;UpdateSettled?.Invoke();}
 }
 async Task RetryDownloadedUpdateAsync(){try{await Task.Delay(10000,updateCancellation.Token);if(settings.AutoUpdates&&pendingUpdate!=null)await CheckUpdateAsync(false);}catch(OperationCanceledException){}}
 internal void StopScreens(){screens.StopReceiver();}
 public void Dispose(){updateCancellation.Cancel();updater?.Dispose();if(disposed)return;disposed=true;Interlocked.Increment(ref lifecycle);screens.Dispose();workspace.Dispose();lock(startLock)support.Dispose();receivingSecret="";settingsDraft.PermanentPassword="";settings.PermanentPassword="";draftSecrets.Clear();sessionIds.Clear();}
}
