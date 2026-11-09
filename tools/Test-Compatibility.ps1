#Requires -Version 7
<#
.SYNOPSIS
    Runs the automated part of a compatibility check on this PC and records the result (docs/24-compatibility-matrix.md §2).

.DESCRIPTION
    Records the Windows release, edition and build, runs build.ps1 (Release, tests, publish, installer smoke
    test on throwaway locations), and writes a dated record to artifacts/compat. Steps 3 to 6 of the matrix
    need a person and are listed in the record as "to do".

.PARAMETER SkipBuild
    Only record the Windows details and the steps still to do (for a PC that runs a build made elsewhere).
#>
[CmdletBinding()]
param([switch] $SkipBuild)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$build = [int]$os.CurrentBuild
$windows = if ($build -ge 22000) { 'Windows 11' } else { 'Windows 10 or older (not supported)' }
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$record = [System.Collections.Generic.List[string]]::new()
$record.Add("Date: $((Get-Date).ToString('yyyy-MM-dd'))")
$record.Add("Windows: $windows $($os.EditionID) $($os.DisplayVersion) build $build.$($os.UBR)")
$record.Add("Architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)")
$record.Add("FreeAll: $version")

if (-not $SkipBuild) {
    $started = Get-Date
    & (Join-Path $root 'build.ps1') -Configuration Release -Publish
    $record.Add("1 Tests and 2 Install/uninstall (test locations): Pass in $([int]((Get-Date) - $started).TotalMinutes) min")
}
else {
    $record.Add('1 Tests and 2 Install/uninstall: not run here')
}

$record.Add('3 Scan, 4 Cleanup and restore, 5 Helper through UAC, 6 Windows tool buttons: to do by hand (docs/12-quality-assurance.md §3.2, §3.3)')

$output = Join-Path $root 'artifacts/compat'
New-Item -ItemType Directory -Force $output | Out-Null
$file = Join-Path $output "$((Get-Date).ToString('yyyy-MM-dd'))-$build.$($os.UBR).txt"
$record | Set-Content $file
$record | Write-Host
Write-Host "Recorded in $file"
