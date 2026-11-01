using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal enum RuntimeTransactionState { None, NoOp, Created, Replaced }

internal sealed class InstallChoice {
 public bool Desktop {get;set;}=true;
 public bool StartMenu {get;set;}=true;
 public bool Client {get;set;}
}

internal sealed class RuntimeFileEntry {
 public string path {get;set;}
 public string sha256 {get;set;}
}

internal sealed class RuntimeManifest {
 public string schema {get;set;}
 public string version {get;set;}
 public List<RuntimeFileEntry> files {get;set;}=new List<RuntimeFileEntry>();
}

internal sealed class UserInstallation {
 internal readonly string DirectoryPath,Executable,DesktopShortcut,StartShortcut,RegistryPath,RunRegistryPath;
 internal const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 internal const string RunName="Remsup Support";
 internal const string RuntimeManifestName="remsup-runtime.json";
 static readonly Regex ShaPattern=new Regex(@"^[0-9a-f]{64}$",RegexOptions.Compiled);
 internal UserInstallation():this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Remsup"),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.Programs),@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Remsup"){}
 internal UserInstallation(string directory,string desktop,string start,string registry,string runRegistry=RunKey) {
  DirectoryPath=Path.GetFullPath(directory);Executable=Path.Combine(DirectoryPath,"Remsup.exe");DesktopShortcut=Path.Combine(desktop,"Remsup.lnk");StartShortcut=Path.Combine(start,"Remsup.lnk");RegistryPath=registry;RunRegistryPath=runRegistry;
 }
 internal bool IsInstalled(string path)=>string.Equals(Path.GetFullPath(path),Executable,StringComparison.OrdinalIgnoreCase);
 internal InstallChoice ReadChoice()=>File.Exists(Path.Combine(DirectoryPath,"installation.json"))?JsonSerializer.Deserialize<InstallChoice>(File.ReadAllText(Path.Combine(DirectoryPath,"installation.json"))):null;
 internal void Register(InstallChoice choice,string version,bool autoStart) {
  if(!File.Exists(Executable))throw new IOException("Installed Remsup.exe is missing.");
  Directory.CreateDirectory(DirectoryPath);
  if(choice.Desktop)Shortcut(DesktopShortcut,choice.Client,false);
  if(choice.StartMenu)Shortcut(StartShortcut,choice.Client,false);
  using(var key=Registry.CurrentUser.CreateSubKey(RegistryPath)) {
   key.SetValue("DisplayName","Remsup");key.SetValue("DisplayVersion",version);key.SetValue("DisplayIcon",Executable+",0");key.SetValue("InstallLocation",DirectoryPath);key.SetValue("Publisher","ID-Yo");key.SetValue("UninstallString","\""+Executable+"\" --uninstall");key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);
  }
  if(autoStart){using var key=Registry.CurrentUser.CreateSubKey(RunRegistryPath);key.SetValue(RunName,"\""+Executable+"\""+(choice.Client?" --client":""));}
  File.WriteAllText(Path.Combine(DirectoryPath,"installation.json"),JsonSerializer.Serialize(choice));
 }
 void Shortcut(string path,bool client,bool remove) {
  if(remove&&!File.Exists(path))return;
  object shell=null,link=null;
  try {
   shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic automation=shell;link=automation.CreateShortcut(path);dynamic shortcut=link;
   if(File.Exists(path)&&!string.Equals((string)shortcut.TargetPath,Executable,StringComparison.OrdinalIgnoreCase))throw new IOException("An unrelated Remsup shortcut exists; it was preserved: "+path);
   if(remove){File.Delete(path);return;}
   shortcut.TargetPath=Executable;shortcut.Arguments=client?"--client":"";shortcut.WorkingDirectory=DirectoryPath;shortcut.IconLocation=Executable+",0";shortcut.Description="Visible Remsup support";shortcut.Save();
  } finally {if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
 }
 internal void RemoveRegistration() {
  Shortcut(DesktopShortcut,false,true);Shortcut(StartShortcut,false,true);
  using(var key=Registry.CurrentUser.OpenSubKey(RunRegistryPath,true)) {string value=key?.GetValue(RunName) as string;if(value!=null&&(value.Equals(Executable,StringComparison.OrdinalIgnoreCase)||value.StartsWith("\""+Executable+"\"",StringComparison.OrdinalIgnoreCase)))key.DeleteValue(RunName,false);};
  Registry.CurrentUser.DeleteSubKeyTree(RegistryPath,false);
 }
 internal bool CleanupFiles() {
  if(!System.IO.Directory.Exists(DirectoryPath))return true;
  if(File.Exists(Executable)){
   try{File.Delete(Executable);}catch(IOException){}catch(UnauthorizedAccessException){}
   if(File.Exists(Executable))return false;
  }
  foreach(string file in System.IO.Directory.EnumerateFiles(DirectoryPath)) {
   string name=Path.GetFileName(file);
   bool owned=name=="Remsup.previous.exe"||name=="installation.json"||name=="Remsup.exe.cleanup.json"||name=="uninstall.log";
   if(name.StartsWith("Remsup.exe.old-",StringComparison.Ordinal)&&Guid.TryParseExact(name.Substring(15),"N",out _))owned=true;
   if(owned)try{File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}
  }
  CleanupAppLocalRuntimes(DirectoryPath);
  if(System.IO.Directory.GetFileSystemEntries(DirectoryPath).Length==0){System.IO.Directory.Delete(DirectoryPath);return true;}
  return false;
 }
 static bool HasReparseAncestor(string fullPath){
  var current=new DirectoryInfo(fullPath);
  while(current!=null){
   if(current.Exists&&current.Attributes.HasFlag(FileAttributes.ReparsePoint))return true;
   current=current.Parent;
  }
  return false;
 }
 internal static bool CleanupAppLocalRuntimes(string rootDir){
  string root=Path.GetFullPath(rootDir);
  if(HasReparseAncestor(root))return false;
  string runtimes=Path.Combine(root,"runtimes");
  if(!Directory.Exists(runtimes)||new DirectoryInfo(runtimes).Attributes.HasFlag(FileAttributes.ReparsePoint))return false;
  string webview=Path.Combine(runtimes,"webview2");
  if(!Directory.Exists(webview)||new DirectoryInfo(webview).Attributes.HasFlag(FileAttributes.ReparsePoint))return false;
  string marker=Path.Combine(webview,RuntimeManifestName);
  if(!File.Exists(marker))return false;
  RuntimeManifest manifest=null;
  try{manifest=JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(marker));}catch{}
  if(manifest==null||manifest.schema!="remsup-runtime-v1"||string.IsNullOrEmpty(manifest.version)||manifest.files==null)return false;

  bool allDeleted=true;
  foreach(var entry in manifest.files){
   if(string.IsNullOrEmpty(entry.path)||entry.path.Contains("..")||Path.IsPathRooted(entry.path)){allDeleted=false;continue;}
   if(string.IsNullOrEmpty(entry.sha256)||!ShaPattern.IsMatch(entry.sha256)){allDeleted=false;continue;}
   string target=Path.GetFullPath(Path.Combine(webview,entry.path.Replace('/',Path.DirectorySeparatorChar)));
   if(!target.StartsWith(webview+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)){allDeleted=false;continue;}
   if(File.Exists(target)){
    if(HasReparseAncestor(Path.GetDirectoryName(target))){allDeleted=false;continue;}
    string h=null;
    try{using var stream=File.OpenRead(target);h=Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}catch{}
    if(h==null||!string.Equals(h,entry.sha256,StringComparison.OrdinalIgnoreCase)){allDeleted=false;continue;}
    try{File.Delete(target);}catch{allDeleted=false;}
   }
  }

  CleanEmptyDirectories(webview);
  // Retain manifest and directories if unowned/unlisted content or failed deletions remain
  if(allDeleted&&Directory.EnumerateFiles(webview).All(f=>f==marker)&&Directory.EnumerateDirectories(webview).All(d=>false)){
   try{File.Delete(marker);}catch{allDeleted=false;}
  } else {
   allDeleted=false;
  }
  if(allDeleted){
   if(Directory.Exists(webview)&&System.IO.Directory.GetFileSystemEntries(webview).Length==0){
    try{Directory.Delete(webview);}catch{allDeleted=false;}
   }
   if(Directory.Exists(runtimes)&&System.IO.Directory.GetFileSystemEntries(runtimes).Length==0){
    try{Directory.Delete(runtimes);}catch{}
   }
  }
  return allDeleted;
 }
 static void CleanEmptyDirectories(string dir){
  try{
   foreach(var sub in Directory.EnumerateDirectories(dir)){
    var info=new DirectoryInfo(sub);
    if(info.Attributes.HasFlag(FileAttributes.ReparsePoint))continue;
    CleanEmptyDirectories(sub);
    if(System.IO.Directory.GetFileSystemEntries(sub).Length==0)try{Directory.Delete(sub);}catch{}
   }
  }catch{}
 }
 internal static RuntimeManifest GenerateRuntimeManifest(string webviewDir,string version){
  if(string.IsNullOrEmpty(version))throw new ArgumentException("Version must not be empty.",nameof(version));
  string root=Path.GetFullPath(webviewDir);
  if(!Directory.Exists(root))throw new DirectoryNotFoundException("WebView2 runtime directory does not exist: "+root);
  if(HasReparseAncestor(root)||new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
   throw new InvalidOperationException("Reparse points are not allowed in runtime root.");
  string mainExe=Path.Combine(root,"msedgewebview2.exe");
  if(!File.Exists(mainExe)||File.GetAttributes(mainExe).HasFlag(FileAttributes.ReparsePoint))throw new FileNotFoundException("msedgewebview2.exe is required in runtime root.");
  var vi=System.Diagnostics.FileVersionInfo.GetVersionInfo(mainExe);
  if(!string.IsNullOrEmpty(vi.FileVersion)&&!string.Equals(vi.FileVersion,version,StringComparison.OrdinalIgnoreCase))
   throw new InvalidOperationException("msedgewebview2.exe version "+vi.FileVersion+" does not match requested version "+version);

  var manifest=new RuntimeManifest{schema="remsup-runtime-v1",version=version,files=new List<RuntimeFileEntry>()};
  foreach(string file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)){
   string name=Path.GetFileName(file);
   if(string.Equals(name,RuntimeManifestName,StringComparison.OrdinalIgnoreCase))continue;
   if(File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))throw new InvalidOperationException("Reparse file links not allowed: "+file);
   string dir=Path.GetDirectoryName(file);
   if(HasReparseAncestor(dir))throw new InvalidOperationException("Reparse points are not allowed in runtime subdirectories: "+dir);
   string rel=Path.GetRelativePath(root,file).Replace('\\','/');
   using var stream=File.OpenRead(file);
   string sha=Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
   manifest.files.Add(new RuntimeFileEntry{path=rel,sha256=sha});
  }
  manifest.files.Sort((a,b)=>string.CompareOrdinal(a.path,b.path));
  return manifest;
 }
 internal static string WriteRuntimeManifest(string webviewDir,string version){
  var manifest=GenerateRuntimeManifest(webviewDir,version);
  string manifestPath=Path.Combine(webviewDir,RuntimeManifestName);
  var options=new JsonSerializerOptions{WriteIndented=true};
  File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest,options));
  return manifestPath;
 }
 internal static bool VerifyRuntimeDirectory(string webviewDir,out RuntimeManifest manifest){
  manifest=null;
  if(string.IsNullOrEmpty(webviewDir)||!Directory.Exists(webviewDir))return false;
  string root=Path.GetFullPath(webviewDir);
  if(HasReparseAncestor(root)||new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))return false;
  string marker=Path.Combine(root,RuntimeManifestName);
  if(!File.Exists(marker)||File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint))return false;
  try{manifest=JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(marker));}catch{return false;}
  if(manifest==null||manifest.schema!="remsup-runtime-v1"||string.IsNullOrEmpty(manifest.version)||manifest.files==null||manifest.files.Count==0)return false;
  string mainExe=Path.Combine(root,"msedgewebview2.exe");
  if(!File.Exists(mainExe)||File.GetAttributes(mainExe).HasFlag(FileAttributes.ReparsePoint))return false;
  var vi=System.Diagnostics.FileVersionInfo.GetVersionInfo(mainExe);
  if(!string.IsNullOrEmpty(vi.FileVersion)&&!string.Equals(vi.FileVersion,manifest.version,StringComparison.OrdinalIgnoreCase))return false;

  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  bool hasMain=false;
  foreach(var entry in manifest.files){
   if(string.IsNullOrEmpty(entry.path)||entry.path.Contains("..")||Path.IsPathRooted(entry.path))return false;
   if(string.IsNullOrEmpty(entry.sha256)||!ShaPattern.IsMatch(entry.sha256))return false;
   if(!seen.Add(entry.path))return false;
   string f=Path.GetFullPath(Path.Combine(root,entry.path.Replace('/',Path.DirectorySeparatorChar)));
   if(!f.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(f))return false;
   if(File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint)||HasReparseAncestor(Path.GetDirectoryName(f)))return false;
   string h=null;
   try{using var s=File.OpenRead(f);h=Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();}catch{return false;}
   if(!string.Equals(h,entry.sha256,StringComparison.OrdinalIgnoreCase))return false;
   if(string.Equals(entry.path,"msedgewebview2.exe",StringComparison.OrdinalIgnoreCase))hasMain=true;
  }
  return hasMain;
 }
 internal static bool StageRuntimePackage(string sourceWebviewDir,string targetWebviewDir,out string oldBackupPath,out RuntimeTransactionState state,string expectedVersion=null,string expectedManifestSha=null){
  oldBackupPath=null;
  state=RuntimeTransactionState.None;
  if(!VerifyRuntimeDirectory(sourceWebviewDir,out var srcManifest))return false;
  string srcRoot=Path.GetFullPath(sourceWebviewDir);
  string dstRoot=Path.GetFullPath(targetWebviewDir);

  string dstParent=Path.GetDirectoryName(dstRoot);
  if(string.IsNullOrEmpty(dstParent))return false;
  string dstAppRoot=Path.GetDirectoryName(dstParent);
  if(HasReparseAncestor(dstAppRoot))return false;
  if(Directory.Exists(dstParent)&&(HasReparseAncestor(dstParent)||new DirectoryInfo(dstParent).Attributes.HasFlag(FileAttributes.ReparsePoint)))return false;
  if(Directory.Exists(dstRoot)&&(HasReparseAncestor(dstRoot)||new DirectoryInfo(dstRoot).Attributes.HasFlag(FileAttributes.ReparsePoint)))return false;

  if(string.Equals(srcRoot,dstRoot,StringComparison.OrdinalIgnoreCase)){
   if(expectedVersion!=null&&!string.Equals(srcManifest.version,expectedVersion,StringComparison.OrdinalIgnoreCase))return false;
   if(expectedManifestSha!=null){
    string srcMarkerFile=Path.Combine(srcRoot,RuntimeManifestName);
    string srcSha=null;
    try{using var s=File.OpenRead(srcMarkerFile);srcSha=Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();}catch{}
    if(!string.Equals(srcSha,expectedManifestSha,StringComparison.OrdinalIgnoreCase))return false;
   }
   state=RuntimeTransactionState.NoOp;
   return true;
  }

  RuntimeManifest oldDstManifest=null;
  bool dstExists=Directory.Exists(dstRoot);
  if(dstExists){
   if(!VerifyRuntimeDirectory(dstRoot,out oldDstManifest))return false;
   foreach(var sub in Directory.EnumerateDirectories(dstRoot,"*",SearchOption.AllDirectories)){
    if(new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)||HasReparseAncestor(sub))return false;
   }
   foreach(var file in Directory.EnumerateFiles(dstRoot,"*",SearchOption.AllDirectories)){
    if(File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))return false;
   }
  }

  Directory.CreateDirectory(dstParent);
  string staging=Path.Combine(dstParent,"webview2.tmp-"+Guid.NewGuid().ToString("N"));
  try{
   Directory.CreateDirectory(staging);
   foreach(var entry in srcManifest.files){
    string srcFile=Path.Combine(srcRoot,entry.path.Replace('/',Path.DirectorySeparatorChar));
    string dstFile=Path.Combine(staging,entry.path.Replace('/',Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(dstFile));
    File.Copy(srcFile,dstFile,false);
   }
   File.Copy(Path.Combine(srcRoot,RuntimeManifestName),Path.Combine(staging,RuntimeManifestName),false);

   if(!VerifyRuntimeDirectory(staging,out var stagedMan)){
    try{Directory.Delete(staging,true);}catch{}
    return false;
   }
   if(expectedVersion!=null&&!string.Equals(stagedMan.version,expectedVersion,StringComparison.OrdinalIgnoreCase)){
    try{Directory.Delete(staging,true);}catch{}
    return false;
   }
   if(expectedManifestSha!=null){
    string stagedMarkerFile=Path.Combine(staging,RuntimeManifestName);
    string stagedSha=null;
    try{using var s=File.OpenRead(stagedMarkerFile);stagedSha=Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();}catch{}
    if(!string.Equals(stagedSha,expectedManifestSha,StringComparison.OrdinalIgnoreCase)){
     try{Directory.Delete(staging,true);}catch{}
     return false;
    }
   }

   if(dstExists&&oldDstManifest!=null){
    var oldListed=new HashSet<string>(oldDstManifest.files.Select(x=>x.path),StringComparer.OrdinalIgnoreCase);
    foreach(string f in Directory.EnumerateFiles(dstRoot,"*",SearchOption.AllDirectories)){
     string name=Path.GetFileName(f);
     if(string.Equals(name,RuntimeManifestName,StringComparison.OrdinalIgnoreCase))continue;
     string rel=Path.GetRelativePath(dstRoot,f).Replace('\\','/');
     if(!oldListed.Contains(rel)){
      string preservedDst=Path.Combine(staging,rel.Replace('/',Path.DirectorySeparatorChar));
      if(!File.Exists(preservedDst)){
       Directory.CreateDirectory(Path.GetDirectoryName(preservedDst));
       File.Copy(f,preservedDst,false);
      }
     }
    }
   }

   if(dstExists){
    oldBackupPath=dstRoot+".old-"+Guid.NewGuid().ToString("N");
    Directory.Move(dstRoot,oldBackupPath);
    state=RuntimeTransactionState.Replaced;
   }else{
    oldBackupPath=null;
    state=RuntimeTransactionState.Created;
   }

   try{
    Directory.Move(staging,dstRoot);
   }catch{
    bool restored=false;
    if(oldBackupPath!=null&&Directory.Exists(oldBackupPath)&&!Directory.Exists(dstRoot)){
     try{
      Directory.Move(oldBackupPath,dstRoot);
      restored=Directory.Exists(dstRoot)&&File.Exists(Path.Combine(dstRoot,RuntimeManifestName));
     }catch{}
    }
    if(!restored&&oldBackupPath!=null){
     // Preserve Replaced state and oldBackupPath so recovery can be performed!
    }else{
     state=RuntimeTransactionState.None;
    }
    throw;
   }
   return true;
  }catch{
   if(Directory.Exists(staging)){try{Directory.Delete(staging,true);}catch{}}
   return false;
  }
 }
 internal static bool RollbackStagedRuntime(string targetWebviewDir,string oldBackupPath,RuntimeTransactionState state){
  if(state==RuntimeTransactionState.None||state==RuntimeTransactionState.NoOp)return true;
  if(string.IsNullOrEmpty(targetWebviewDir))return false;
  string root=Path.GetFullPath(targetWebviewDir);
  if(HasReparseAncestor(root))return false;
  try{
   string tmpDel=null;
   if(Directory.Exists(root)){
    if(new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))return false;
    tmpDel=root+".del-"+Guid.NewGuid().ToString("N");
    Directory.Move(root,tmpDel);
   }
   bool backupRestored=false;
   if(state==RuntimeTransactionState.Replaced){
    if(oldBackupPath!=null&&Directory.Exists(oldBackupPath)&&!HasReparseAncestor(oldBackupPath)&&!new DirectoryInfo(oldBackupPath).Attributes.HasFlag(FileAttributes.ReparsePoint)){
     Directory.Move(oldBackupPath,root);
     backupRestored=Directory.Exists(root)&&File.Exists(Path.Combine(root,RuntimeManifestName));
    }
   }else if(state==RuntimeTransactionState.Created){
    backupRestored=!Directory.Exists(root);
   }
   if(backupRestored){
    if(tmpDel!=null&&Directory.Exists(tmpDel)){
     try{Directory.Delete(tmpDel,true);}catch{}
    }
   }
   // If backupRestored is false, DO NOT delete tmpDel! Retain recoverable bytes and report false.
   return backupRestored;
  }catch{
   return false;
  }
 }
 internal static bool CommitStagedRuntime(string oldBackupPath,out int preservedCount){
  preservedCount=0;
  if(string.IsNullOrEmpty(oldBackupPath)||!Directory.Exists(oldBackupPath))return true;
  try{
   string fullBackup=Path.GetFullPath(oldBackupPath);
   if(HasReparseAncestor(fullBackup)||new DirectoryInfo(fullBackup).Attributes.HasFlag(FileAttributes.ReparsePoint))return false;
   string marker=Path.Combine(fullBackup,RuntimeManifestName);
   if(File.Exists(marker)&&!File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint)){
    RuntimeManifest manifest=null;
    try{manifest=JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(marker));}catch{}
    if(manifest!=null&&manifest.schema=="remsup-runtime-v1"&&manifest.files!=null){
     foreach(var entry in manifest.files){
      if(string.IsNullOrEmpty(entry.path)||entry.path.Contains("..")||Path.IsPathRooted(entry.path))continue;
      string f=Path.GetFullPath(Path.Combine(fullBackup,entry.path.Replace('/',Path.DirectorySeparatorChar)));
      if(File.Exists(f)&&!File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint)&&!HasReparseAncestor(Path.GetDirectoryName(f))){
       string h=null;
       try{using var s=File.OpenRead(f);h=Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();}catch{}
       if(string.Equals(h,entry.sha256,StringComparison.OrdinalIgnoreCase)){
        try{File.Delete(f);}catch{}
       }
      }
     }
     CleanEmptyDirectories(fullBackup);
     if(Directory.EnumerateFiles(fullBackup).All(f=>f==marker)&&Directory.EnumerateDirectories(fullBackup).All(d=>false)){
      try{File.Delete(marker);}catch{}
     }
    }
   }
   if(Directory.Exists(fullBackup)){
    var remaining=Directory.GetFileSystemEntries(fullBackup);
    if(remaining.Length==0){
     Directory.Delete(fullBackup,true);
     return true;
    }else{
     preservedCount=remaining.Length;
     return false;
    }
   }
   return true;
  }catch{
   return false;
  }
 }
 internal bool InstallAppLocalRuntimes(string sourceExecutablePath){
  if(string.IsNullOrEmpty(sourceExecutablePath)||!File.Exists(sourceExecutablePath))return false;
  string sourceDir=Path.GetDirectoryName(Path.GetFullPath(sourceExecutablePath));
  string sourceRuntimes=Path.Combine(sourceDir,"runtimes","webview2");
  string targetRuntimes=Path.Combine(DirectoryPath,"runtimes","webview2");
  if(!StageRuntimePackage(sourceRuntimes,targetRuntimes,out string backup,out var txState))return false;
  CommitStagedRuntime(backup,out _);
  return true;
 }
}