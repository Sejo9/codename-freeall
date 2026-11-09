#Requires -Version 7
<#
.SYNOPSIS
    Writes and signs the update manifest for a release (TD-050).

.DESCRIPTION
    Produces manifest.json and manifest.json.sig in -OutputDirectory. Upload both, together with the
    setup file at -PackageUrl, to the HTTPS location the app was built with. The signature is ECDSA
    P-256/SHA-256 in IEEE P1363 form, over "freeall-update-manifest-v1\n" followed by the exact manifest
    bytes, which is what UpdateManifestVerifier checks.

    -Sequence must be larger than any sequence published before, even for a manifest that only extends
    the expiry; apps refuse a lower one. Re-sign before -ExpiresInDays runs out, or apps report that the
    update information has expired.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SetupPath,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $PackageUrl,
    [Parameter(Mandatory)] [long] $Sequence,
    [Parameter(Mandatory)] [string] $PrivateKeyPath,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [securestring] $Password,
    [string] $Notes = '',
    [string] $Compatibility = 'Windows 11 releases in Microsoft servicing, x64. See the compatibility matrix in the release notes.',
    [int] $MinimumWindowsBuild = 22000,
    [int] $ExpiresInDays = 90,
    [datetime] $Now = [datetime]::UtcNow
)

$ErrorActionPreference = 'Stop'
if (-not $PackageUrl.StartsWith('https://', [StringComparison]::Ordinal)) { throw 'The package address must be HTTPS.' }
if ($Sequence -lt 1) { throw 'The sequence starts at 1.' }
[void][version]::Parse($Version)
if (-not $Password) { $Password = Read-Host -AsSecureString 'Password for the private key' }

$setup = Get-Item $SetupPath
$manifest = [ordered]@{
    schema = 'freeall.update'
    schemaVersion = 1
    product = 'FreeAll'
    channel = 'stable'
    architecture = 'x64'
    sequence = $Sequence
    version = $Version
    published = $Now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    expires = $Now.ToUniversalTime().AddDays($ExpiresInDays).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    minimumWindowsBuild = $MinimumWindowsBuild
    notes = $Notes
    compatibility = $Compatibility
    package = [ordered]@{
        url = $PackageUrl
        size = $setup.Length
        sha256 = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 4))
$prefix = [System.Text.Encoding]::ASCII.GetBytes("freeall-update-manifest-v1`n")
$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromEncryptedPem((Get-Content $PrivateKeyPath -Raw), (ConvertFrom-SecureString $Password -AsPlainText))
    if ($key.KeySize -ne 256) { throw 'The key is not a P-256 key.' }
    $signature = $key.SignData([byte[]]($prefix + $bytes), [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
}
finally {
    $key.Dispose()
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'manifest.json'), $bytes)
Set-Content -Path (Join-Path $OutputDirectory 'manifest.json.sig') -Value ([Convert]::ToBase64String($signature)) -Encoding ascii -NoNewline
Write-Host "Signed manifest for $Version (sequence $Sequence) written to $OutputDirectory"
