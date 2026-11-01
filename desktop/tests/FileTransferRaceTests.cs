using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class FileTransferRaceTests
{
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string p,uint a,uint s,IntPtr sec,uint mode,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool DeviceIoControl(SafeFileHandle file,uint code,byte[] input,uint length,IntPtr output,uint capacity,out uint returned,IntPtr overlapped);
 static void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS "+text);}
 static byte[] JunctionData(string target){
  byte[] sub=Encoding.Unicode.GetBytes(@"\??\"+target),print=Encoding.Unicode.GetBytes(target);var data=new byte[16+sub.Length+2+print.Length+2];
  Array.Copy(BitConverter.GetBytes(0xa0000003u),0,data,0,4);Array.Copy(BitConverter.GetBytes((ushort)(data.Length-8)),0,data,4,2);Array.Copy(BitConverter.GetBytes((ushort)sub.Length),0,data,10,2);Array.Copy(BitConverter.GetBytes((ushort)(sub.Length+2)),0,data,12,2);Array.Copy(BitConverter.GetBytes((ushort)print.Length),0,data,14,2);Array.Copy(sub,0,data,16,sub.Length);Array.Copy(print,0,data,18+sub.Length,print.Length);
  return data;
 }
 static bool Junction(SafeFileHandle handle,string target){var data=JunctionData(target);return DeviceIoControl(handle,0x900a4,data,(uint)data.Length,IntPtr.Zero,0,out _,IntPtr.Zero);}
 static bool Junction(string parent,string target){
  using(var handle=CreateFile(parent,0x40000000,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero)){return !handle.IsInvalid&&Junction(handle,target);}
 }
 sealed class Script:MemoryStream{
  internal readonly MemoryStream Written=new();bool fail;
  internal Script(string data):base(Encoding.ASCII.GetBytes(data)){fail=true;}
  public override bool CanTimeout=>true;public override int ReadTimeout{get;set;}=20000;public override int WriteTimeout{get;set;}=20000;
  public override void Write(byte[] b,int o,int n){if(fail&&Encoding.ASCII.GetString(b,o,n).StartsWith("DONE ")){fail=false;throw new IOException("synthetic lost DONE");}Written.Write(b,o,n);}
  public override void Flush(){}
 }
 internal static void Run(string root){
  string parent=Path.Combine(root,"empty-ancestor"),foreign=Path.Combine(root,"foreign-marker");Directory.CreateDirectory(parent);Directory.CreateDirectory(foreign);File.WriteAllText(Path.Combine(foreign,"file"),"FOREIGN-SYNTHETIC");
  bool redirected=false;try{using(var binding=new FileTransferPaths(Path.Combine(parent,"file"))){redirected=Junction(parent,foreign);if(redirected){bool refused=false;try{using(var input=binding.OpenSource(Path.Combine(parent,"file"))){} }catch(FileTransferFailure ex){refused=ex.Code=="reparse-path";}Check(refused,"in-place ancestor junction mutation refused before object-relative source open");}else Check(true,"in-place ancestor junction mutation prevented");}}finally{if(redirected)Directory.Delete(parent,false);}
  Directory.CreateDirectory(parent);redirected=false;try{using(var binding=new FileTransferPaths(Path.Combine(parent,"file"))){binding.BeforeFileOpen=()=>redirected=Junction(parent,foreign);bool refused=false;try{using(var source=binding.OpenSource(Path.Combine(parent,"file")))using(var text=new StreamReader(source)){Check(text.ReadToEnd()!="FOREIGN-SYNTHETIC","object-relative root never follows a junction changed AFTER final validation");}}catch(FileTransferFailure){refused=true;}Check(refused,"post-validation junction race cannot redirect opened source");}}finally{if(redirected)Directory.Delete(parent,false);}
  Directory.CreateDirectory(parent);redirected=false;try{
   using(var mutator=CreateFile(parent,0x40000000,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero))using(var binding=new FileTransferPaths(Path.Combine(parent,"file"))){
    Check(!mutator.IsInvalid,"owned mutator handle predates directory binding");binding.BeforePartialCreate=()=>redirected=Junction(mutator,foreign);bool refused=false;
    try{using(var partial=binding.CreatePartial(Path.Combine(parent,"owned.partial"))){FileTransferPaths.DeleteOwned(partial.SafeFileHandle);}}catch(FileTransferFailure){refused=true;}
    Check(refused&&!File.Exists(Path.Combine(foreign,"owned.partial")),"preexisting mutator at partial-create boundary cannot redirect or delete foreign file");
   }
  }finally{if(redirected)Directory.Delete(parent,false);}
  Directory.CreateDirectory(parent);redirected=false;try{using(var binding=new FileTransferPaths(Path.Combine(parent,"final.bin")))using(var partial=binding.CreatePartial(Path.Combine(parent,"owned.partial"))){
   binding.BeforeRename=()=>redirected=Junction(parent,foreign);bool refused=false;try{binding.RenameOwned(partial.SafeFileHandle,Path.Combine(parent,"final.bin"),false);}catch(FileTransferFailure){refused=true;FileTransferPaths.DeleteOwned(partial.SafeFileHandle);}
   Check(refused||!File.Exists(Path.Combine(foreign,"final.bin")),"post-validation rename mutation never places file in foreign directory");
  }}finally{if(redirected)Directory.Delete(parent,false);}
  Directory.CreateDirectory(parent);redirected=false;int steps=0;try{bool refused=false;try{using(var binding=new FileTransferPaths(Path.Combine(parent,"child","file"),()=>{if(++steps==4)redirected=Junction(parent,foreign);})){} }catch(FileTransferFailure){refused=true;}Check(refused,"ancestor mutation before child descent refuses without foreign traversal");}finally{if(redirected)Directory.Delete(parent,false);}
  string sha=FileTransferProtocol.Hash(new MemoryStream()),lost=Path.Combine(root,"lost-done.bin");FileTransferInfo result=null;
  using(var stream=new Script("PUT "+RemsupProtocol.Encode(lost)+" 0 "+sha+" 0\nCOMMIT\n"))using(var receiver=new FileTransferReceiver(()=>true,info=>result=info)){
   receiver.Serve(stream,CancellationToken.None);Check(File.Exists(lost)&&result.State=="Complete"&&result.Error=="acknowledgement-lost"&&!Encoding.ASCII.GetString(stream.Written.ToArray()).Contains("ERROR interrupted"),"lost DONE retains committed placement and truthful terminal state");
  }
  string local=Path.Combine(root,"local-cancel.bin");File.WriteAllText(local,"KEEP-ORIGINAL");
  using(var cancel=new CancellationTokenSource())using(var gate=new FileTransferConnection.CommitGate(cancel.Token))using(var partial=new FileTransferPartial(local,0,sha,true)){
   partial.Prepare();cancel.Cancel();bool refused=false;try{gate.Place(partial);}catch(FileTransferFailure ex){refused=ex.Code=="cancelled";}Check(refused&&File.ReadAllText(local)=="KEEP-ORIGINAL","local cancellation after DONE/Prepare wins serialized placement and preserves destination");
  }
  using(var cancel=new CancellationTokenSource())using(var gate=new FileTransferConnection.CommitGate(cancel.Token))using(var partial=new FileTransferPartial(local,0,sha,true)){partial.Prepare();gate.Place(partial);cancel.Cancel();Check(new FileInfo(local).Length==0,"local placement winner remains committed after late cancel");}
 }
}
