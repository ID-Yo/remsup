using System;
using System.IO;
using System.Threading.Tasks;
internal static class SettingsCommit {
 // Stage in the destination directory before any OS or receiving-state mutation.
 internal static async Task Apply(string path,Action<string> stage,Func<Task> apply,Func<Task> rollback){
  string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";byte[] previous=File.Exists(path)?File.ReadAllBytes(path):null;bool replaced=false,attempted=false;
  try{stage(temporary);File.Move(temporary,path,true);replaced=true;attempted=true;await apply();}
  catch(Exception failure){
   try{if(replaced){if(previous==null)File.Delete(path);else{File.WriteAllBytes(temporary,previous);File.Move(temporary,path,true);}}if(attempted)await rollback();}
   catch(Exception restore){throw new AggregateException("Settings could not be applied or fully restored. Receiving state needs review.",failure,restore);}
   throw;
  }finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
}
