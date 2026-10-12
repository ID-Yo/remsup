param([string]$Dotnet='dotnet',[string]$Output,[switch]$Publish,[string]$Version='v1.25')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if($Version -notmatch '^v1\.\d+$'){throw 'Review version must use v1.N'}
function Assert-CleanInputs {
 $dirty=git -C $root status --porcelain --untracked-files=all -- assets shared client/portable-exe operator/desktop desktop scripts/build-modern-desktop.ps1 docs/product/modern-desktop-review.md global.json NuGet.Config
 if($dirty){throw 'Commit product/package inputs before publishing an exact-source review manifest'}
}
if($Publish){Assert-CleanInputs;if(-not $Output -or -not [IO.Path]::IsPathFullyQualified($Output)){throw 'Publish requires an absolute new review output folder'};if(Test-Path $Output){throw 'Output already exists; preserve the prior review package'}}
Push-Location $root
try {
 & powershell.exe -NoProfile -File scripts/build-icon.ps1;if($LASTEXITCODE){throw 'Icon generation failed'}
 New-Item desktop/ui/public -ItemType Directory -Force|Out-Null;Copy-Item assets/remsup-64.png desktop/ui/public/remsup-64.png -Force
 & npm.cmd ci --prefix desktop/ui;if($LASTEXITCODE){throw 'UI dependency restore failed'}
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
  $exe=Join-Path $Output "RemsupDesktop_$Version.exe";$shipping=@(Get-ChildItem $Output -Recurse -File)
  if($shipping.Count -ne 1 -or $shipping[0].FullName -ne $exe.Replace('/','\')){throw 'Single-file publish left unexpected shipping files; inspect before delivery'}
  Assert-CleanInputs;$embeddedNotices=@{};Get-ChildItem $notices -File | ForEach-Object {$embeddedNotices[$_.Name]=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
  @{version=$Version;commit=(git rev-parse HEAD);reviewOnly=$true;format='one self-contained compressed win-x64 EXE';exe=(Split-Path $exe -Leaf);sha256=(Get-FileHash $exe -Algorithm SHA256).Hash;bytes=(Get-Item $exe).Length;embeddedUi=$ui;embeddedNotices=$embeddedNotices;webView2='Installed Evergreen dependency; not bundled';nativeExtraction='Automatic versioned .NET managed/native cache, normally %TEMP%/.net; no manual extraction';client='--client or a copy named RemsupClient_v1.N.exe'}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $Output 'review-manifest.json')
 }
}finally{Pop-Location}
