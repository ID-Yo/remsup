using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Resolve each component relative to its checked parent object. Pathname/junction
// mutation cannot redirect the subsequent relative open, rename or owned cleanup.
internal sealed class FileTransferPaths:IDisposable
{
 readonly List<SafeFileHandle> ancestors=new List<SafeFileHandle>();
 internal Action BeforeFileOpen;
 internal Action BeforePartialCreate,BeforeRename;
 [StructLayout(LayoutKind.Sequential)]struct Info{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint mode,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle file,out Info info);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetFileInformationByHandle(SafeFileHandle file,int kind,IntPtr info,uint length);
 [StructLayout(LayoutKind.Sequential)]struct UnicodeName{public ushort Length,Maximum;public IntPtr Buffer;}
 [StructLayout(LayoutKind.Sequential)]struct Attributes{public int Length;public IntPtr Root,Name;public uint Flags;public IntPtr Security,Qos;}
 [StructLayout(LayoutKind.Sequential)]struct IoStatus{public IntPtr Status,Information;}
 [DllImport("ntdll.dll")]static extern int NtCreateFile(out SafeFileHandle handle,uint access,ref Attributes attributes,out IoStatus status,IntPtr allocation,uint fileAttributes,uint share,uint disposition,uint options,IntPtr ea,uint eaLength);
 [DllImport("ntdll.dll")]static extern int NtSetInformationFile(SafeFileHandle file,out IoStatus status,IntPtr info,uint bytes,int kind);
 static SafeFileHandle Relative(SafeFileHandle parent,string name,uint access,uint share,uint disposition,bool directory){
  if(string.IsNullOrEmpty(name)||name.IndexOfAny(new[]{'\\','/',':'})>=0||name=="."||name=="..")throw new FileTransferFailure("invalid-path");
  IntPtr chars=Marshal.StringToHGlobalUni(name),unicode=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeName)));
  try{var value=new UnicodeName{Length=checked((ushort)(name.Length*2)),Maximum=checked((ushort)(name.Length*2+2)),Buffer=chars};Marshal.StructureToPtr(value,unicode,false);var attributes=new Attributes{Length=Marshal.SizeOf(typeof(Attributes)),Root=parent.DangerousGetHandle(),Name=unicode,Flags=0x40};
   SafeFileHandle handle;IoStatus io;int result=NtCreateFile(out handle,access|0x100000,ref attributes,out io,IntPtr.Zero,0,share,disposition,0x200000|0x20u|(directory?1u:0x40u),IntPtr.Zero,0);
   if(result<0){if(handle!=null)handle.Dispose();throw new FileTransferFailure(result==unchecked((int)0xc0000035)?"destination-exists":result==unchecked((int)0xc0000034)?"source-missing":"path-unavailable");}try{Verify(handle,directory);return handle;}catch{handle.Dispose();throw;}
  }finally{Marshal.FreeHGlobal(unicode);Marshal.FreeHGlobal(chars);}
 }
 SafeFileHandle Parent { get { return ancestors[ancestors.Count - 1]; } }
 void VerifyAncestors(){foreach(var handle in ancestors)Verify(handle,true);}
 internal FileTransferPaths(string path,Action beforeChild=null){
  string parent=System.IO.Path.GetDirectoryName(path),root=System.IO.Path.GetPathRoot(path);
  try{var handle=CreateFile(root,0x80,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);if(handle.IsInvalid){handle.Dispose();throw new FileTransferFailure("path-unavailable");}ancestors.Add(handle);Verify(handle,true);
   foreach(string component in parent.Substring(root.Length).Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries)){VerifyAncestors();if(beforeChild!=null)beforeChild();ancestors.Add(Relative(Parent,component,0x80,3,1,true));}
  }catch{Dispose();throw;}

 }
 static void Verify(SafeFileHandle handle,bool directory){Info info;if(!GetFileInformationByHandle(handle,out info))throw new Win32Exception();if((info.Attributes&0x400)!=0||(directory&&(info.Attributes&0x10)==0))throw new FileTransferFailure("reparse-path");}
 internal FileStream OpenSource(string path){VerifyAncestors();if(BeforeFileOpen!=null)BeforeFileOpen();var handle=Relative(Parent,System.IO.Path.GetFileName(path),0x80000000,1,1,false);try{return new FileStream(handle,FileAccess.Read,65536,false);}catch{handle.Dispose();throw;}}
 internal FileStream CreatePartial(string path){VerifyAncestors();if(BeforePartialCreate!=null)BeforePartialCreate();var handle=Relative(Parent,System.IO.Path.GetFileName(path),0xc0010000,0,2,false);try{return new FileStream(handle,FileAccess.ReadWrite,65536,false);}catch{handle.Dispose();throw;}}
 internal static void DeleteOwned(SafeFileHandle handle){IntPtr data=Marshal.AllocHGlobal(1);try{Marshal.WriteByte(data,1);if(!SetFileInformationByHandle(handle,4,data,1))throw new Win32Exception();}finally{Marshal.FreeHGlobal(data);}}
 internal void RenameOwned(SafeFileHandle handle,string destination,bool replace){
  VerifyAncestors();
  if(BeforeRename!=null)BeforeRename();
  byte[] name=System.Text.Encoding.Unicode.GetBytes(System.IO.Path.GetFileName(destination));int offset=IntPtr.Size==8?20:12;int length=offset+name.Length+2;IntPtr data=Marshal.AllocHGlobal(length);
  try{for(int i=0;i<length;i++)Marshal.WriteByte(data,i,0);Marshal.WriteByte(data,replace?(byte)1:(byte)0);Marshal.WriteIntPtr(data,IntPtr.Size==8?8:4,Parent.DangerousGetHandle());Marshal.WriteInt32(data,IntPtr.Size==8?16:8,name.Length);Marshal.Copy(name,0,IntPtr.Add(data,offset),name.Length);IoStatus status;int result=NtSetInformationFile(handle,out status,data,(uint)length,10);if(result<0)throw new FileTransferFailure("finalize-failed");}finally{Marshal.FreeHGlobal(data);}
 }
 public void Dispose(){for(int i=ancestors.Count-1;i>=0;i--)ancestors[i].Dispose();ancestors.Clear();}
}
