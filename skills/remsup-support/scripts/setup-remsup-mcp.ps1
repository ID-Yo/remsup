param(
 [ValidatePattern('^[a-zA-Z0-9_-]{1,40}$')][string]$Agent='codex',
 [string]$SshAlias,[string]$Endpoint,[ValidateRange(0,65535)][int]$LocalPort=0,
 [Parameter(Mandatory=$true)][string]$RelayHost,[Parameter(Mandatory=$true)][ValidateRange(1,65535)][int]$RelayPort,
 [Parameter(Mandatory=$true)][string]$ProfileHost,[Parameter(Mandatory=$true)][ValidateRange(1,65535)][int]$ProfilePort,
 [Security.SecureString]$Token,[string]$Python,[string]$CredentialFile
)
$ErrorActionPreference='Stop'
if([bool]$SshAlias -eq [bool]$Endpoint){throw 'Choose one provisioned non-root SSH alias or verified HTTPS endpoint'}
if(-not $CredentialFile){$CredentialFile=Join-Path $env:USERPROFILE ".codex/secrets/remsup-mcp-$Agent.dpapi"}
. (Join-Path $PSScriptRoot 'mcp-runtime.ps1')
Assert-McpSshAlias $SshAlias
$Python=Resolve-McpPython $Python -Setup
if(Test-Path -LiteralPath $CredentialFile){
 & (Join-Path $PSScriptRoot 'remsup-mcp.ps1') -Operation health -Agent $Agent -Python $Python -CredentialFile $CredentialFile
 return
}
if(-not $Token){$Token=Read-Host 'Provisioned per-agent MCP bearer token' -AsSecureString}
Add-Type -AssemblyName System.Security
$pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($Token);$bytes=$null;$secret=$null
try {
 $secret=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
 if([string]::IsNullOrWhiteSpace($secret)){throw 'Bearer token is required'}
 $value=@{token=$secret;ssh_alias=$SshAlias;endpoint=$Endpoint;local_port=$LocalPort;local_port_explicit=$PSBoundParameters.ContainsKey('LocalPort');route=@{relay_host=$RelayHost;relay_port=$RelayPort;profile_host=$ProfileHost;profile_port=$ProfilePort}}
 $bytes=[Text.Encoding]::UTF8.GetBytes(($value|ConvertTo-Json -Depth 4 -Compress));$cipher=[Security.Cryptography.ProtectedData]::Protect($bytes,[Text.Encoding]::UTF8.GetBytes("Remsup.Mcp.$Agent.v1"),[Security.Cryptography.DataProtectionScope]::CurrentUser)
 $directory=Split-Path -Parent $CredentialFile;New-Item -ItemType Directory -Path $directory -Force|Out-Null
 # Create atomically and restrict the encrypted file to the current Windows identity.
 $stream=[IO.File]::Open($CredentialFile,[IO.FileMode]::CreateNew);try{$stream.Write($cipher,0,$cipher.Length)}finally{$stream.Dispose()}
 $acl=Get-Acl -LiteralPath $CredentialFile;$acl.SetAccessRuleProtection($true,$false);$identity=[Security.Principal.WindowsIdentity]::GetCurrent().User;$acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','Allow'));Set-Acl -LiteralPath $CredentialFile -AclObject $acl
 & (Join-Path $PSScriptRoot 'remsup-mcp.ps1') -Operation health -Agent $Agent -Python $Python -CredentialFile $CredentialFile
 if($LASTEXITCODE){throw 'Provisioned connection health failed; protected file retained for diagnosis'}
} finally {if($bytes){[Array]::Clear($bytes,0,$bytes.Length)};[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer);$secret=$null;$Token=$null;$value=$null}
