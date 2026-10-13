using System;
using System.IO;
internal sealed class HostOptions {
 internal string UpdateSecret=null; internal readonly string UpdatePipe,UpdateReady; internal readonly bool Client,Fixture,FixtureLiveRelay; internal readonly string DataRoot,Artifacts,RuntimeFolder,Scenario,FixtureScript,FixtureRelay;
 internal HostOptions(string[] args){
  Client=Array.IndexOf(args,"--client")>=0||Path.GetFileName(Environment.ProcessPath).StartsWith("RemsupClient_",StringComparison.OrdinalIgnoreCase);string Value(string name){int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
  UpdatePipe=Value("--update-pipe");UpdateReady=Value("--update-ready");
  string root=Value("--fixture-root");Fixture=root!=null;
  if(Fixture&&!Path.IsPathFullyQualified(root))throw new ArgumentException("Fixture root must be an absolute task-owned path.");
  DataRoot=Fixture?Path.GetFullPath(root):null;Artifacts=Value("--artifacts");Scenario=Value("--scenario")??"home";RuntimeFolder=Value("--runtime-folder");
  if(Artifacts!=null&&!Fixture)throw new ArgumentException("Evidence output requires isolated fixture root.");
  FixtureScript=Value("--fixture-script");if(FixtureScript!=null&&(!Fixture||Artifacts==null||!Path.IsPathFullyQualified(FixtureScript)))throw new ArgumentException("Review scripts require isolated fixture data, evidence output and an absolute script path.");
  FixtureRelay=Value("--fixture-relay");if(FixtureRelay!=null){if(!Fixture||!System.Text.RegularExpressions.Regex.IsMatch(FixtureRelay,@"^127\.0\.0\.1:[0-9]{1,5}$")||!int.TryParse(FixtureRelay.Substring(10),out int port)||port<1||port>65535)throw new ArgumentException("Support integration fixture requires an explicit IPv4 loopback relay and isolated root.");}
  FixtureLiveRelay=Array.IndexOf(args,"--fixture-live-relay")>=0;
  if(FixtureLiveRelay&&(!Fixture||FixtureRelay!=null))throw new ArgumentException("Live TLS review requires an isolated fixture root and cannot use a plaintext fixture relay.");
 }
 internal string SettingsPath=>Fixture?Path.Combine(DataRoot,"settings.txt"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemsupPortable","settings.txt");
 // A SystemAware browser cannot be shared with the PMv2 successor during update overlap.
 // UI state/credentials live outside WebView; keep the old renderer folder for rollback.
 internal string UserData=>Fixture?Path.Combine(DataRoot,"webview-pmv2"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Remsup","WebView-pmv2");
}
internal sealed class ProfilePayload {
 public string recordId {get;set;} public string displayName {get;set;} public string deviceId {get;set;} public string mode {get;set;} public string host {get;set;} public int port {get;set;} public string pin {get;set;} public string note {get;set;} public bool isFavorite {get;set;} public bool savePassword {get;set;}
}
internal sealed class SettingsPayload {
 public bool autoAcceptScreenSessions {get;set;}
 public bool allowScreenView {get;set;} public bool allowScreenControl {get;set;}
 public string relayHost {get;set;} public int relayPort {get;set;} public string relayPin {get;set;} public bool autoUpdates {get;set;} public bool autoStart {get;set;} public bool lanEnabled {get;set;} public string lanAddress {get;set;} public int lanPort {get;set;}
}
internal sealed class ConnectPayload {
 public string recordId {get;set;} public string deviceId {get;set;} public string mode {get;set;} public string host {get;set;} public int port {get;set;} public string pin {get;set;}
}
