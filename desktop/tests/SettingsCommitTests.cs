using System;
using System.IO;
using System.Threading.Tasks;
internal static class SettingsCommitTests {
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static async Task Main(string[] args){string root=args[0];Directory.CreateDirectory(root);string path=Path.Combine(root,"settings.txt");File.WriteAllText(path,"prior bytes");int applied=0,restored=0;
  try{await SettingsCommit.Apply(path,p=>throw new IOException("denied staging"),()=>{applied++;return Task.CompletedTask;},()=>{restored++;return Task.CompletedTask;});throw new Exception("failure expected");}catch(IOException){}
  Check(applied==0&&restored==0&&File.ReadAllText(path)=="prior bytes","stage failure mutated persisted or OS state");
  try{await SettingsCommit.Apply(path,p=>File.WriteAllText(p,"new bytes"),()=>{applied++;throw new IOException("startup denied");},()=>{restored++;return Task.CompletedTask;});throw new Exception("failure expected");}catch(IOException){}
  Check(applied==1&&restored==1&&File.ReadAllText(path)=="prior bytes","apply failure did not restore exact prior state");
  await SettingsCommit.Apply(path,p=>File.WriteAllText(p,"new bytes"),()=>{applied++;return Task.CompletedTask;},()=>{restored++;return Task.CompletedTask;});Check(File.ReadAllText(path)=="new bytes"&&restored==1,"success state");
  Check(Directory.GetFiles(root,"*.tmp").Length==0,"staging file left behind");Console.WriteLine("Settings staging failure / apply rollback / success PASS");
 }
}
