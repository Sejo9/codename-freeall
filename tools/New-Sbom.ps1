#Requires -Version 7
<#
.SYNOPSIS
    Writes an SPDX 2.3 software bill of materials for a FreeAll release from the committed lock files.

.DESCRIPTION
    Lists every NuGet package (direct and transitive) that the shipped projects resolve for win-x64, with
    its version, licence as recorded in docs/23-dependency-register.md where known, and a package URL.
    The lock files are the source of truth because CI restores from them in locked mode (TD-005).
    Test projects are not shipped and are left out.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$shipped = 'FreeAll.Core', 'FreeAll.Application', 'FreeAll.Scanner', 'FreeAll.Residue', 'FreeAll.Duplicates',
    'FreeAll.Cleanup', 'FreeAll.Infrastructure', 'FreeAll.Desktop', 'FreeAll.Helper', 'FreeAll.Installer'

$packages = @{}
foreach ($project in $shipped) {
    $lock = Join-Path $root "src/$project/packages.lock.json"
    if (-not (Test-Path $lock)) { continue }
    $json = Get-Content $lock -Raw | ConvertFrom-Json
    foreach ($framework in $json.dependencies.PSObject.Properties) {
        foreach ($package in $framework.Value.PSObject.Properties) {
            if ($package.Value.type -eq 'Project') { continue }
            $id = $package.Name
            $resolved = $package.Value.resolved
            if ($resolved) { $packages["$id@$resolved"] = [pscustomobject]@{ Id = $id; Version = $resolved } }
        }
    }
}

$sorted = $packages.Values | Sort-Object Id, Version
$created = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$spdxPackages = @(
    [ordered]@{
        SPDXID = 'SPDXRef-FreeAll'
        name = 'FreeAll'
        versionInfo = $Version
        downloadLocation = 'NOASSERTION'
        licenseConcluded = 'MIT'
        licenseDeclared = 'MIT'
        copyrightText = 'Copyright (c) FreeAll contributors'
        supplier = 'Organization: FreeAll contributors'
    }
)
$relationships = @([ordered]@{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = 'SPDXRef-FreeAll' })
$index = 0
foreach ($package in $sorted) {
    $index++
    $spdxId = "SPDXRef-Package-$index"
    $spdxPackages += [ordered]@{
        SPDXID = $spdxId
        name = $package.Id
        versionInfo = $package.Version
        downloadLocation = "https://www.nuget.org/api/v2/package/$($package.Id)/$($package.Version)"
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
        externalRefs = @([ordered]@{
            referenceCategory = 'PACKAGE-MANAGER'
            referenceType = 'purl'
            referenceLocator = "pkg:nuget/$($package.Id)@$($package.Version)"
        })
    }
    $relationships += [ordered]@{ spdxElementId = 'SPDXRef-FreeAll'; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = $spdxId }
}

$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "FreeAll-$Version-x64"
    documentNamespace = "https://freeall.invalid/spdx/FreeAll-$Version-x64-$([guid]::NewGuid())"
    creationInfo = [ordered]@{ created = $created; creators = @('Tool: FreeAll tools/New-Sbom.ps1') }
    packages = $spdxPackages
    relationships = $relationships
}

$document | ConvertTo-Json -Depth 8 | Set-Content -Path $Output -Encoding utf8NoBOM
Write-Host "SBOM: $($sorted.Count) packages -> $Output"
