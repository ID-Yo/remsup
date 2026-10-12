$ErrorActionPreference='Stop'
function Resolve-McpPython([string]$Python,[switch]$Setup){
 $venv=Join-Path $env:LOCALAPPDATA 'Remsup/agent-venv'
 $default=Join-Path $venv 'Scripts/python.exe'
 if(-not $Python -and -not $Setup){$Python=$default}
 if($Setup -and -not $Python){
  $bootstrap=Get-Command python -ErrorAction SilentlyContinue
  if(-not $bootstrap){throw 'Python 3.11+ is missing. Run: winget install --id Python.Python.3.13 -e ; reopen PowerShell, then run setup-remsup-mcp.ps1.'}
  & $bootstrap.Source -c 'import sys; sys.exit(0 if sys.version_info >= (3,11) else 1)' 2>$null
  if($LASTEXITCODE -ne 0){throw 'Python 3.11+ is missing or unavailable. Run: winget install --id Python.Python.3.13 -e ; reopen PowerShell, then run setup-remsup-mcp.ps1.'}
  if(-not (Test-Path -LiteralPath $default)){
   & $bootstrap.Source -m venv $venv
   if($LASTEXITCODE -ne 0){throw 'Cannot create operator environment. Repair Python venv support and run setup-remsup-mcp.ps1 again.'}
  }
  $Python=$default
  & $Python -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'requirements.txt') | Out-Host
  if($LASTEXITCODE -ne 0){throw 'Dependency installation failed. Fix package access and run setup-remsup-mcp.ps1 again.'}
 }
 $resolved=Get-Command $Python -ErrorAction SilentlyContinue
 if(-not $resolved){throw 'Operator Python environment is missing. Run setup-remsup-mcp.ps1; it creates %LOCALAPPDATA%/Remsup/agent-venv. Install Python first with: winget install --id Python.Python.3.13 -e'}
 & $resolved.Source -B (Join-Path $PSScriptRoot 'check-mcp-runtime.py') 2>$null
 if($LASTEXITCODE -ne 0){throw ('Python 3.11+ and mcp==1.27.0 are required. Run setup-remsup-mcp.ps1, or repair the explicit environment with: & "'+$resolved.Source+'" -m pip install -r "'+(Join-Path $PSScriptRoot 'requirements.txt')+'"')}
 return $resolved.Source
}
function Assert-McpSshAlias([string]$Alias){
 if(-not $Alias){return}
 $ssh=Get-Command ssh -ErrorAction SilentlyContinue
 if(-not $ssh){throw 'Windows OpenSSH is missing. Configure the approved SSH key/alias, then run setup-remsup-mcp.ps1.'}
 try{$configuration=& $ssh.Source -G $Alias 2>$null}catch{throw 'Provisioned SSH alias is missing or unreadable. Configure the administrator supplied SSH key/alias, then run setup-remsup-mcp.ps1.'}
 if($LASTEXITCODE -ne 0 -or -not ($configuration -match '^hostname ') -or ($configuration -match ('^hostname '+[regex]::Escape($Alias)+'$'))){throw 'Provisioned SSH alias is missing. Configure the administrator supplied SSH key/alias, then run setup-remsup-mcp.ps1.'}
 if($configuration -match '^user root$'){throw 'Root SSH is not supported. Configure a restricted forwarding alias, then run setup-remsup-mcp.ps1.'}
}
