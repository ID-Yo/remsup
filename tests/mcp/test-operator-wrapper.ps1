param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
if($OutputRoot -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+\\)' -or (Test-Path $OutputRoot)){throw 'Use a new absolute owned output root'}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Copy-Item -LiteralPath (Join-Path $root 'skills/remsup-support/scripts/saved-profile.ps1'),(Join-Path $root 'skills/remsup-support/scripts/remsup-mcp.ps1'),(Join-Path $root 'skills/remsup-support/scripts/mcp-runtime.ps1'),(Join-Path $root 'skills/remsup-support/scripts/requirements.txt'),(Join-Path $root 'skills/remsup-support/scripts/check-mcp-runtime.py') -Destination $OutputRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'operator_wrapper_probe.py') -Destination (Join-Path $OutputRoot 'remsup_mcp_client.py')
Add-Type -AssemblyName System.Security
function Protect($text,$entropy){[Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($text),[Text.Encoding]::UTF8.GetBytes($entropy),[Security.Cryptography.DataProtectionScope]::CurrentUser)}
$password='WRAPPER_PASSWORD_SENTINEL';$token='WRAPPER_TOKEN_SENTINEL';$credential=Join-Path $OutputRoot 'codex.dpapi';$store=Join-Path $OutputRoot 'devices-v2.json'
$config=@{token=$token;local_port=18765;route=@{relay_host='127.0.0.1';relay_port=8443;profile_host='relay.example.invalid';profile_port=443}}
[IO.File]::WriteAllBytes($credential,(Protect ($config|ConvertTo-Json -Depth 5 -Compress) 'Remsup.Mcp.codex.v1'))
@{version=2;records=@(@{displayName='fixture';deviceId='123456789';transport='Relay';relayMode='Custom';host='relay.example.invalid';port=443;protectedPassword=[Convert]::ToBase64String((Protect $password 'Remsup.Operator.DeviceStore.v1'))})}|ConvertTo-Json -Depth 5|Set-Content $store
$fixtureWrapper=Join-Path $OutputRoot 'remsup-mcp.ps1'
# First prove the synthetic vault is decryptable by this exact runner user.
$decrypts=& (Join-Path $OutputRoot 'saved-profile.ps1') -Name fixture -DeviceId 123456789 -StorePath $store -UseCredential {param($metadata,$secret) $secret -ceq 'WRAPPER_PASSWORD_SENTINEL'}
if($decrypts -ne $true){throw 'Synthetic CurrentUser DPAPI vault roundtrip failed'}
$fixtureHelper=Join-Path $OutputRoot 'saved-profile.ps1'
$previousInputEncoding=[Console]::InputEncoding
[Console]::InputEncoding=[Text.UTF8Encoding]::new($true)
$output=[Collections.Generic.List[string]]::new()
& (Join-Path $OutputRoot 'remsup-mcp.ps1') -Python python -Operation exec -Name fixture -DeviceId 123456789 -Command ('echo '+[string]([char]0x43f)+[char]0x440+[char]0x43e+[char]0x432+[char]0x435+[char]0x440+[char]0x43a+[char]0x430) -CredentialFile $credential -StorePath $store | ForEach-Object {$output.Add([string]$_);Write-Host ([string]$_)}
[Console]::InputEncoding=$previousInputEncoding
$text=$output -join "`n"
if($text.Contains($password) -or $text.Contains($token)){throw 'Credential leaked from wrapper'}
$result=$text|ConvertFrom-Json
if(-not ($result.argv_clean -and $result.env_clean -and $result.stdin_credentials -and $result.unicode_command)){throw 'Protected stdin consumer not proved'}
if((Get-ChildItem $OutputRoot -File|Where-Object Extension -eq '.json'|ForEach-Object {Get-Content $_.FullName -Raw}) -match $password){throw 'Plaintext credential in output files'}
'PASS DPAPI wrapper delivers credentials through stdin only; argv/env/output clean'
$diag=& $fixtureWrapper -Python python -Operation diagnostics -Name fixture -DeviceId 123456789 -CredentialFile $credential -StorePath $store|ConvertFrom-Json
if($diag.operation -ne 'diagnostics' -or -not $diag.diagnostics_command_absent){throw 'Protected diagnostics operation included a shell command'}
'PASS diagnostics wrapper operation passes no command text'
$explicit=& $fixtureWrapper -Python python -Operation exec -Name fixture -DeviceId 123456789 -Command 'echo explicit-port' -LocalPort 24680 -CredentialFile $credential -StorePath $store|ConvertFrom-Json
if(-not $result.port_selection -or -not $explicit.port_selection){throw 'Automatic/explicit tunnel port selection failed'}
$config.local_port=24680
[IO.File]::WriteAllBytes($credential,(Protect ($config|ConvertTo-Json -Depth 5 -Compress) 'Remsup.Mcp.codex.v1'))
$configured=& $fixtureWrapper -Python python -Operation exec -Name fixture -DeviceId 123456789 -Command 'echo explicit-port' -CredentialFile $credential -StorePath $store|ConvertFrom-Json
if(-not $configured.port_selection){throw 'Legacy custom configured port was discarded'}
'PASS legacy stored port ignored by default and explicit invocation override retained'
function Assert-SetupError([scriptblock]$Action,[string]$Pattern){
 $message=$null
 try {& $Action | Out-Null} catch {$message=$_.Exception.Message}
 if(-not $message -or $message -notmatch $Pattern){throw "Expected actionable setup error: $Pattern"}
}
# Real subprocesses prove failure output survives both shell pipeline implementations.
$failureProbe=@'
import json,sys
request=json.loads(sys.stdin.readline())
print(json.dumps({'ok':False,'error':'no-agent: '+request['otp']+' '+request['token']}),flush=True)
sys.exit(1)
'@
[IO.File]::WriteAllText((Join-Path $OutputRoot 'remsup_mcp_client.py'),$failureProbe,[Text.UTF8Encoding]::new($false))
$runner=Join-Path $OutputRoot 'failure-runner.ps1'
$badStore=Join-Path $OutputRoot 'bad-vault.json'
$badDocument=Get-Content -LiteralPath $store -Raw|ConvertFrom-Json
$badDocument.records[0].protectedPassword=[Convert]::ToBase64String([byte[]]@(1,2,3))
$badDocument|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $badStore
[IO.File]::WriteAllText($runner,@'
param($Wrapper,$Credential,$Store)
$ErrorActionPreference='Stop'
try {& $Wrapper -Python python -Operation exec -Command "echo fixture" -Name fixture -DeviceId 123456789 -CredentialFile $Credential -StorePath $Store} catch {Write-Output ('FAILURE: '+$_.Exception.Message);exit 1}
'@,[Text.UTF8Encoding]::new($false))
foreach($shell in @('powershell.exe','pwsh.exe')){
 $shellPath=(Get-Command $shell -ErrorAction Stop).Source
 $start=[Diagnostics.ProcessStartInfo]::new($shellPath);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $start.Arguments='-NoProfile -File "'+$runner+'" "'+$fixtureWrapper+'" "'+$credential+'" "'+$store+'"'
 $child=[Diagnostics.Process]::Start($start)
 try{
  $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(30000)){throw 'Failure-output fixture timed out'}
  $text=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
  if($child.ExitCode -eq 0 -or $text -notmatch '"ok": false' -or $text -notmatch 'no-agent' -or $text -match 'Saved-credential operation failed'){throw "Sanitized client result lost under $shell"}
  if($text.Contains($password) -or $text.Contains($token)){throw "Failure output leaked credential under $shell"}
 }finally{if(-not $child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
 "PASS $shell preserves sanitized no-agent JSON and failed exit"
 $start.Arguments='-NoProfile -File "'+$runner+'" "'+$fixtureWrapper+'" "'+$credential+'" "'+$badStore+'"'
 $child=[Diagnostics.Process]::Start($start)
 try{
  $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(30000)){throw 'Vault failure fixture timed out'}
  $text=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
  if($child.ExitCode -eq 0 -or $text -notmatch 'Saved-credential operation failed' -or $text -match 'no-agent'){throw "DPAPI error misclassified under $shell"}
  if($text.Contains($password) -or $text.Contains($token)){throw "Vault failure leaked credential under $shell"}
 }finally{if(-not $child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
 "PASS $shell retains generic vault error only for failed DPAPI"
}
$callbackError=$null
try{& $fixtureHelper -Name fixture -DeviceId 123456789 -StorePath $store -UseCredential {param($metadata,$secret) throw $secret}|Out-Null}catch{$callbackError=$_.Exception.Message}
if($callbackError -notmatch 'Protected credential consumer failed' -or $callbackError.Contains($password) -or $callbackError -match 'Saved-credential operation failed'){throw 'Unsafe callback failure classification'}
'PASS untrusted callback error is suppressed without claiming vault failure'
Assert-SetupError {& (Join-Path $OutputRoot 'remsup-mcp.ps1') -CredentialFile (Join-Path $OutputRoot 'missing.dpapi')} 'token file is missing.*setup-remsup-mcp'
. (Join-Path $OutputRoot 'mcp-runtime.ps1')
Assert-SetupError {Resolve-McpPython (Join-Path $OutputRoot 'missing-python.exe')} 'environment is missing.*setup-remsup-mcp'
Assert-SetupError {Assert-McpSshAlias 'remsup-nonexistent-test-alias-57'} 'SSH .*missing.*setup-remsup-mcp'
$emptyVenv=Join-Path $OutputRoot 'without-mcp'
& python -m venv --without-pip $emptyVenv
if($LASTEXITCODE -ne 0){throw 'Could not create dependency fixture'}
Assert-SetupError {Resolve-McpPython (Join-Path $emptyVenv 'Scripts/python.exe')} 'mcp==1.27.0.*pip install -r'
'PASS actionable missing token/runtime/SSH alias/pinned SDK preflight'
# Expected negative Python checks set this variable; the test itself passed.
$global:LASTEXITCODE=0
