param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputRoot){throw 'Use new fixture output'}
New-Item -ItemType Directory -Path $OutputRoot|Out-Null
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'skills/remsup-support/scripts/profile-policy.ps1')
$path=Join-Path $OutputRoot 'permissions.json'
$profile=[pscustomobject]@{StorePath=(Join-Path $OutputRoot 'vault.json');DisplayName='fixture';DeviceId='123456789'}
$other=[pscustomobject]@{StorePath=$profile.StorePath;DisplayName='fixture';DeviceId='987654321'}
if(Get-ProfilePermission $profile $path){throw 'Missing scope invented'}
$record=[pscustomobject]@{vaultPath=$profile.StorePath;profileName=$profile.DisplayName;deviceId=$profile.DeviceId;scopes=@('fixture-only');exclusions=@('all other actions');source='synthetic owner instruction'}
Save-ProfileRecord $profile $record $path
if((Get-ProfilePermission $profile $path).scopes[0] -ne 'fixture-only' -or (Get-ProfilePermission $other $path)){throw 'Permission crossed profiles'}
$acl=Get-Acl -LiteralPath $path
if(-not $acl.AreAccessRulesProtected -or @($acl.Access).Count -ne 1){throw 'Permission file ACL not private'}
function Refused([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};if(-not $failed){throw 'Expected safe refusal'}}
Refused {Save-ProfileRecord $profile $record (Get-PolicyPath (Join-Path $root 'private-test.json') 'test-permissions.json')}
Refused {Get-PolicyPath 'relative.json' 'test-permissions.json'}
$document=Get-Content $path -Raw|ConvertFrom-Json
$document.records+=@($document.records[0]);$document|ConvertTo-Json -Depth 6|Set-Content $path
Refused {Get-ProfilePermission $profile $path}
Set-Content $path '{broken'
Refused {Get-ProfilePermission $profile $path}
'PASS local profile-bound scope, missing/duplicate/corrupt refusal and repository exclusion'