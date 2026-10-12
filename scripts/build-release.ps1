param([Parameter(Mandatory = $true)][string] $Version,[string]$Output)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^v1\.\d+$') { throw 'Version must use v1.N' }
$root = Split-Path -Parent $PSScriptRoot
& powershell.exe -NoProfile -File (Join-Path $root 'scripts/build-icon.ps1');if($LASTEXITCODE){throw 'Icon generation failed'}
$release = Join-Path $root 'releases'
if($Output){
 if($Output -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+\\)' -or (Test-Path -LiteralPath $Output)){throw 'Output requires a new absolute release directory'}
 $release=$Output
}
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw 'Framework C# compiler missing' }
New-Item -ItemType Directory -Path $release -Force | Out-Null
$shared = @(Get-ChildItem (Join-Path $root 'shared\*.cs') | ForEach-Object FullName)
$client = @(Get-ChildItem (Join-Path $root 'client\portable-exe\*.cs') | ForEach-Object FullName)
$desktop = @(Get-ChildItem (Join-Path $root 'operator\desktop\*.cs') | ForEach-Object FullName)
$manifest = Join-Path $root 'client\portable-exe\RemsupClient.manifest'
$versionSource = Join-Path $release ($Version + '.version.building.cs')
$assemblyVersion = $Version.Substring(1) + '.0.0'
$targets = @(
 @{ Name='RemsupClient'; Main='RemsupClientProgram'; Type='winexe'; Sources=$shared+$client },
 @{ Name='RemsupOperator'; Main='RemsupOperator'; Type='exe'; Sources=$shared+@(Join-Path $root 'operator\RemsupOperator.cs')+@(Join-Path $root 'client\portable-exe\RelayConfiguration.cs') },
 @{ Name='RemsupDesktop'; Main='RemsupDesktop'; Type='winexe'; Sources=$shared+$client+$desktop }
)
try {
 [IO.File]::WriteAllText($versionSource, "using System.Reflection;`n[assembly: AssemblyProduct(""Remsup"")]`n[assembly: AssemblyVersion(""$assemblyVersion"")]`n[assembly: AssemblyFileVersion(""$assemblyVersion"")]`n[assembly: AssemblyInformationalVersion(""$Version"")]`n")
foreach ($target in $targets) {
 $out = Join-Path $release ($target.Name+'_'+$Version+'.exe')
 $temporary = Join-Path $release ($target.Name+'_'+$Version+'.building.exe')
 try {
  $iconArgs = @()
  if ($target.Type -eq 'winexe') { $icon = Join-Path $root 'assets\remsup.ico'; $iconArgs = @("/win32icon:$icon", "/resource:$icon,Remsup.App.ico") }
  & $csc /nologo /platform:anycpu /target:$($target.Type) /main:$($target.Main) /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:Microsoft.CSharp.dll /r:System.Runtime.Serialization.dll /win32manifest:$manifest /out:$temporary $iconArgs $target.Sources $versionSource
  if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $($target.Name)" }
  Move-Item -LiteralPath $temporary -Destination $out -Force
  Get-FileHash -LiteralPath $out -Algorithm SHA256 | Select-Object Path,Hash
 } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
} finally { if (Test-Path -LiteralPath $versionSource) { Remove-Item -LiteralPath $versionSource -Force } }
