#Requires -Version 7
<#
.SYNOPSIS
    Runs the long soak test (BLK-009): repeated full scans, updates, searches and retention on a changing tree.

.PARAMETER Cycles
    How many cycles (default 40).

.PARAMETER Folders
    How many folders of 50 files the test tree has (default 400, so 20,000 files).

.NOTES
    The tree is created in %TEMP% and removed afterwards. Results are in artifacts/perf/soak-long.txt.
#>
[CmdletBinding()]
param([int] $Cycles = 40, [int] $Folders = 400)

$ErrorActionPreference = 'Stop'
$env:FREEALL_SOAK_CYCLES = $Cycles
$env:FREEALL_SOAK_FOLDERS = $Folders
$project = Join-Path $PSScriptRoot '../tests/FreeAll.Performance.Tests'
dotnet test --project $project --configuration Release -- --explicit only
if ($LASTEXITCODE -ne 0) { throw "The soak test failed with exit code $LASTEXITCODE." }
Get-Content (Join-Path $PSScriptRoot '../artifacts/perf/soak-long.txt')
