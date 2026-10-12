param([Parameter(Mandatory=$true)][string]$Name,[string]$DeviceId,[string]$StorePath,[string]$PermissionFile)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'profile-policy.ps1')
$profile=& (Join-Path $PSScriptRoot 'saved-profile.ps1') -Name $Name -DeviceId $DeviceId -StorePath $StorePath
[pscustomobject]@{testPermission=(Get-ProfilePermission $profile $PermissionFile)}|ConvertTo-Json -Depth 6
