param([string]$Dotnet='dotnet',[Parameter(Mandatory=$true)][string]$OutputRoot)
# #137: real single-file bundle, external deletion of the unlocked extraction-cache files, then a real update handoff.
$ErrorActionPreference='Stop'
if(-not [IO.Path]::IsPathFullyQualified($OutputRoot)){throw 'Tests require an absolute task-owned output root'}
$root=Split-Path -Parent $PSScriptRoot
New-Item $OutputRoot -ItemType Directory -Force|Out-Null
$OutputRoot=(Resolve-Path $OutputRoot).Path
function Publish-Fixture($name,$version){
 & $Dotnet publish "$root/desktop/tests/BundleSelfHealTests.csproj" -c Release -r win-x64 --self-contained true -o "$OutputRoot/pub-$name" "-p:BaseIntermediateOutputPath=$OutputRoot/obj-$name/" -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$version" --disable-build-servers
 if($LASTEXITCODE){throw "Fixture publish failed: $name"}
}
function Run-Mode($mode){
 $dir=Join-Path $OutputRoot "run-$mode"
 if(Test-Path $dir){if(-not $dir.StartsWith($OutputRoot)){throw 'Refusing to clean outside the output root'};Remove-Item -LiteralPath $dir -Recurse -Force}
 New-Item "$dir/inst","$dir/pkg","$dir/cache" -ItemType Directory -Force|Out-Null
 Copy-Item "$OutputRoot/pub-old/BundleSelfHealTests.exe" "$dir/inst/Remsup.exe"
 Copy-Item "$OutputRoot/pub-cand/BundleSelfHealTests.exe" "$dir/pkg/cand.exe"
 $previous=$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
 $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR="$dir/cache"
 try{$process=Start-Process "$dir/inst/Remsup.exe" -ArgumentList '--root',$dir,'--package',"$dir/pkg/cand.exe",'--mode',$mode -PassThru -WindowStyle Hidden}finally{$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=$previous}
 if(-not $process.WaitForExit(120000)){Stop-Process -Id $process.Id -Force;throw "$mode timed out"}
 Get-Process Remsup -ErrorAction SilentlyContinue|Where-Object {$_.Path -like "$dir*"}|Stop-Process -Force
 $file="$dir/result-$mode.txt"
 if(-not (Test-Path $file)){throw "$mode produced no result"}
 $text=Get-Content $file -Raw
 Write-Host "--- $mode";Write-Host $text
 return [string]$text
}
function Expect($text,$pattern,$what){if($text -notmatch $pattern){throw "FAIL: $what (expected /$pattern/)"}}
Publish-Fixture 'old' '1.52.0'
Publish-Fixture 'cand' '1.99.0'
$legacy=Run-Mode 'legacy'
Expect $legacy 'afterExternalCleanup=\d+ pipesPresent=False' 'external cleanup removes the unloaded handoff assembly'
Expect $legacy 'status=RolledBack' 'without the fix the handoff rolls back'
Expect $legacy 'FileNotFoundException; 0x80070002' 'without the fix the reported field failure reproduces'
$warm=Run-Mode 'warm'
Expect $warm 'warmUpMissing=none' 'warm-up loads every handoff assembly'
Expect $warm 'pipesPresent=True' 'a warmed-up assembly is locked and survives the cleanup'
Expect $warm 'status=Installed' 'handoff succeeds after warm-up and cleanup'
$heal=Run-Mode 'heal'
Expect $heal 'pipesPresent=False' 'external cleanup removes the unloaded handoff assembly'
Expect $heal 'status=Installed' 'the check repairs a damaged cache and the update succeeds'
Expect $heal 'pipesPresentAfter=True' 'the same EXE restored the missing files'
$refuse=Run-Mode 'refuse'
Expect $refuse 'status=Failed code=bundle-incomplete' 'an unrepairable cache is refused'
Expect $refuse 'Restart Remsup and retry the update' 'the refusal tells what to do'
Expect $refuse 'suspends=0 executableUnchanged=True' 'nothing was suspended or replaced'
Write-Host 'Bundle self-heal end-to-end PASS'
