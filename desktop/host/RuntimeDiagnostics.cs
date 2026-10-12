using System;
using System.IO;
using System.Text.Json;
// Bounded operational states only: no command output, device IDs or input content.
internal sealed class RuntimeDiagnostics
{
 readonly object gate=new object();readonly string directory;string last="";
 internal RuntimeDiagnostics(string path){directory=path;}
 string Clean(string text,string secret){text=text??"";if(!string.IsNullOrEmpty(secret))text=text.Replace(secret,"[REDACTED]");return text.Length>2048?text.Substring(0,2048):text;}
 internal void State(SupportSnapshot state,bool lanEnabled,bool autoUpdates,string update,string secret){
  lock(gate){string value=JsonSerializer.Serialize(new{lan_enabled=lanEnabled,lan_ready=state.LanReady,lan_warning=Clean(state.LanWarning,secret),relay_state=state.State.ToString(),relay_ready=state.RelayReady,auto_updates=autoUpdates,update_result=Clean(update,secret)});if(value==last)return;last=value;Write("state",value);}
 }
 internal void UpdateCheck(string result,string version,string error,string secret){lock(gate)Write("update-check",JsonSerializer.Serialize(new{result,version,error=Clean(error,secret)}));}
 void Write(string kind,string value){try{Directory.CreateDirectory(directory);string path=Path.Combine(directory,"state.jsonl");if(File.Exists(path)&&new FileInfo(path).Length>262144){string prior=path+".1";if(File.Exists(prior))File.Delete(prior);File.Move(path,prior);}File.AppendAllText(path,"{\"utc\":\""+DateTime.UtcNow.ToString("o")+"\",\"event\":\""+kind+"\",\"data\":"+value+"}\n");}catch(IOException){}catch(UnauthorizedAccessException){}}
}
