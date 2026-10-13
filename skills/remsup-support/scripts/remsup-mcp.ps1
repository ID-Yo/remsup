param(
 [ValidateSet('health','exec','diagnostics','screen')][string]$Operation='health',
 [ValidatePattern('^[a-zA-Z0-9_-]{1,40}$')][string]$Agent='codex',
 [string]$Name,[string]$DeviceId,
 [string]$Command,[string]$CommandFile,[string]$MonitorId,[switch]$Control,[switch]$CapabilitiesOnly,
 [string]$ImageDirectory,[string]$ScreenActionsFile,[scriptblock]$ScreenHandler,
 [string]$Python,[string]$StorePath,[string]$CredentialFile,
 [ValidateRange(0,65535)][int]$LocalPort=0,
 [ValidateRange(1,90)][int]$TimeoutSeconds=45
)
$ErrorActionPreference='Stop'
if($Operation -ne 'health' -and -not $Name){throw 'Device operations require exact profile'}
if(-not $CredentialFile){$CredentialFile=Join-Path $env:USERPROFILE ".codex/secrets/remsup-mcp-$Agent.dpapi"}
if(-not (Test-Path -LiteralPath $CredentialFile)){throw 'Protected MCP token file is missing for this Windows user. Run setup-remsup-mcp.ps1 with the administrator supplied token and route.'}
. (Join-Path $PSScriptRoot 'mcp-runtime.ps1')
$Python=Resolve-McpPython $Python
Add-Type -AssemblyName System.Security
$clear=$null;$token=$null;$process=$null
$outcome=@{Failed=$false}
try {
 $clear=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($CredentialFile),[Text.Encoding]::UTF8.GetBytes("Remsup.Mcp.$Agent.v1"),[Security.Cryptography.DataProtectionScope]::CurrentUser)
 $config=[Text.Encoding]::UTF8.GetString($clear)|ConvertFrom-Json
 Assert-McpSshAlias $config.ssh_alias
 $token=$config.token
 if([string]::IsNullOrWhiteSpace($token)){throw 'Empty provisioned bearer credential'}
 if($CommandFile){$Command=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $CommandFile))}
 if($Operation -eq 'exec' -and [string]::IsNullOrWhiteSpace($Command)){throw 'exec requires Command or CommandFile'}
 if($Operation -eq 'screen' -and -not $CapabilitiesOnly -and (-not $MonitorId -or -not $ImageDirectory -or $ImageDirectory -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+\\)')){throw 'screen requires capabilities monitor and absolute private ImageDirectory'}
 $port=if($PSBoundParameters.ContainsKey('LocalPort')){$LocalPort}elseif($config.local_port_explicit -or ($config.local_port -and $config.local_port -ne 18765)){$config.local_port}else{0}
 $request=@{operation=$Operation;token=$token;config=@{ssh_alias=$config.ssh_alias;endpoint=$config.endpoint;local_port=$port};route=$config.route;timeout=$TimeoutSeconds}
 if($Operation -eq 'exec'){
  $request.command=$Command
 }
 if($Operation -eq 'screen'){
  $request.monitor_id=$MonitorId;$request.control=[bool]$Control;$request.capabilities_only=[bool]$CapabilitiesOnly;$request.image_directory=$ImageDirectory
  if($ScreenActionsFile){$request.screen_actions=@([IO.File]::ReadAllText((Resolve-Path -LiteralPath $ScreenActionsFile))|ConvertFrom-Json)}
  $request.interactive=(-not $CapabilitiesOnly -and -not $ScreenActionsFile)
 }
 function Invoke-Consumer($profile,$password){
  if($profile){
   $request.profile=$profile;$request.otp=$password
   if($profile.RelayMode -eq 'Default'){
    $path=Join-Path $env:ProgramData 'RemsupPortable/settings.txt';$hostValue=$config.route.profile_host;$portValue=$config.route.profile_port
    if(Test-Path -LiteralPath $path){foreach($line in [IO.File]::ReadLines($path)){if($line -match '^RelayHost=(.*)$'){$hostValue=$Matches[1].Trim()};if($line -match '^RelayPort=(\d+)$'){$portValue=[int]$Matches[1]}}}
    if($hostValue -eq $config.route.profile_host -and $portValue -eq $config.route.relay_port){$portValue=$config.route.profile_port}
    if($hostValue -ne $config.route.profile_host -or $portValue -ne $config.route.profile_port){throw 'Default profile route differs from provisioned MCP route'}
   }
  }
  $start=[Diagnostics.ProcessStartInfo]::new($Python);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
  # Script path only: secrets are written solely to the child's private stdin.
  $consumer=Join-Path $PSScriptRoot 'remsup_mcp_client.py'
  if($start.PSObject.Properties.Name -contains 'ArgumentList'){$start.ArgumentList.Add('-B');$start.ArgumentList.Add($consumer)}else{$start.Arguments='-B "'+$consumer+'"'}
  # Framework PowerShell derives its redirected writer encoding at Process.Start.
  $originalEncoding=[Console]::InputEncoding
  try{[Console]::InputEncoding=[Text.UTF8Encoding]::new($false);$process=[Diagnostics.Process]::Start($start)}finally{[Console]::InputEncoding=$originalEncoding}
  $stderr=$process.StandardError.ReadToEndAsync();$inputWriter=[IO.StreamWriter]::new($process.StandardInput.BaseStream,[Text.UTF8Encoding]::new($false),1024,$false);$inputWriter.AutoFlush=$true
  try {
   $inputWriter.WriteLine(($request|ConvertTo-Json -Depth 12 -Compress))
   if(-not $request.interactive){$inputWriter.Close()}
   $deadline=[DateTime]::UtcNow.AddSeconds($(if($request.interactive){650}else{[Math]::Min(320,$TimeoutSeconds*5+20)}))
   $success=$false
   while($true){
    $read=$process.StandardOutput.ReadLineAsync();$remaining=[Math]::Max(1,[int]($deadline-[DateTime]::UtcNow).TotalMilliseconds)
    if(-not $read.Wait($remaining)){throw 'Protected client timeout; no automatic retry; remote outcome may be unknown'}
    $line=$read.Result;if($null -eq $line){break}
    foreach($secret in @($token,$password)){if($secret){$line=$line.Replace($secret,'[REDACTED]')}}
    Write-Output $line
    $event=$line|ConvertFrom-Json
    if($event.ok -eq $true){$success=$true}
    if($event.next){$answer=if($ScreenHandler){& $ScreenHandler $event}else{Read-Host 'Screen action JSON (settings-read or settings-set needs exact name/value; inspect image before input), or {"action":"close"}'};if(-not $answer){$answer='{"action":"close"}'};if($answer -isnot [string]){$answer=$answer|ConvertTo-Json -Depth 8 -Compress};$inputWriter.WriteLine($answer)}
   }
   if(-not $process.WaitForExit(5000)){throw 'Protected client did not exit'}
   # Do not throw inside the credential callback: PS7 can discard its pipeline output.
   if($process.ExitCode -ne 0 -or -not $success){$outcome.Failed=$true}
  } finally {
   if(-not $process.HasExited){$inputWriter.Close();if(-not $process.WaitForExit(5000)){if($PSVersionTable.PSVersion.Major -ge 7){$process.Kill($true)}else{& taskkill.exe /PID $process.Id /T /F | Out-Null}}}
   $process.Dispose();$process=$null;$request.otp=$null
  }
 }
 if($Operation -eq 'health'){Invoke-Consumer $null $null}else{& (Join-Path $PSScriptRoot 'saved-profile.ps1') -Name $Name -DeviceId $DeviceId -StorePath $StorePath -UseCredential {param($metadata,$password) Invoke-Consumer $metadata $password}}
 if($outcome.Failed){throw 'MCP operation failed; sanitized result above; no automatic retry'}
} finally {if($clear){[Array]::Clear($clear,0,$clear.Length)};$token=$null;$config=$null;$request=$null}
