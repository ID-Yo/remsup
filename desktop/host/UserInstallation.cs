using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

internal sealed class InstallChoice {
 public bool Desktop {get;set;}=true;
 public bool StartMenu {get;set;}=true;
 public bool Client {get;set;}
}
internal sealed class UserInstallation {
 internal readonly string DirectoryPath,Executable,DesktopShortcut,StartShortcut,RegistryPath,RunRegistryPath;
 internal const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 internal const string RunName="Remsup Support";
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
  using(var key=Registry.CurrentUser.OpenSubKey(RunRegistryPath,true)) {string value=key?.GetValue(RunName) as string;if(value!=null&&(value.Equals(Executable,StringComparison.OrdinalIgnoreCase)||value.StartsWith("\""+Executable+"\"",StringComparison.OrdinalIgnoreCase)))key.DeleteValue(RunName,false);}
  Registry.CurrentUser.DeleteSubKeyTree(RegistryPath,false);
 }
 internal bool CleanupFiles() {
  if(!System.IO.Directory.Exists(DirectoryPath))return true;
  foreach(string file in System.IO.Directory.EnumerateFiles(DirectoryPath)) {
   string name=Path.GetFileName(file);
   bool owned=name=="Remsup.exe"||name=="Remsup.previous.exe"||name=="installation.json"||name=="Remsup.exe.cleanup.json"||name=="uninstall.log";
   if(name.StartsWith("Remsup.exe.old-",StringComparison.Ordinal)&&Guid.TryParseExact(name.Substring(15),"N",out _))owned=true;
   if(owned)try{File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}
  }
  if(System.IO.Directory.GetFileSystemEntries(DirectoryPath).Length==0){System.IO.Directory.Delete(DirectoryPath);return true;}
  return false;
 }
}
