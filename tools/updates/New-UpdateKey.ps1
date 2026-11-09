#Requires -Version 7
<#
.SYNOPSIS
    Creates the project's update signing key (ECDSA P-256), TD-050.

.DESCRIPTION
    Writes the private key as a password-encrypted PKCS#8 PEM file and prints the public key, which is the
    value for FREEALL_UPDATE_PUBLIC_KEY (build.ps1 -UpdatePublicKey). Keep the private key and its password
    offline and out of this repository; whoever holds them can publish updates every FreeAll build trusts.
    Changing the key later means a release signed with the old key must ship the new public key first.

.EXAMPLE
    ./tools/updates/New-UpdateKey.ps1 -PrivateKeyPath D:\keys\freeall-update.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PrivateKeyPath,
    [securestring] $Password
)

$ErrorActionPreference = 'Stop'
if (Test-Path $PrivateKeyPath) { throw "$PrivateKeyPath already exists. Choose another path; an existing key is never overwritten." }
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$target = [System.IO.Path]::GetFullPath($PrivateKeyPath)
if ($target.StartsWith($repository, [StringComparison]::OrdinalIgnoreCase)) { throw 'The private key must not be stored inside the repository.' }
if (-not $Password) {
    $Password = Read-Host -AsSecureString 'Password for the private key'
    $again = Read-Host -AsSecureString 'Repeat the password'
    if ((ConvertFrom-SecureString $Password -AsPlainText) -ne (ConvertFrom-SecureString $again -AsPlainText)) { throw 'The passwords differ.' }
}

$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $parameters = [System.Security.Cryptography.PbeParameters]::new(
        [System.Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        600000)
    $pem = $key.ExportEncryptedPkcs8PrivateKeyPem((ConvertFrom-SecureString $Password -AsPlainText), $parameters)
    Set-Content -Path $target -Value $pem -Encoding ascii -NoNewline
    $public = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
    Write-Host "Private key written to $target"
    Write-Output $public
}
finally {
    $key.Dispose()
}
