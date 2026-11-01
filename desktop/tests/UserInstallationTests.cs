using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

internal static class UserInstallationTests {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 [STAThread] static void Main(){
  string root=Path.Combine(Path.GetTempPath(),"Remsup-install-tests-"+Guid.NewGuid().ToString("N")),key=@"Software\RemsupInstallTests\"+Guid.NewGuid().ToString("N");
  Directory.CreateDirectory(root);
  string desktop=Path.Combine(root,"Desktop"),start=Path.Combine(root,"Start");
  Directory.CreateDirectory(desktop);Directory.CreateDirectory(start);
  var install=new UserInstallation(Path.Combine(root,"Programs","Remsup"),desktop,start,key,key+"Run");
  Directory.CreateDirectory(install.DirectoryPath);File.WriteAllText(install.Executable,"fixture");

  try {
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.43",false);
   Check(!File.Exists(install.DesktopShortcut)&&!File.Exists(install.StartShortcut),"unchecked icons created");
   install.Register(new InstallChoice(),"v1.43",true);
   Check(File.Exists(install.DesktopShortcut)&&File.Exists(install.StartShortcut),"icons missing");
   using(var run=Registry.CurrentUser.OpenSubKey(key+"Run")){Check((string)run.GetValue(UserInstallation.RunName)=="\""+install.Executable+"\"","startup path not stable");}
   string target=Target(install.DesktopShortcut);
   Check(target==install.Executable,"shortcut not stable");
   install.Register(install.ReadChoice(),"v1.44",false);
   Check(Target(install.DesktopShortcut)==target,"update changed icon target");
   using(var registration=Registry.CurrentUser.OpenSubKey(key)){Check((string)registration.GetValue("DisplayVersion")=="v1.44"&&(string)registration.GetValue("UninstallString")=="\""+target+"\" --uninstall","app registration/version wrong");}
   install.RemoveRegistration();
   Check(!File.Exists(install.DesktopShortcut)&&!File.Exists(install.StartShortcut)&&Registry.CurrentUser.OpenSubKey(key)==null,"uninstall registration failed");

   // Test 1: locked executable keeps runtime intact in direct CleanupFiles
   string runtimeDir=Path.Combine(install.DirectoryPath,"runtimes","webview2");
   Directory.CreateDirectory(runtimeDir);
   string testFile=Path.Combine(runtimeDir,"msedgewebview2.exe");
   File.WriteAllText(testFile,"binary content");
   string fileHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testFile))).ToLowerInvariant();
   string manifestContent=JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{new RuntimeFileEntry{path="msedgewebview2.exe",sha256=fileHash}}
   });
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),manifestContent);

   using(var locked=new FileStream(install.Executable,FileMode.Open,FileAccess.Read,FileShare.Read)){
    Check(!install.CleanupFiles()&&File.Exists(install.Executable),"locked app not preserved");
    Check(File.Exists(testFile),"runtime was deleted while executable was locked");
   }

   // Test 2: unlisted file in runtime directory is preserved
   string unlisted=Path.Combine(runtimeDir,"unrelated.doc");
   File.WriteAllText(unlisted,"important user doc");
   install.CleanupFiles();
   Check(File.Exists(unlisted),"unlisted file in runtime directory was deleted");
   Check(!File.Exists(testFile),"inventoried runtime file was not deleted");
   File.Delete(unlisted);

   // Test 3: corrupted / invalid manifest does not delete runtime files
   File.WriteAllText(testFile,"binary content");
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),"invalid json");
   install.CleanupFiles();
   Check(File.Exists(testFile),"runtime was deleted despite invalid manifest");
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),manifestContent);

   // Test 4: hash mismatch preserves file
   File.WriteAllText(testFile,"modified binary content");
   install.CleanupFiles();
   Check(File.Exists(testFile),"runtime was deleted despite hash mismatch");
   Check(File.Exists(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName)),"manifest was removed despite hash mismatch");

   // Clean up direct helper test state
   File.WriteAllText(testFile,"binary content");
   Check(install.CleanupFiles(),"clean uninstall executed");
   Check(!Directory.Exists(install.DirectoryPath),"install directory deleted");

   // Test 5: real UninstallManager worker process with runtime inventory
   Directory.CreateDirectory(install.DirectoryPath);
   File.WriteAllText(install.Executable,"fixture");
   Directory.CreateDirectory(runtimeDir);
   File.WriteAllText(testFile,"binary content");
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),manifestContent);
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   string status=Path.Combine(root,"uninstall-status.txt");
   int helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"uninstall worker remained running");
   }
   Check(!Directory.Exists(install.DirectoryPath)&&File.ReadAllText(status).Contains("Remsup removed"),"real worker did not clean up inventoried runtime");

   // Test 6: worker preserves unlisted file and reports truthful status
   Directory.CreateDirectory(install.DirectoryPath);
   File.WriteAllText(install.Executable,"fixture");
   Directory.CreateDirectory(runtimeDir);
   File.WriteAllText(testFile,"binary content");
   File.WriteAllText(unlisted,"preserved user doc");
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),manifestContent);
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"uninstall worker with unlisted file remained running");
   }
   Check(File.Exists(unlisted),"worker deleted unlisted user file");
   Check(File.Exists(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName)),"worker deleted manifest while unlisted files remained");
   Check(File.ReadAllText(status).Contains("some runtime files were locked or preserved"),"worker did not report preserved runtime status");
   File.Delete(unlisted);
   File.Delete(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName));
   Directory.Delete(runtimeDir);
   Directory.Delete(Path.Combine(install.DirectoryPath,"runtimes"));
   Directory.Delete(install.DirectoryPath);

   // Test 6b: worker rejects path traversal, rooted paths, and malformed SHA without escaping
   Directory.CreateDirectory(install.DirectoryPath);
   File.WriteAllText(install.Executable,"fixture");
   Directory.CreateDirectory(runtimeDir);
   string sentinelOutside = Path.Combine(install.DirectoryPath,"sentinel.txt");
   File.WriteAllText(sentinelOutside,"sentinel content");
   string sentinelHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sentinelOutside))).ToLowerInvariant();
   string ownedRootedTarget = Path.Combine(root,"owned-rooted-sentinel.txt");
   File.WriteAllText(ownedRootedTarget,"owned rooted sentinel");
   string ownedRootedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ownedRootedTarget))).ToLowerInvariant();
   string validFileWithBadSha = Path.Combine(runtimeDir,"valid.txt");
   File.WriteAllText(validFileWithBadSha,"actual content");
   string validFileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(validFileWithBadSha))).ToLowerInvariant();
   string traversalManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="../../sentinel.txt",sha256=sentinelHash},
     new RuntimeFileEntry{path=ownedRootedTarget.Replace(Path.DirectorySeparatorChar,'/'),sha256=ownedRootedHash},
     new RuntimeFileEntry{path="valid.txt",sha256="bad_sha_not_64_hex"}
    }
   });
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),traversalManifest);
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"worker traversal test remained running");
   }
   Check(File.Exists(sentinelOutside)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sentinelOutside))).ToLowerInvariant()==sentinelHash,"worker traversal touched outer sentinel");
   Check(File.Exists(ownedRootedTarget)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ownedRootedTarget))).ToLowerInvariant()==ownedRootedHash,"worker deleted owned rooted target");
   Check(File.Exists(validFileWithBadSha)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(validFileWithBadSha))).ToLowerInvariant()==validFileHash,"worker deleted file with malformed SHA");
   Check(File.Exists(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName)),"worker deleted manifest when traversal/malformed entries failed");
   Check(File.ReadAllText(status).Contains("some runtime files were locked or preserved"),"worker status did not reflect preserved items on traversal rejection");
   File.Delete(sentinelOutside);
   File.Delete(ownedRootedTarget);
   File.Delete(validFileWithBadSha);
   File.Delete(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName));
   Directory.Delete(runtimeDir);
   Directory.Delete(Path.Combine(install.DirectoryPath,"runtimes"));
   Directory.Delete(install.DirectoryPath);

   // Test 6c: worker handles hash mismatch and retry recovery
   Directory.CreateDirectory(install.DirectoryPath);
   File.WriteAllText(install.Executable,"fixture");
   Directory.CreateDirectory(runtimeDir);
   File.WriteAllText(testFile,"original content");
   string validHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testFile))).ToLowerInvariant();
   File.WriteAllText(testFile,"tampered content");
   string retryManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="msedgewebview2.exe",sha256=validHash}
    }
   });
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),retryManifest);
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"worker mismatch test remained running");
   }
   Check(File.Exists(testFile),"worker deleted file with mismatched hash");
   Check(File.Exists(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName)),"worker deleted manifest despite hash mismatch");
   // Restore matching content and execute retry with retained manifest
   File.WriteAllText(testFile,"original content");
   File.WriteAllText(install.Executable,"fixture");
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"worker retry test remained running");
   }
   Check(!Directory.Exists(install.DirectoryPath)&&File.ReadAllText(status).Contains("Remsup removed"),"worker retry with restored valid file did not complete clean uninstall");

   // Test 6d: worker handles hash-read refusal (FileShare.None), preserves manifest and status, then clean retry
   Directory.CreateDirectory(install.DirectoryPath);
   File.WriteAllText(install.Executable,"fixture");
   Directory.CreateDirectory(runtimeDir);
   File.WriteAllText(testFile,"binary content");
   string lockedTestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testFile))).ToLowerInvariant();
   string lockedManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="msedgewebview2.exe",sha256=lockedTestHash}
    }
   });
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),lockedManifest);
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   using(var lockedFile = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.None)){
    helper=UninstallManager.Schedule(install,false,status);
    using(var process=System.Diagnostics.Process.GetProcessById(helper)){
     Check(process.WaitForExit(15000),"worker locked runtime file test remained running");
    }
    Check(File.Exists(testFile),"worker deleted locked runtime file");
    Check(File.Exists(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName)),"worker deleted manifest despite hash-read failure");
    Check(File.ReadAllText(status).Contains("some runtime files were locked or preserved"),"worker did not report preserved runtime on file lock");
   }
   // Retry after release
   File.WriteAllText(install.Executable,"fixture");
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"worker retry after lock release remained running");
   }
   Check(!Directory.Exists(install.DirectoryPath)&&File.ReadAllText(status).Contains("Remsup removed"),"worker clean retry after lock release failed");

   // Test 6e: C# helper parity check - missing target entry with bad sha preserves marker
   Directory.CreateDirectory(install.DirectoryPath);
   Directory.CreateDirectory(runtimeDir);
   string badTargetManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="missing-target.exe",sha256="bad_sha"}
    }
   });
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),badTargetManifest);
   Check(!install.CleanupFiles(),"C# helper cleaned up with malformed missing-target manifest");
   Check(File.Exists(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName)),"C# helper deleted manifest despite missing target / bad sha");
   File.Delete(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName));
   Directory.Delete(runtimeDir);
   Directory.Delete(Path.Combine(install.DirectoryPath,"runtimes"));
   Directory.Delete(install.DirectoryPath);

   // Test 6f: worker handles post-hash matching DELETE refusal (FileShare.Read), preserves manifest and status, then clean retry
   Directory.CreateDirectory(install.DirectoryPath);
   File.WriteAllText(install.Executable,"fixture");
   Directory.CreateDirectory(runtimeDir);
   File.WriteAllText(testFile,"matching binary content");
   string postReadTestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testFile))).ToLowerInvariant();
   string postReadManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="msedgewebview2.exe",sha256=postReadTestHash}
    }
   });
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),postReadManifest);
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   using(var readShareOnly = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.Read)){
    // FileShare.Read allows SHA256 computation to succeed, but blocks Remove-Item from deleting the file!
    helper=UninstallManager.Schedule(install,false,status);
    using(var process=System.Diagnostics.Process.GetProcessById(helper)){
     Check(process.WaitForExit(15000),"worker post-hash delete refusal test remained running");
    }
    Check(File.Exists(testFile),"worker deleted in-use runtime file");
    Check(File.Exists(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName)),"worker deleted manifest despite post-hash delete failure");
    Check(File.ReadAllText(status).Contains("some runtime files were locked or preserved"),"worker status did not reflect delete failure");
   }
   // Clean retry after stream release
   File.WriteAllText(install.Executable,"fixture");
   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"worker retry after delete-lock release remained running");
   }
   Check(!Directory.Exists(install.DirectoryPath)&&File.ReadAllText(status).Contains("Remsup removed"),"worker clean retry after delete-lock release failed");

   // Test 6g: worker and C# helper reparse / junction detection across leaf, ancestor, and descendant
   // Target 1: Owned external target for webview2 leaf junction
   string ownedLeafTarget = Path.Combine(root,"owned-leaf-target");
   Directory.CreateDirectory(ownedLeafTarget);
   string leafListedSentinel = Path.Combine(ownedLeafTarget,"msedgewebview2.exe");
   File.WriteAllText(leafListedSentinel,"leaf listed sentinel bytes");
   string leafListedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(leafListedSentinel))).ToLowerInvariant();
   string leafUnlistedSentinel = Path.Combine(ownedLeafTarget,"unlisted-leaf.txt");
   File.WriteAllText(leafUnlistedSentinel,"leaf unlisted sentinel bytes");
   string leafUnlistedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(leafUnlistedSentinel))).ToLowerInvariant();

   // Target 2: Owned external target for ancestor junction
   string ownedAncestorTarget = Path.Combine(root,"owned-ancestor-target");
   Directory.CreateDirectory(ownedAncestorTarget);
   string ancestorSentinel = Path.Combine(ownedAncestorTarget,"ancestor-sentinel.txt");
   File.WriteAllText(ancestorSentinel,"ancestor sentinel bytes");
   string ancestorHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ancestorSentinel))).ToLowerInvariant();

   // Target 3: Owned external target for descendant junction inside webview2
   string ownedDescendantTarget = Path.Combine(root,"owned-descendant-target");
   Directory.CreateDirectory(ownedDescendantTarget);
   string descendantSentinel = Path.Combine(ownedDescendantTarget,"descendant-sentinel.txt");
   File.WriteAllText(descendantSentinel,"descendant sentinel bytes");
   string descendantHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(descendantSentinel))).ToLowerInvariant();

   // Case 1: Leaf junction (runtimes/webview2 is a ReparsePoint)
   Directory.CreateDirectory(install.DirectoryPath);
   File.WriteAllText(install.Executable,"fixture");
   string runtimesDir = Path.Combine(install.DirectoryPath,"runtimes");
   Directory.CreateDirectory(runtimesDir);

   string leafManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="msedgewebview2.exe",sha256=leafListedHash}
    }
   });
   File.WriteAllText(Path.Combine(ownedLeafTarget,UserInstallation.RuntimeManifestName),leafManifest);

   string juncCmdLeaf = "New-Item -ItemType Junction -Path '" + runtimeDir.Replace('/', '\\') + "' -Target '" + ownedLeafTarget.Replace('/', '\\') + "' -Force";
   using(var juncProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + juncCmdLeaf + "\"") { CreateNoWindow=true, UseShellExecute=false })){
    Check(juncProc.WaitForExit(10000),"leaf junction creation timed out");
    Check(juncProc.ExitCode==0,"leaf junction creation failed");
   }
   Check(Directory.Exists(runtimeDir)&&new DirectoryInfo(runtimeDir).Attributes.HasFlag(FileAttributes.ReparsePoint),"leaf junction not verified as ReparsePoint");

   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"worker leaf reparse test remained running");
   }

   Check(File.Exists(leafListedSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(leafListedSentinel))).ToLowerInvariant()==leafListedHash,"worker deleted listed file in leaf junction");
   Check(File.Exists(leafUnlistedSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(leafUnlistedSentinel))).ToLowerInvariant()==leafUnlistedHash,"worker deleted unlisted file in leaf junction");
   Check(File.Exists(Path.Combine(ownedLeafTarget,UserInstallation.RuntimeManifestName)),"worker deleted manifest in leaf junction target");
   Check(File.ReadAllText(status).Contains("some runtime files were locked or preserved"),"worker status did not reflect leaf reparse refusal");

   Check(!UserInstallation.CleanupAppLocalRuntimes(install.DirectoryPath), "C# helper allowed leaf reparse cleanup");
   Check(File.Exists(leafListedSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(leafListedSentinel))).ToLowerInvariant()==leafListedHash,"C# helper touched listed file in leaf junction");
   Check(File.Exists(leafUnlistedSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(leafUnlistedSentinel))).ToLowerInvariant()==leafUnlistedHash,"C# helper touched unlisted file in leaf junction");

   Directory.Delete(runtimeDir); // safe unlink of leaf junction
   Directory.Delete(runtimesDir);
   Directory.Delete(install.DirectoryPath);

   // Case 2: Ancestor / Installation-root junction (install.DirectoryPath root is a ReparsePoint)
   // Setup target directory structure with valid reachable runtimes/webview2 and manifest with matching listed file
   string targetRuntimes = Path.Combine(ownedAncestorTarget,"runtimes");
   string targetWv = Path.Combine(targetRuntimes,"webview2");
   Directory.CreateDirectory(targetWv);
   string ancestorListedSentinel = Path.Combine(targetWv,"msedgewebview2.exe");
   File.WriteAllText(ancestorListedSentinel,"ancestor listed sentinel bytes");
   string ancestorListedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ancestorListedSentinel))).ToLowerInvariant();
   string ancestorUnlistedSentinel = Path.Combine(targetWv,"unlisted-ancestor.txt");
   File.WriteAllText(ancestorUnlistedSentinel,"ancestor unlisted sentinel bytes");
   string ancestorUnlistedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ancestorUnlistedSentinel))).ToLowerInvariant();
   File.WriteAllText(Path.Combine(ownedAncestorTarget,"Remsup.exe"),"fixture");

   string ancestorManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="msedgewebview2.exe",sha256=ancestorListedHash}
    }
   });
   File.WriteAllText(Path.Combine(targetWv,UserInstallation.RuntimeManifestName),ancestorManifest);

   string juncCmdAncestor = "New-Item -ItemType Junction -Path '" + install.DirectoryPath.Replace('/', '\\') + "' -Target '" + ownedAncestorTarget.Replace('/', '\\') + "' -Force";
   using(var juncProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + juncCmdAncestor + "\"") { CreateNoWindow=true, UseShellExecute=false })){
    Check(juncProc.WaitForExit(10000),"ancestor junction creation timed out");
    Check(juncProc.ExitCode==0,"ancestor junction creation failed");
   }
   Check(Directory.Exists(install.DirectoryPath)&&new DirectoryInfo(install.DirectoryPath).Attributes.HasFlag(FileAttributes.ReparsePoint),"ancestor junction not verified as ReparsePoint");

   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"worker ancestor reparse test remained running");
   }

   Check(File.Exists(ancestorListedSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ancestorListedSentinel))).ToLowerInvariant()==ancestorListedHash,"worker deleted listed target in ancestor junction");
   Check(File.Exists(ancestorUnlistedSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ancestorUnlistedSentinel))).ToLowerInvariant()==ancestorUnlistedHash,"worker deleted unlisted target in ancestor junction");
   Check(File.Exists(Path.Combine(targetWv,UserInstallation.RuntimeManifestName)),"worker deleted manifest in ancestor junction target");
   Check(File.ReadAllText(status).Contains("some runtime files were locked or preserved"),"worker status did not reflect ancestor reparse refusal");

   Check(!UserInstallation.CleanupAppLocalRuntimes(install.DirectoryPath), "C# helper allowed ancestor reparse cleanup");
   Check(File.Exists(ancestorListedSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ancestorListedSentinel))).ToLowerInvariant()==ancestorListedHash,"C# helper touched listed target in ancestor junction");
   Check(File.Exists(ancestorUnlistedSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ancestorUnlistedSentinel))).ToLowerInvariant()==ancestorUnlistedHash,"C# helper touched unlisted target in ancestor junction");

   Directory.Delete(install.DirectoryPath); // safe unlink of ancestor/root junction without touching target
   File.Delete(ancestorListedSentinel);
   File.Delete(ancestorUnlistedSentinel);
   File.Delete(Path.Combine(targetWv,UserInstallation.RuntimeManifestName));
   File.Delete(Path.Combine(ownedAncestorTarget,"Remsup.exe"));
   Directory.Delete(targetWv);
   Directory.Delete(targetRuntimes);
   Directory.Delete(ownedAncestorTarget,true);

   // Case 3: Descendant junction inside webview2 + recursive empty dir sweep test
   Directory.CreateDirectory(install.DirectoryPath);
   File.WriteAllText(install.Executable,"fixture");
   Directory.CreateDirectory(runtimeDir);
   string nestedDir = Path.Combine(runtimeDir,"subfolder");
   Directory.CreateDirectory(nestedDir);
   string emptyNestedDir = Path.Combine(runtimeDir,"empty-subfolder");
   Directory.CreateDirectory(emptyNestedDir);
   string regularFile = Path.Combine(nestedDir,"regular.dll");
   File.WriteAllText(regularFile,"regular payload");
   string regularHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(regularFile))).ToLowerInvariant();

   string descendantLink = Path.Combine(nestedDir,"linked-descendant");
   string juncCmdDescendant = "New-Item -ItemType Junction -Path '" + descendantLink.Replace('/', '\\') + "' -Target '" + ownedDescendantTarget.Replace('/', '\\') + "' -Force";
   using(var juncProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + juncCmdDescendant + "\"") { CreateNoWindow=true, UseShellExecute=false })){
    Check(juncProc.WaitForExit(10000),"descendant junction creation timed out");
    Check(juncProc.ExitCode==0,"descendant junction creation failed");
   }
   Check(Directory.Exists(descendantLink)&&new DirectoryInfo(descendantLink).Attributes.HasFlag(FileAttributes.ReparsePoint),"descendant junction not verified as ReparsePoint");

   string descendantManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="subfolder/regular.dll",sha256=regularHash},
     new RuntimeFileEntry{path="subfolder/linked-descendant/descendant-sentinel.txt",sha256=descendantHash}
    }
   });
   File.WriteAllText(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName),descendantManifest);

   install.Register(new InstallChoice{Desktop=false,StartMenu=false},"v1.44",false);
   helper=UninstallManager.Schedule(install,false,status);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"worker descendant reparse test remained running");
   }

   Check(!File.Exists(regularFile),"worker did not delete regular unlinked runtime file");
   Check(!Directory.Exists(emptyNestedDir),"worker recursive sweep did not clean empty subfolder");
   Check(File.Exists(descendantSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(descendantSentinel))).ToLowerInvariant()==descendantHash,"worker touched target through descendant junction");
   Check(Directory.Exists(descendantLink)&&new DirectoryInfo(descendantLink).Attributes.HasFlag(FileAttributes.ReparsePoint),"worker deleted or corrupted descendant junction");
   Check(File.Exists(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName)),"worker deleted manifest when descendant reparse prevented complete cleanup");
   Check(File.ReadAllText(status).Contains("some runtime files were locked or preserved"),"worker status did not reflect preserved descendant reparse");

   Check(!UserInstallation.CleanupAppLocalRuntimes(install.DirectoryPath), "C# helper allowed descendant reparse cleanup");
   Check(File.Exists(descendantSentinel)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(descendantSentinel))).ToLowerInvariant()==descendantHash,"C# helper touched target in descendant junction");

   // Safe unlink and fixture teardown
   Directory.Delete(descendantLink);
   File.Delete(Path.Combine(runtimeDir,UserInstallation.RuntimeManifestName));
   Directory.Delete(nestedDir);
   Directory.Delete(runtimeDir);
   Directory.Delete(Path.Combine(install.DirectoryPath,"runtimes"));
   Directory.Delete(install.DirectoryPath);
   File.Delete(leafListedSentinel);
   File.Delete(leafUnlistedSentinel);
   File.Delete(Path.Combine(ownedLeafTarget,UserInstallation.RuntimeManifestName));
   Directory.Delete(ownedLeafTarget,true);
   File.Delete(descendantSentinel);
   Directory.Delete(ownedDescendantTarget,true);

   // Test 7: settings and data deletion flag
   Directory.CreateDirectory(install.DirectoryPath);
   File.WriteAllText(install.Executable,"fixture");
   string settings=Path.Combine(root,"settings.txt"),data=Path.Combine(root,"vault");
   Directory.CreateDirectory(data);
   File.WriteAllText(settings,"fixture");
   File.WriteAllText(Path.Combine(data,"devices-v2.json"),"fixture");
   File.WriteAllText(Path.Combine(data,"devices.json"),"fixture");
   File.WriteAllText(Path.Combine(data,"unrelated.txt"),"preserve");
   helper=UninstallManager.Schedule(install,true,status,settings,data);
   using(var process=System.Diagnostics.Process.GetProcessById(helper)){
    Check(process.WaitForExit(15000),"explicit data cleanup worker remained running");
   }
   Check(!File.Exists(settings)&&!File.Exists(Path.Combine(data,"devices-v2.json"))&&!File.Exists(Path.Combine(data,"devices.json"))&&File.Exists(Path.Combine(data,"unrelated.txt")),"explicit deletion failed or touched unrelated data");

   // Test 8: production manifest generation, roundtrip verification, and local runtime installation
   string sampleRuntimeDir = Path.Combine(root,"sample-runtime");
   Directory.CreateDirectory(sampleRuntimeDir);
   string sampleSub = Path.Combine(sampleRuntimeDir,"sub");
   Directory.CreateDirectory(sampleSub);
   string sampleBin1 = Path.Combine(sampleRuntimeDir,"msedgewebview2.exe");
   File.WriteAllText(sampleBin1,"binary one content");
   string sampleBin1Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sampleBin1))).ToLowerInvariant();
   string sampleBin2 = Path.Combine(sampleSub,"component.dll");
   File.WriteAllText(sampleBin2,"binary two content");
   string sampleBin2Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sampleBin2))).ToLowerInvariant();

   string generatedManifestPath = UserInstallation.WriteRuntimeManifest(sampleRuntimeDir,"155.0.4283.45");
   Check(File.Exists(generatedManifestPath),"manifest writer failed to produce remsup-runtime.json");
   var genManifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(generatedManifestPath));
   Check(genManifest.schema=="remsup-runtime-v1","schema mismatch in generated manifest");
   Check(genManifest.version=="155.0.4283.45","version mismatch in generated manifest");
   Check(genManifest.files.Count==2,"manifest file count mismatch");
   Check(genManifest.files[0].path=="msedgewebview2.exe"&&genManifest.files[0].sha256==sampleBin1Hash,"file 1 metadata mismatch in generated manifest");
   Check(genManifest.files[1].path=="sub/component.dll"&&genManifest.files[1].sha256==sampleBin2Hash,"file 2 metadata mismatch in generated manifest");

   // Test 8b: InstallAppLocalRuntimes copies validated runtime from portable source
   string portableSourceDir = Path.Combine(root,"portable-source");
   Directory.CreateDirectory(portableSourceDir);
   string portableExe = Path.Combine(portableSourceDir,"Remsup.exe");
   File.WriteAllText(portableExe,"portable fixture");
   string portableRuntimes = Path.Combine(portableSourceDir,"runtimes","webview2");
   Directory.CreateDirectory(portableRuntimes);
   File.Copy(sampleBin1,Path.Combine(portableRuntimes,"msedgewebview2.exe"));
   Directory.CreateDirectory(Path.Combine(portableRuntimes,"sub"));
   File.Copy(sampleBin2,Path.Combine(portableRuntimes,"sub","component.dll"));
   File.Copy(generatedManifestPath,Path.Combine(portableRuntimes,UserInstallation.RuntimeManifestName));

   string freshInstallDir = Path.Combine(root,"installed-app");
   var freshInstall = new UserInstallation(freshInstallDir,desktop,start,key+"Fresh",key+"FreshRun");
   Directory.CreateDirectory(freshInstall.DirectoryPath);
   File.WriteAllText(freshInstall.Executable,"installed fixture");
   Check(freshInstall.InstallAppLocalRuntimes(portableExe),"InstallAppLocalRuntimes failed to copy valid runtime");

   string targetManifest = Path.Combine(freshInstall.DirectoryPath,"runtimes","webview2",UserInstallation.RuntimeManifestName);
   Check(File.Exists(targetManifest),"installed runtime manifest missing");
   Check(File.Exists(Path.Combine(freshInstall.DirectoryPath,"runtimes","webview2","msedgewebview2.exe")),"installed runtime binary missing");
   Check(File.Exists(Path.Combine(freshInstall.DirectoryPath,"runtimes","webview2","sub","component.dll")),"installed runtime sub-binary missing");

   // Test 8c: invalid source (corrupted hash) fails closed before destination writes
   string badSourceDir = Path.Combine(root,"bad-source");
   Directory.CreateDirectory(badSourceDir);
   string badSourceExe = Path.Combine(badSourceDir,"Remsup.exe");
   File.WriteAllText(badSourceExe,"fixture");
   string badSourceRuntimes = Path.Combine(badSourceDir,"runtimes","webview2");
   Directory.CreateDirectory(badSourceRuntimes);
   File.WriteAllText(Path.Combine(badSourceRuntimes,"msedgewebview2.exe"),"corrupt content");
   string badManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="msedgewebview2.exe",sha256="0000000000000000000000000000000000000000000000000000000000000000"}
    }
   });
   File.WriteAllText(Path.Combine(badSourceRuntimes,UserInstallation.RuntimeManifestName),badManifest);
   string targetForBad = Path.Combine(root,"target-for-bad");
   var targetForBadInstall = new UserInstallation(targetForBad,desktop,start,key+"Bad",key+"BadRun");
   Directory.CreateDirectory(targetForBadInstall.DirectoryPath);
   File.WriteAllText(targetForBadInstall.Executable,"fixture");
   Check(!targetForBadInstall.InstallAppLocalRuntimes(badSourceExe),"corrupted source accepted by installer");
   Check(!Directory.Exists(Path.Combine(targetForBadInstall.DirectoryPath,"runtimes")),"files written despite corrupted source");
   Directory.Delete(targetForBad,true);
   Directory.Delete(badSourceDir,true);

   // Test 8d: destination reparse / junction refusal
   string juncTargetDir = Path.Combine(root,"junc-target");
   Directory.CreateDirectory(juncTargetDir);
   string juncInstallDir = Path.Combine(root,"junc-install");
   Directory.CreateDirectory(juncInstallDir);
   string juncRuntimes = Path.Combine(juncInstallDir,"runtimes");
   Directory.CreateDirectory(juncRuntimes);
   string juncCmd = "New-Item -ItemType Junction -Path '" + Path.Combine(juncRuntimes,"webview2").Replace('/', '\\') + "' -Target '" + juncTargetDir.Replace('/', '\\') + "' -Force";
   using(var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + juncCmd + "\""){CreateNoWindow=true,UseShellExecute=false})){
    Check(p.WaitForExit(10000)&&p.ExitCode==0,"junc creation failed");
   }
   var juncInstall = new UserInstallation(juncInstallDir,desktop,start,key+"Junc",key+"JuncRun");
   File.WriteAllText(juncInstall.Executable,"fixture");
   Check(!juncInstall.InstallAppLocalRuntimes(portableExe),"installer wrote to reparse destination");
   Check(Directory.GetFileSystemEntries(juncTargetDir).Length==0,"destination junction target touched");
   Directory.Delete(Path.Combine(juncRuntimes,"webview2"));
   Directory.Delete(juncInstallDir,true);
   Directory.Delete(juncTargetDir,true);

   // Test 8e: destination with pre-existing unlisted file preserves unlisted content on upgrade
   string userDoc = Path.Combine(freshInstall.DirectoryPath,"runtimes","webview2","user-notes.txt");
   File.WriteAllText(userDoc,"important preserved user data");
   string userDocHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(userDoc))).ToLowerInvariant();

   // Prepare an updated runtime source (v2)
   string portableV2Dir = Path.Combine(root,"portable-v2");
   Directory.CreateDirectory(portableV2Dir);
   string portableV2Exe = Path.Combine(portableV2Dir,"Remsup.exe");
   File.WriteAllText(portableV2Exe,"portable v2 fixture");
   string portableV2Runtimes = Path.Combine(portableV2Dir,"runtimes","webview2");
   Directory.CreateDirectory(portableV2Runtimes);
   File.WriteAllText(Path.Combine(portableV2Runtimes,"msedgewebview2.exe"),"v2 binary content");
   string v2Manifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="msedgewebview2.exe",sha256=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("v2 binary content"))).ToLowerInvariant()}
    }
   });
   File.WriteAllText(Path.Combine(portableV2Runtimes,UserInstallation.RuntimeManifestName),v2Manifest);
   Check(freshInstall.InstallAppLocalRuntimes(portableV2Exe),"upgrade failed on valid runtime");
   Check(File.Exists(userDoc)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(userDoc))).ToLowerInvariant()==userDocHash,"unlisted file was deleted during runtime upgrade");
   Directory.Delete(portableV2Dir,true);

   // Test 8f: pre-existing unowned directory (no valid marker) in destination is refused
   string unownedDstDir = Path.Combine(root,"unowned-dst");
   Directory.CreateDirectory(unownedDstDir);
   string unownedWv = Path.Combine(unownedDstDir,"runtimes","webview2");
   Directory.CreateDirectory(unownedWv);
   string unownedSentinel = Path.Combine(unownedWv,"unowned-data.txt");
   File.WriteAllText(unownedSentinel,"unowned foreign directory");
   var unownedInstall = new UserInstallation(unownedDstDir,desktop,start,key+"Unowned",key+"UnownedRun");
   File.WriteAllText(unownedInstall.Executable,"fixture");
   Check(!unownedInstall.InstallAppLocalRuntimes(portableExe),"installer overwrote unowned directory without marker");
   Check(File.Exists(unownedSentinel),"unowned foreign sentinel was deleted");
   Directory.Delete(unownedDstDir,true);

   // Test 8g: destination descendant reparse is refused fail-closed
   string descendantReparseDst = Path.Combine(root,"desc-reparse-dst");
   Directory.CreateDirectory(descendantReparseDst);
   string descRuntimes = Path.Combine(descendantReparseDst,"runtimes","webview2");
   Directory.CreateDirectory(descRuntimes);
   File.WriteAllText(Path.Combine(descRuntimes,"msedgewebview2.exe"),"fixture content");
   string descValidManifest = JsonSerializer.Serialize(new RuntimeManifest{
    schema="remsup-runtime-v1",
    version="155.0.4283.45",
    files=new System.Collections.Generic.List<RuntimeFileEntry>{
     new RuntimeFileEntry{path="msedgewebview2.exe",sha256=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fixture content"))).ToLowerInvariant()}
    }
   });
   File.WriteAllText(Path.Combine(descRuntimes,UserInstallation.RuntimeManifestName),descValidManifest);
   string linkedDesc = Path.Combine(descRuntimes,"reparse-link");
   string extTarget = Path.Combine(root,"ext-target");
   Directory.CreateDirectory(extTarget);
   string juncCmdDesc = "New-Item -ItemType Junction -Path '" + linkedDesc.Replace('/', '\\') + "' -Target '" + extTarget.Replace('/', '\\') + "' -Force";
   using(var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + juncCmdDesc + "\""){CreateNoWindow=true,UseShellExecute=false})){
    Check(p.WaitForExit(10000)&&p.ExitCode==0,"desc junc creation failed");
   }
   var descReparseInstall = new UserInstallation(descendantReparseDst,desktop,start,key+"DescReparse",key+"DescReparseRun");
   File.WriteAllText(descReparseInstall.Executable,"fixture");
   Check(!descReparseInstall.InstallAppLocalRuntimes(portableExe),"installer accepted destination with descendant reparse point");
   Directory.Delete(linkedDesc);
   Directory.Delete(extTarget,true);
   Directory.Delete(descendantReparseDst,true);

   // Test 8h: same-path runtime staging produces NoOp state and rollback does NOT delete runtime
   string freshWv = Path.Combine(freshInstall.DirectoryPath,"runtimes","webview2");
   Check(UserInstallation.StageRuntimePackage(freshWv,freshWv,out string sameBackup,out var sameState),"same-path staging returned false");
   Check(sameState==RuntimeTransactionState.NoOp,"same-path transaction state was not NoOp");
   Check(sameBackup==null,"same-path backup was not null");
   Check(UserInstallation.RollbackStagedRuntime(freshWv,sameBackup,sameState),"same-path rollback returned false");
   Check(Directory.Exists(freshWv)&&File.Exists(Path.Combine(freshWv,"msedgewebview2.exe")),"same-path rollback deleted runtime directory");
   Check(File.Exists(userDoc),"same-path rollback deleted unlisted user document");

   // Verify same-path NoOp fails closed on wrong expected version or wrong expected manifest hash
   Check(!UserInstallation.StageRuntimePackage(freshWv,freshWv,out _,out _,"wrong-version",null),"same-path NoOp accepted wrong expected version");
   Check(!UserInstallation.StageRuntimePackage(freshWv,freshWv,out _,out _,null,"0000000000000000000000000000000000000000000000000000000000000000"),"same-path NoOp accepted wrong expected manifest hash");

   // Test 8i: failed rollback preserves recoverable bytes and does not delete candidate runtime
   string failRollbackDir = Path.Combine(root,"fail-rollback");
   Directory.CreateDirectory(failRollbackDir);
   string failWv = Path.Combine(failRollbackDir,"runtimes","webview2");
   Directory.CreateDirectory(failWv);
   string failMain = Path.Combine(failWv,"msedgewebview2.exe");
   File.WriteAllText(failMain,"candidate bytes to preserve on recovery failure");
   // Call rollback with Replaced state but missing oldBackupPath
   bool rollbackFailed = UserInstallation.RollbackStagedRuntime(failWv,Path.Combine(root,"missing-backup-folder"),RuntimeTransactionState.Replaced);
   Check(!rollbackFailed,"RollbackStagedRuntime reported true when old backup was missing");
   // Verify recoverable bytes were NOT deleted
   string[] lingeringDirs = Directory.GetDirectories(Path.Combine(failRollbackDir,"runtimes"),"webview2.del-*");
   Check(lingeringDirs.Length == 1 && File.Exists(Path.Combine(lingeringDirs[0],"msedgewebview2.exe")),"RollbackStagedRuntime deleted recoverable candidate bytes on recovery failure");
   Directory.Delete(failRollbackDir,true);

   // Teardown test 8 fixtures
   Directory.Delete(freshInstallDir,true);
   Directory.Delete(portableSourceDir,true);
   Directory.Delete(sampleRuntimeDir,true);

   Console.WriteLine("Per-user installation, icon choices, stable shortcuts/version, unregister and locked/unrelated cleanup PASS");
  }finally{
   Registry.CurrentUser.DeleteSubKeyTree(key,false);
   Registry.CurrentUser.DeleteSubKeyTree(key+"Run",false);
   try{Directory.Delete(root,true);}catch{}
  }
 }
 static string Target(string path){
  dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
  dynamic shortcut=shell.CreateShortcut(path);
  try{return shortcut.TargetPath;}finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);}
 }
}
