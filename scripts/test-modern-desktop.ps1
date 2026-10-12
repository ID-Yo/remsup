param([string]$Dotnet='dotnet',[Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
if(-not [IO.Path]::IsPathFullyQualified($OutputRoot)){throw 'Tests require an absolute task-owned output root'}
$root=Split-Path -Parent $PSScriptRoot
New-Item $OutputRoot -ItemType Directory -Force|Out-Null
$csc='C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
Push-Location $root
try{
 foreach($item in @(@{Name='BridgePolicy';Sources=@('desktop/tests/BridgePolicyTests.cs','desktop/host/BridgePolicy.cs')},@{Name='BridgeGate';Sources=@('desktop/tests/BridgeGateTests.cs','desktop/host/BridgeGate.cs')})){
  $sourceFiles=@($item.Sources | ForEach-Object {(Resolve-Path $_).Path});$exe=Join-Path $OutputRoot ($item.Name+'.exe');& $csc /nologo /target:exe /out:$exe @sourceFiles;if($LASTEXITCODE){throw 'Test compile failed'};& $exe;if($LASTEXITCODE){throw 'Bridge test failed'}
 }
 $indicatorSources=@('desktop/tests/TransportIndicatorTests.cs','desktop/host/TransportIndicator.cs','shared/SupportReadiness.cs')|ForEach-Object {(Resolve-Path $_).Path};$indicatorExe=Join-Path $OutputRoot 'TransportIndicator.exe';& $csc /nologo /target:exe /out:$indicatorExe @indicatorSources;if($LASTEXITCODE){throw 'Indicator test compile failed'};& $indicatorExe;if($LASTEXITCODE){throw 'Indicator mapping failed'}
 foreach($name in @('RelayTlsTests','LanTlsTests','UpdateManagerTests','UserInstallationTests','InstallationDialogTests','UpdateBootstrapTests','UpdateProcessTests','EmbeddedUiTests','ExecutorTests','SettingsCommitTests','HostContractTests','LanStartupTests','LanSettingsUiTests','ScreenChannelTests','NativeTypingTests','NativeMouseTests','ScreenLoopbackTests','ParallelChannelTests','TrayLifecycleTests')){& $Dotnet run --project "desktop/tests/$name.csproj" -c Release --disable-build-servers -- (Join-Path $OutputRoot $name);if($LASTEXITCODE){throw "$name failed"}}
 & $Dotnet build desktop/tests/LegacyRendererFixture.csproj -c Release --disable-build-servers;if($LASTEXITCODE){throw 'Legacy renderer fixture build failed'}
 & $Dotnet run --project desktop/tests/WebViewLayoutTests.csproj -c Release --disable-build-servers -- (Join-Path $OutputRoot 'WebViewLayoutTests') (Join-Path $root 'desktop/tests/bin/LegacyRendererFixture/net10.0-windows/LegacyRendererFixture.exe');if($LASTEXITCODE){throw 'WebView layout/update-overlap regression failed'}
}finally{Pop-Location}
