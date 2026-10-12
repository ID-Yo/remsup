function Get-PolicyPath([string]$Path,[string]$Filename){
 if(-not $Path){$Path=Join-Path $env:LOCALAPPDATA ('Remsup/Operator/'+$Filename)}
 if($Path -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+\\)'){throw 'Local profile policy requires an absolute path'}
 $Path=[IO.Path]::GetFullPath($Path)
 foreach($start in @($PSScriptRoot,(Get-Location).Path)){
  $directory=$start
  while($directory){
   if((Test-Path -LiteralPath (Join-Path $directory '.git')) -and ($Path -eq $directory -or $Path.StartsWith($directory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase))){throw 'Private profile data must stay outside the repository'}
   $directory=Split-Path -Parent $directory
  }
 }
 $skill=Split-Path -Parent $PSScriptRoot
 if((Test-Path -LiteralPath (Join-Path $skill 'SKILL.md')) -and $Path.StartsWith($skill+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Private profile data must stay outside the skill'}
 return $Path
}
function Read-ProfilePolicy([string]$Path){
 if(-not (Test-Path -LiteralPath $Path)){return [pscustomobject]@{version=1;records=@()}}
 if((Get-Item -LiteralPath $Path).Length -gt 1MB){throw 'Local profile policy exceeds size limit'}
 try{$document=[IO.File]::ReadAllText($Path)|ConvertFrom-Json}catch{throw 'Local profile policy is unreadable; preserve it and ask the owner'}
 if($document.version -ne 1 -or $null -eq $document.records){throw 'Unsupported local profile policy; no reset or migration'}
 return $document
}
function Find-ProfileRecord($Profile,$Document){
 $records=@($Document.records|Where-Object {$_.vaultPath -ieq $Profile.StorePath -and $_.profileName -ieq $Profile.DisplayName -and $_.deviceId -ceq $Profile.DeviceId})
 if($records.Count -gt 1){throw 'Ambiguous local profile policy; no record selected'}
 if($records.Count){return $records[0]}
}
function Save-ProfileRecord($Profile,$Record,[string]$Path){
 $document=Read-ProfilePolicy $Path;$existing=Find-ProfileRecord $Profile $document
 $document.records=@($document.records|Where-Object {$_ -ne $existing})+@($Record)
 $directory=Split-Path -Parent $Path;New-Item -ItemType Directory -Path $directory -Force|Out-Null
 $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
 try{
  [IO.File]::WriteAllText($temporary,($document|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
  $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetAccessRuleProtection($true,$false)
  $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
  $acl.SetOwner($sid);$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow'))
  Set-Acl -LiteralPath $temporary -AclObject $acl
  if(Test-Path -LiteralPath $Path){[IO.File]::Replace($temporary,$Path,[Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temporary,$Path)}
  Set-Acl -LiteralPath $Path -AclObject $acl
 }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
}
function Get-ProfilePermission($Profile,[string]$Path){
 $Path=Get-PolicyPath $Path 'test-permissions.json'
 return Find-ProfileRecord $Profile (Read-ProfilePolicy $Path)
}
