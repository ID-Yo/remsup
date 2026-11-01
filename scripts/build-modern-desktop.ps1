
param([string]$Dotnet='dotnet',[string]$Output,[switch]$Publish,[string]$Version='v1.25',[string]$WebView2RuntimePath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if($Version -notmatch '^v1\.\d+$'){throw 'Review version must use v1.N'}
function Assert-CleanInputs {
 $dirty=git -C $root status --porcelain --untracked-files=all -- assets shared client/portable-exe operator/desktop desktop scripts/build-modern-desktop.ps1 docs/product/modern-desktop-review.md global.json NuGet.Config
 if($dirty){throw 'Commit product/package inputs before publishing an exact-source review manifest'}
}
function Test-PathReparseAncestor ($path) {
 $curr = [System.IO.Path]::GetFullPath($path)
 while ($curr) {
  if ((Test-Path -LiteralPath $curr) -and (Get-Item -LiteralPath $curr).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
   return $true
  }
  $curr = Split-Path $curr -Parent
 }
 return $false
}
function New-RemsupRuntimeManifest {
 param([Parameter(Mandatory=$true)][string]$Directory, [Parameter(Mandatory=$true)][string]$RuntimeVersion)
 $dir = [System.IO.Path]::GetFullPath($Directory)
 if (-not (Test-Path -LiteralPath $dir)) { throw "WebView2 directory not found: $dir" }
 if (Test-PathReparseAncestor $dir) { throw "Reparse points not allowed in ancestry of: $dir" }
 if ((Get-Item -LiteralPath $dir).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "Reparse point not allowed on directory root: $dir" }
 $mainExe = Join-Path $dir 'msedgewebview2.exe'
 if (-not (Test-Path -LiteralPath $mainExe)) { throw "msedgewebview2.exe missing in $dir" }
 if ((Get-Item -LiteralPath $mainExe).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "msedgewebview2.exe must not be a reparse point" }
 $vi = (Get-Item -LiteralPath $mainExe).VersionInfo.FileVersion
 if (-not $vi -or $vi -ne $RuntimeVersion) { throw "msedgewebview2.exe version '$vi' does not match requested version '$RuntimeVersion'" }

 $manifestPath = Join-Path $dir 'remsup-runtime.json'
 $files = @()
 $sha256 = [System.Security.Cryptography.SHA256]::Create()
 foreach ($sub in Get-ChildItem -LiteralPath $dir -Recurse -Directory) {
  if (Test-PathReparseAncestor $sub.FullName) { throw "Reparse point directory not allowed in runtime subtree: $($sub.FullName)" }
 }
 foreach ($file in Get-ChildItem -LiteralPath $dir -Recurse -File) {
  if ($file.Name -eq 'remsup-runtime.json') { continue }
  if ((Get-Item -LiteralPath $file.FullName).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "Reparse file link not allowed: $($file.FullName)" }
  if (Test-PathReparseAncestor (Split-Path $file.FullName -Parent)) { throw "Reparse ancestor not allowed for: $($file.FullName)" }
  $rel = $file.FullName.Substring($dir.Length).TrimStart('\', '/').Replace('\', '/')
  $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
  $hash = [System.BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
  $files += @{ path = $rel; sha256 = $hash }
 }
 $sortedFiles = @($files | Sort-Object -Property path)
 $manifest = [ordered]@{
  schema = 'remsup-runtime-v1'
  version = $RuntimeVersion
  files = $sortedFiles
 }
 $json = $manifest | ConvertTo-Json -Depth 5
 [System.IO.File]::WriteAllText($manifestPath, $json)
 $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
 return @{ Path = $manifestPath; Sha256 = $manifestHash; FileCount = $sortedFiles.Count; Version = $RuntimeVersion }
}

if($Publish){Assert-CleanInputs;if(-not $Output -or -not [IO.Path]::IsPathFullyQualified($Output)){throw 'Publish requires an absolute new review output folder'};if(Test-Path $Output){throw 'Output already exists; preserve the prior review package'}}
Push-Location $root
try {
 if(-not (Test-Path (Join-Path $root 'desktop/ui/node_modules'))){& npm.cmd ci --prefix desktop/ui;if($LASTEXITCODE){throw 'UI dependency restore failed'}}
 & npm.cmd run build --prefix desktop/ui;if($LASTEXITCODE){throw 'UI build failed'}
 $dist=(Resolve-Path desktop/ui/dist).Path;$ui=@{}
 Get-ChildItem $dist -Recurse -File | Where-Object Name -ne integrity.json | ForEach-Object {$ui[$_.FullName.Substring($dist.Length+1).Replace('\','/')]=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
 [IO.File]::WriteAllText((Join-Path $dist 'integrity.json'),($ui|ConvertTo-Json))
 & $Dotnet restore desktop/host --locked-mode;if($LASTEXITCODE){throw 'Host restore failed'}
 $notices=Join-Path $root 'desktop/host/bundled-notices';New-Item $notices -ItemType Directory -Force|Out-Null
 foreach($name in @('react','react-dom','scheduler')){Copy-Item -LiteralPath "desktop/ui/node_modules/$name/LICENSE" -Destination (Join-Path $notices "$name-LICENSE.txt")}
 Copy-Item -LiteralPath 'desktop/ui/node_modules/vite/LICENSE.md' -Destination (Join-Path $notices 'vite-LICENSE.md')
 $nugetRoot=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'}
 Copy-Item -LiteralPath (Join-Path $nugetRoot 'microsoft.web.webview2/1.0.4258.31/LICENSE.txt') -Destination (Join-Path $notices 'WebView2-SDK-LICENSE.txt')
 foreach($runtime in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')){
  $pack=Join-Path $nugetRoot "$runtime/10.0.12";Get-ChildItem $pack -File | Where-Object {$_.Name -match '(?i)license|third.?party'} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $notices ($runtime+'-'+$_.Name))}
  if(-not (Get-ChildItem $notices -File | Where-Object Name -like "$runtime-*")){throw "Runtime terms/notices missing: $runtime"}
 }
 if($Publish){
  # A running compressed bundle must not lazily load managed dependencies from a replaced EXE.
  & $Dotnet publish desktop/host -c Release -r win-x64 --self-contained true -o $Output -p:RestoreLockedMode=true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:AssemblyName=RemsupDesktop_$Version" "-p:Version=$($Version.Substring(1)).0" "-p:InformationalVersion=$Version-modern-review" --disable-build-servers
 }else{& $Dotnet build desktop/host -c Release --no-restore --disable-build-servers}
 if($LASTEXITCODE){throw 'Host build/publish failed'}
 if($Publish){
  $exe=Join-Path $Output "RemsupDesktop_$Version.exe";$shippingRoot=@(Get-ChildItem $Output -File)
  if($shippingRoot.Count -ne 1 -or $shippingRoot[0].FullName -ne $exe.Replace('/','\')){throw 'Single-file publish left unexpected shipping files; inspect before delivery'}
  $wvManifestInfo=$null
  if($WebView2RuntimePath){
   $fullWvPath = [System.IO.Path]::GetFullPath($WebView2RuntimePath)
   if(-not (Test-Path -LiteralPath $fullWvPath)){throw "WebView2 runtime path not found: $fullWvPath"}
   if(Test-PathReparseAncestor $fullWvPath){throw "WebView2 runtime path must not have reparse ancestors: $fullWvPath"}
   if((Get-Item -LiteralPath $fullWvPath).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw "WebView2 runtime path must not be a reparse point: $fullWvPath"}
   foreach($sub in Get-ChildItem -LiteralPath $fullWvPath -Recurse -Directory){
    if(Test-PathReparseAncestor $sub.FullName){throw "Reparse ancestor not allowed in runtime subtree: $($sub.FullName)"}
    if($sub.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw "Reparse directory not allowed in runtime subtree: $($sub.FullName)"}
   }
   foreach($file in Get-ChildItem -LiteralPath $fullWvPath -Recurse -File){
    if($file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw "Reparse file link not allowed in runtime subtree: $($file.FullName)"}
   }
   $wvExe=Join-Path $fullWvPath 'msedgewebview2.exe'
   if(-not (Test-Path -LiteralPath $wvExe)){throw "msedgewebview2.exe missing in $fullWvPath"}
   $wvVersion=(Get-Item -LiteralPath $wvExe).VersionInfo.FileVersion
   if(-not $wvVersion){throw "Could not determine WebView2 runtime version from $wvExe; version must not be fabricated"}
   $targetWvDir=Join-Path $Output 'runtimes\webview2'
   New-Item -ItemType Directory -Path $targetWvDir -Force|Out-Null
   Copy-Item -Path (Join-Path $fullWvPath '*') -Destination $targetWvDir -Recurse -Force
   $wvManifestInfo=New-RemsupRuntimeManifest -Directory $targetWvDir -RuntimeVersion $wvVersion
  }else{
   $shippingAll=@(Get-ChildItem $Output -Recurse -File)
   if($shippingAll.Count -ne 1){throw 'Single-file publish left unexpected shipping files; inspect before delivery'}
  }
  Assert-CleanInputs;$embeddedNotices=@{};Get-ChildItem $notices -File | ForEach-Object {$embeddedNotices[$_.Name]=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
  $formatDesc=if($WebView2RuntimePath){'self-contained compressed win-x64 EXE with app-local fixed-version WebView2 runtime'}else{'one self-contained compressed win-x64 EXE'}
  $wvDesc=if($WebView2RuntimePath){@{bundled=$true;version=$wvManifestInfo.Version;manifestSha256=$wvManifestInfo.Sha256;fileCount=$wvManifestInfo.FileCount}}else{'Installed Evergreen dependency; not bundled'}
  @{version=$Version;commit=(git rev-parse HEAD);reviewOnly=$true;format=$formatDesc;exe=(Split-Path $exe -Leaf);sha256=(Get-FileHash $exe -Algorithm SHA256).Hash;bytes=(Get-Item $exe).Length;embeddedUi=$ui;embeddedNotices=$embeddedNotices;webView2=$wvDesc;nativeExtraction='Automatic versioned .NET managed/native cache, normally %TEMP%/.net; no manual extraction';client='--client or a copy named RemsupClient_v1.N.exe'}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $Output 'review-manifest.json')
 }
}finally{Pop-Location}
