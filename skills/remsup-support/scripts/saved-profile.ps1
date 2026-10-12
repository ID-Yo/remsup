param(
    [Parameter(Mandatory=$true)][string]$Name,
    [string]$DeviceId,
    [string]$StorePath,
    [scriptblock]$UseCredential
)
$ErrorActionPreference = 'Stop'
if (-not $StorePath) {
    $profileDirectory = Join-Path $env:LOCALAPPDATA 'Remsup\Operator'
    $StorePath = Join-Path $profileDirectory 'devices-v2.json'
    if (-not (Test-Path -LiteralPath $StorePath)) {
        $earlier = Join-Path $profileDirectory 'devices.json'
        if (Test-Path -LiteralPath $earlier) { $StorePath = $earlier }
    }
}
if (-not (Test-Path -LiteralPath $StorePath -PathType Leaf)) { throw 'No saved Remsup vault found for this Windows user.' }
if ((Get-Item -LiteralPath $StorePath).Length -gt 4MB) { throw 'Saved vault exceeds the application size limit.' }
try { $document = [IO.File]::ReadAllText($StorePath) | ConvertFrom-Json } catch { throw 'Saved vault is unreadable; no data was changed.' }
if ($document.version -ne 2 -or $null -eq $document.records) { throw 'Only the current v2 saved vault is supported; no migration or fallback to old credentials.' }
$normalizedId = if ($DeviceId) { $DeviceId -replace '[\s().-]', '' } else { $null }
if ($normalizedId -and $normalizedId -notmatch '^[0-9]{9}$') { throw 'Device ID must contain nine digits.' }
$matches = @($document.records | Where-Object { $_.displayName -ieq $Name -and (-not $normalizedId -or $_.deviceId -ceq $normalizedId) })
if ($matches.Count -ne 1) { throw 'Saved target is missing or ambiguous; confirm name and Device ID. No credential was read.' }
$record = $matches[0]
if ($record.deviceId -notmatch '^[0-9]{9}$' -or $record.transport -notin @('Relay','Lan') -or $record.relayMode -notin @('Default','Custom','Lan')) { throw 'Invalid saved target metadata; no credential was read.' }
$metadata = [pscustomobject]@{
    StorePath = [IO.Path]::GetFullPath($StorePath)
    DisplayName = $record.displayName
    DeviceId = $record.deviceId
    Transport = $record.transport
    RelayMode = $record.relayMode
    Host = $record.host
    Port = $record.port
    HasSavedPassword = -not [string]::IsNullOrEmpty($record.protectedPassword)
}
if (-not $UseCredential) { return $metadata }
if (-not $metadata.HasSavedPassword) { throw 'This profile has no saved permanent password. Obtain a current OTP through the protected input path.' }
$cipher = $null; $clear = $null; $credential = $null
try {
    try {
        Add-Type -AssemblyName System.Security
        $cipher = [Convert]::FromBase64String($record.protectedPassword)
        $entropy = [Text.Encoding]::UTF8.GetBytes('Remsup.Operator.DeviceStore.v1')
        $clear = [Security.Cryptography.ProtectedData]::Unprotect($cipher, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $credential = [Text.UTF8Encoding]::new($false,$true).GetString($clear)
        if ([string]::IsNullOrEmpty($credential)) { throw 'Empty saved credential.' }
    } catch {
        throw 'Saved-credential operation failed. Check current Windows user and vault integrity; no data was changed.'
    }
    # Only an audited consumer may emit output. Never return or log this argument.
    try { & $UseCredential $metadata $credential } catch {
        # Untrusted callback errors may contain credentials; never forward them.
        throw 'Protected credential consumer failed; details suppressed. No automatic retry.'
    }
} finally {
    if ($clear) { [Array]::Clear($clear,0,$clear.Length) }
    if ($cipher) { [Array]::Clear($cipher,0,$cipher.Length) }
    $credential = $null; $record = $null; $document = $null
}
