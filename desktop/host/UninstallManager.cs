using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;

internal static class UninstallManager {
 internal static string EventName=>@"Local\Remsup.Uninstall."+WindowsIdentity.GetCurrent().User.Value;
 internal static bool RequestExisting(){try{using var request=EventWaitHandle.OpenExisting(EventName);request.Set();return true;}catch(WaitHandleCannotBeOpenedException){return false;}}
 static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
 internal static int Schedule(UserInstallation installation,bool deleteData,string statusPath=null,string settingsPath=null,string dataDirectory=null){
  if(!File.Exists(installation.Executable))throw new IOException("No installed Remsup app was found.");
  statusPath??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Remsup","uninstall-status.txt");
  installation.RemoveRegistration();
  string script=Path.Combine(Path.GetTempPath(),"Remsup-uninstall-"+Guid.NewGuid().ToString("N")+".ps1");
  string data=dataDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Remsup","Operator");
  string settings=settingsPath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemsupPortable","settings.txt");
  string body="$ErrorActionPreference='Stop'\n$folder="+Quote(installation.DirectoryPath)+"\n$deadline=[DateTime]::UtcNow.AddSeconds(90)\ndo {\n foreach($file in Get-ChildItem -LiteralPath $folder -File -ErrorAction SilentlyContinue){\n  if($file.Name -in @('Remsup.exe','Remsup.previous.exe','installation.json','Remsup.exe.cleanup.json') -or $file.Name -match '^Remsup\\.exe\\.old-[a-f0-9]{32}$'){try{Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop}catch{}}\n }\n if(-not (Test-Path -LiteralPath (Join-Path $folder 'Remsup.exe'))){break}\n Start-Sleep -Milliseconds 500\n}while([DateTime]::UtcNow -lt $deadline)\n";
  if(deleteData)body+="if(-not (Test-Path -LiteralPath (Join-Path $folder 'Remsup.exe'))){Remove-Item -LiteralPath "+Quote(settings)+" -Force -ErrorAction SilentlyContinue; foreach($name in @('devices-v2.json','devices.json')){Remove-Item -LiteralPath (Join-Path "+Quote(data)+" $name) -Force -ErrorAction SilentlyContinue}}\n";
  body+="if((Test-Path -LiteralPath $folder) -and -not (Get-ChildItem -LiteralPath $folder -Force)){Remove-Item -LiteralPath $folder}\nRemove-Item -LiteralPath $PSCommandPath -Force\n";
  body="$status="+Quote(statusPath)+"\nNew-Item -ItemType Directory -Path (Split-Path -Parent $status) -Force | Out-Null\nSet-Content -LiteralPath $status -Value 'Uninstall cleanup running'\n"+body;
  body+="if(Test-Path -LiteralPath (Join-Path $folder 'Remsup.exe')){Set-Content -LiteralPath $status -Value 'Cleanup incomplete: installed executable remains locked; exit Remsup and retry uninstall.'}else{Set-Content -LiteralPath $status -Value 'Remsup removed; unrelated installation-folder files were preserved.'}\n";
  if(deleteData)body+="if((Test-Path -LiteralPath "+Quote(settings)+") -or (Test-Path -LiteralPath (Join-Path "+Quote(data)+" 'devices-v2.json')) -or (Test-Path -LiteralPath (Join-Path "+Quote(data)+" 'devices.json'))){Add-Content -LiteralPath $status -Value 'Requested settings/device cleanup is incomplete; files remain locked or inaccessible.'}\n";
  File.WriteAllText(script,body);
  var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-File");start.ArgumentList.Add(script);using var process=Process.Start(start);return process.Id;
 }
}
