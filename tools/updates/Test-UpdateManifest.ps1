#Requires -Version 7
<#
.SYNOPSIS
    Checks a signed update manifest against the public key the app is built with (TD-050, DEC-031).

.DESCRIPTION
    Verifies manifest.json.sig over "freeall-update-manifest-v1\n" plus the exact bytes of manifest.json,
    as UpdateManifestVerifier does, and, with -SetupPath, that the setup file has the size and SHA-256 the
    manifest names. The release workflow runs it before publishing, so a signing key that doesn't match
    the key built into the app, or a setup file that isn't the one signed, stops the release. Writes the
    manifest's version, sequence and package digest; throws on any mismatch.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ManifestPath,
    [Parameter(Mandatory)] [string] $SignaturePath,
    [Parameter(Mandatory)] [string] $PublicKey,
    [string] $SetupPath
)

$ErrorActionPreference = 'Stop'
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $ManifestPath))
$signature = [Convert]::FromBase64String((Get-Content $SignaturePath -Raw).Trim())
$prefix = [System.Text.Encoding]::ASCII.GetBytes("freeall-update-manifest-v1`n")

$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $read = 0
    $key.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($PublicKey.Trim()), [ref] $read)
    if ($key.KeySize -ne 256) { throw 'The public key is not a P-256 key.' }
    $valid = $key.VerifyData([byte[]]($prefix + $bytes), $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
}
finally {
    $key.Dispose()
}

if (-not $valid) { throw 'The manifest signature does not match the public key. The signing key and the key built into the app differ.' }

$manifest = [System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
if ($SetupPath) {
    $setup = Get-Item $SetupPath
    if ($setup.Length -ne $manifest.package.size) { throw "The setup file is $($setup.Length) bytes; the manifest says $($manifest.package.size)." }
    $digest = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($digest -ne $manifest.package.sha256) { throw 'The setup file is not the one the manifest was signed for (SHA-256 differs).' }
}

[pscustomobject]@{
    Version = $manifest.version
    Sequence = $manifest.sequence
    Published = $manifest.published
    Expires = $manifest.expires
    PackageUrl = $manifest.package.url
    PackageSha256 = $manifest.package.sha256
}
