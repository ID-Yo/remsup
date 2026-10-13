param(
 [string]$Dotnet='dotnet',[string]$Python='python',[string]$NodeDirectory,
 [string]$WslDistribution,[Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)){throw 'Use a new absolute task-owned CI output directory'}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if($OutputRoot.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'CI output must stay outside the checkout'}
$commit=git -C $root rev-parse HEAD
if(git -C $root status --porcelain){throw 'Commit the complete change before running exact-head CI'}
New-Item -ItemType Directory -Path $OutputRoot|Out-Null
$originalPath=$env:PATH
$pythonPath=(Get-Command $Python -ErrorAction Stop).Source
$env:PATH=(Split-Path -Parent $pythonPath)+';'+$(if($NodeDirectory){$NodeDirectory+';'})+$env:PATH
$steps=[Collections.Generic.List[object]]::new();$clock=[Diagnostics.Stopwatch]::StartNew()
$before=@(Get-CimInstance Win32_Process|Select-Object -ExpandProperty ProcessId)
function Native([string]$Executable,[string[]]$Arguments){& $Executable @Arguments 2>&1|ForEach-Object {Write-Host $_};if($LASTEXITCODE -ne 0){throw "$Executable exited $LASTEXITCODE"}}
function Step([string]$Name,[scriptblock]$Action){
 $timer=[Diagnostics.Stopwatch]::StartNew();$status='PASS';$reason=$null
 Write-Host "START $Name"
 try{& $Action|ForEach-Object {Write-Host $_}}catch{$status='FAIL';$reason=$_.Exception.Message;Write-Host $reason}
 $steps.Add([pscustomobject]@{step=$Name;status=$status;seconds=[Math]::Round($timer.Elapsed.TotalSeconds,2);error=$reason})
 Write-Host "$status $Name ($([Math]::Round($timer.Elapsed.TotalSeconds,1))s)"
}
Push-Location $root
Start-Transcript -Path (Join-Path $OutputRoot 'ci.log')|Out-Null
try{
 Step 'relay' {
  $arguments=@();if($WslDistribution){$arguments+=@('-d',$WslDistribution)}
  Native 'wsl.exe' ($arguments+@('--cd',$root,'--exec','python3','tests/relay/test_relay.py'))
 }
 Step 'windows-core-vault' {Native 'powershell.exe' @('-NoProfile','-File','scripts/test-windows.ps1')}
 Step 'windows-build' {Native 'powershell.exe' @('-NoProfile','-File','scripts/build-release.ps1','-Version','v1.50','-Output',(Join-Path $OutputRoot 'windows-build'))}
 Step 'mcp-dependencies' {Native $pythonPath @('-m','pip','install','-r','mcp/requirements.txt')}
 Step 'mcp-contracts' {Native $pythonPath @('-B','-m','unittest','discover','-s','tests/mcp','-v')}
 Step 'operator-wrapper-PS5-PS7' {Native 'powershell.exe' @('-NoProfile','-File','tests/mcp/test-operator-wrapper.ps1','-OutputRoot',(Join-Path $OutputRoot 'wrapper'))}
 Step 'profile-policy' {Native 'powershell.exe' @('-NoProfile','-File','tests/mcp/test-profile-policy.ps1','-OutputRoot',(Join-Path $OutputRoot 'profile-policy'))}
 Step 'skill-data' {Native $pythonPath @('-B','scripts/check-skill-data.py')}
 Step 'public-content' {Native $pythonPath @('-B','scripts/scan-public.py')}
 Step 'python-suites' {Native $pythonPath @('-B','-m','unittest','discover','-s','tests','-v')}
 Step 'modern-package' {& scripts/build-modern-desktop.ps1 -Dotnet $Dotnet -Publish -Output (Join-Path $OutputRoot 'modern-package')}
 Step 'modern-contracts' {& scripts/test-modern-desktop.ps1 -Dotnet $Dotnet -OutputRoot (Join-Path $OutputRoot 'modern-tests')}
 Step 'dependency-audit' {Native 'npm.cmd' @('audit','--prefix','desktop/ui','--audit-level=low')}
 Step 'diff-check' {Native 'git' @('-c','core.whitespace=cr-at-eol','diff','--check')}
 Step 'source-head' {if((git rev-parse HEAD) -ne $commit -or (git status --porcelain)){throw 'Source changed during CI; rerun on the final clean head'}}
 Step 'process-sanity' {
  $owned=@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -notin $before -and $_.ProcessId -ne $PID -and $_.CommandLine -and ($_.CommandLine.Contains($root) -or $_.CommandLine.Contains($OutputRoot)) -and $_.Name -match '^(node|python|dotnet|.*Tests|.*Fixture|Remsup.*)\.exe$'})
  foreach($process in $owned){Write-Host "Stopping completed CI worker $($process.Name) PID $($process.ProcessId)";Stop-Process -Id $process.ProcessId -ErrorAction Stop}
  Write-Host 'Checked new processes with this CI workspace/output path; existing processes preserved.'
 }
}finally{
 $clock.Stop()
 $success=@($steps|Where-Object status -eq 'FAIL').Count -eq 0
 $summary=@{commit=$commit;success=$success;seconds=[Math]::Round($clock.Elapsed.TotalSeconds,2);steps=@($steps.ToArray())}
 $summary|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $OutputRoot 'summary.json')
 Write-Host "$(if($success){'PASS'}else{'FAIL'}) LOCAL CI commit=$($summary.commit) duration=$($summary.seconds)s"
 Stop-Transcript|Out-Null;Pop-Location;$env:PATH=$originalPath
}
if(-not $success){exit 1}
