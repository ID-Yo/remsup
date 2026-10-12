param([switch]$AllowLanFirewall)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'releases\RemsupTests.exe'
New-Item -ItemType Directory -Path (Split-Path $out) -Force|Out-Null
& powershell.exe -NoProfile -File (Join-Path $root 'scripts/build-icon.ps1');if($LASTEXITCODE){throw 'Icon generation failed'}
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem (Join-Path $root 'shared\*.cs') -ErrorAction SilentlyContinue | ForEach-Object FullName)
$sources += @(Get-ChildItem (Join-Path $root 'tests\windows\*.cs') | ForEach-Object FullName)
$sources += @(Get-ChildItem (Join-Path $root 'operator\desktop\*.cs'), (Join-Path $root 'client\portable-exe\*.cs') | ForEach-Object FullName)
try {
 $icon = Join-Path $root 'assets\remsup.ico'
 & $csc /nologo /target:exe /main:TestRunner /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:Microsoft.CSharp.dll /r:System.Runtime.Serialization.dll "/resource:$icon,Remsup.App.ico" /out:$out $sources
 if ($LASTEXITCODE -ne 0) { throw 'Windows test compilation failed' }
 if ($AllowLanFirewall) { $env:REMSUP_TEST_LAN_FIREWALL = '1' }
 & $out
 if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed' }
} finally {
 Remove-Item Env:REMSUP_TEST_LAN_FIREWALL -ErrorAction SilentlyContinue
 if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
}
