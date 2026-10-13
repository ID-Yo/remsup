using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Text.RegularExpressions;
// Bounded operational states only: no command output, device IDs or input content.
internal sealed class RuntimeDiagnostics
{
 readonly object gate=new object();readonly string directory;string last="";UpdateDiagnostics updates=new UpdateDiagnostics();DateTime observed;
 internal RuntimeDiagnostics(string path){directory=path;Load();if(updates.last_check==null)LoadPriorCheck();}
 internal sealed class CheckInfo {public string utc{get;set;}public string result{get;set;}public string version{get;set;}public string error_code{get;set;}}
 internal sealed class DownloadInfo {public string utc{get;set;}public string version{get;set;}public string sha256{get;set;}}
 internal sealed class InstallInfo {public string utc{get;set;}public string result{get;set;}public string error_code{get;set;}}
 internal sealed class ErrorInfo {public string utc{get;set;}public string code{get;set;}}
 internal sealed class UpdateDiagnostics {public bool enabled{get;set;}public CheckInfo last_check{get;set;}public DownloadInfo last_download{get;set;}public InstallInfo last_install{get;set;}public List<ErrorInfo> latest_errors{get;set;}=new List<ErrorInfo>();}
 static string Version(string value)=>value!=null&&Regex.IsMatch(value,@"\Av1\.(?:0|[1-9][0-9]{0,5})(?:\.(?:0|[1-9][0-9]{0,5}))?\z")?value:"";
 static string Utc(string value)=>DateTimeOffset.TryParse(value,out var time)?time.ToUniversalTime().ToString("o"):"";
 static string Code(string value)=>value=="check-failed"||value=="download-failed"||value=="readiness-failed"||value=="install-failed"||value=="recovery-failed"||value=="registration-repair"||value=="diagnostics-unavailable"?value:"";
 static string CheckResult(string value)=>value=="Current"||value=="Available"||value=="Error"||value=="Disabled"?value:"Error";
 static string InstallResult(string value)=>value=="Installed"||value=="Deferred"||value=="RolledBack"||value=="RecoveryFailed"||value=="Failed"?value:"Failed";
 static UpdateDiagnostics Safe(UpdateDiagnostics value){
  var result=new UpdateDiagnostics();if(value==null)return result;
  if(value.last_check!=null)result.last_check=new CheckInfo{utc=Utc(value.last_check.utc),result=CheckResult(value.last_check.result),version=Version(value.last_check.version),error_code=Code(value.last_check.error_code)};
  if(value.last_download!=null)result.last_download=new DownloadInfo{utc=Utc(value.last_download.utc),version=Version(value.last_download.version),sha256=value.last_download.sha256!=null&&Regex.IsMatch(value.last_download.sha256,@"\A[0-9a-fA-F]{64}\z")?value.last_download.sha256.ToLowerInvariant():""};
  if(value.last_install!=null)result.last_install=new InstallInfo{utc=Utc(value.last_install.utc),result=InstallResult(value.last_install.result),error_code=Code(value.last_install.error_code)};
  foreach(var error in value.latest_errors??new List<ErrorInfo>())if(error!=null&&Code(error.code)!=""&&result.latest_errors.Count<8)result.latest_errors.Add(new ErrorInfo{utc=Utc(error.utc),code=Code(error.code)});
  return result;
 }
 void Load(){try{string path=Path.Combine(directory,"update-summary.json");if(File.Exists(path)){var time=File.GetLastWriteTimeUtc(path);if(time==observed)return;if(new FileInfo(path).Length>16384)throw new JsonException();updates=Safe(JsonSerializer.Deserialize<UpdateDiagnostics>(File.ReadAllText(path)));observed=time;}}catch(JsonException){}catch(IOException){}catch(UnauthorizedAccessException){}}
 void LoadPriorCheck(){ReadPriorCheck("state.jsonl.1");ReadPriorCheck("state.jsonl");}
 void ReadPriorCheck(string name){try{string path=Path.Combine(directory,name);if(!File.Exists(path)||new FileInfo(path).Length>270336)return;foreach(string line in File.ReadAllLines(path)){try{using var document=JsonDocument.Parse(line);var item=document.RootElement;if(item.GetProperty("event").GetString()!="update-check")continue;var data=item.GetProperty("data");updates.last_check=new CheckInfo{utc=Utc(item.GetProperty("utc").GetString()),result=CheckResult(data.GetProperty("result").GetString()),version=Version(data.GetProperty("version").GetString()),error_code=CheckResult(data.GetProperty("result").GetString())=="Error"?"check-failed":""};}catch(JsonException){}catch(InvalidOperationException){}catch(KeyNotFoundException){}}}catch(IOException){}catch(UnauthorizedAccessException){}}
 void Save(){try{Directory.CreateDirectory(directory);string path=Path.Combine(directory,"update-summary.json"),temp=path+".new";File.WriteAllText(temp,JsonSerializer.Serialize(updates));File.Move(temp,path,true);observed=File.GetLastWriteTimeUtc(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
 internal UpdateDiagnostics Updates(bool enabled){lock(gate){Load();var result=Safe(updates);result.enabled=enabled;return result;}}
 void Error(string code){if(code=="")return;updates.latest_errors.Add(new ErrorInfo{utc=DateTime.UtcNow.ToString("o"),code=code});while(updates.latest_errors.Count>8)updates.latest_errors.RemoveAt(0);}
 internal void Download(string version,string hash){lock(gate){updates.last_download=new DownloadInfo{utc=DateTime.UtcNow.ToString("o"),version=Version(version),sha256=hash!=null&&Regex.IsMatch(hash,@"\A[0-9a-fA-F]{64}\z")?hash.ToLowerInvariant():""};Save();Write("update-download",JsonSerializer.Serialize(updates.last_download));}}
 internal void Install(string result,string code){lock(gate){code=Code(code);updates.last_install=new InstallInfo{utc=DateTime.UtcNow.ToString("o"),result=InstallResult(result),error_code=code};Error(code);Save();Write("update-install",JsonSerializer.Serialize(updates.last_install));}}
 internal void SettingsEvent(string name,string result){lock(gate)Write("remote-settings",JsonSerializer.Serialize(new{setting=name,result}));}
 internal void Failure(string code){lock(gate){Error(Code(code));Save();Write("update-error",JsonSerializer.Serialize(new{code=Code(code)}));}}
 internal static string SafeLanWarning(string value){
  if(string.IsNullOrEmpty(value))return "";
  foreach(string known in new[]{"LAN IP is inactive. Refresh addresses and choose this computer's active private IPv4.","LAN port must be 1-65535.","The network is Public — change it to Private in Windows Settings before using LAN.","LAN requires a Windows Private network. Check the network profile in Windows Settings.","The scoped LAN firewall rule is missing. Select Prepare LAN firewall for this address and app.","LAN identity is unavailable. Restore this Windows user's protected identity; it was not regenerated.","LAN listener could not start. Check whether the address/port is already in use."})if(value==known)return known;
  return "LAN diagnostics unavailable.";
 }
 internal void State(SupportSnapshot state,bool lanEnabled,bool autoUpdates,string update,string secret){
  lock(gate){string value=JsonSerializer.Serialize(new{lan_enabled=lanEnabled,lan_ready=state.LanReady,lan_warning=SafeLanWarning(state.LanWarning),relay_state=state.State.ToString(),relay_ready=state.RelayReady,auto_updates=autoUpdates,update_result=updates.last_install?.result??updates.last_check?.result??""});if(value==last)return;last=value;Write("state",value);}
 }
 internal void UpdateCheck(string result,string version,string error,string secret){lock(gate){string code=string.IsNullOrEmpty(error)?"":"check-failed";updates.last_check=new CheckInfo{utc=DateTime.UtcNow.ToString("o"),result=CheckResult(result),version=Version(version),error_code=code};Error(code);Save();Write("update-check",JsonSerializer.Serialize(updates.last_check));}}
 void Write(string kind,string value){try{Directory.CreateDirectory(directory);string path=Path.Combine(directory,"state.jsonl");if(File.Exists(path)&&new FileInfo(path).Length>262144){string prior=path+".1";if(File.Exists(prior))File.Delete(prior);File.Move(path,prior);}File.AppendAllText(path,"{\"utc\":\""+DateTime.UtcNow.ToString("o")+"\",\"event\":\""+kind+"\",\"data\":"+value+"}\n");}catch(IOException){}catch(UnauthorizedAccessException){}}
}
