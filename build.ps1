#Requires -Version 7
<#
.SYNOPSIS
    Builds, tests and optionally publishes FreeAll. CI runs this same script.

.PARAMETER Configuration
    Debug or Release.

.PARAMETER Locked
    Restore in locked mode so dependency versions must match the committed packages.lock.json files.

.PARAMETER Publish
    Publish the self-contained win-x64 Desktop app and Helper to ./artifacts/publish.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $Locked,
    [switch] $Publish
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root = $PSScriptRoot
$solution = Join-Path $root 'FreeAll.slnx'
$artifacts = Join-Path $root 'artifacts'

function Invoke-Step([string] $Name, [scriptblock] $Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

$restoreArgs = @('restore', $solution)
if ($Locked) { $restoreArgs += '--locked-mode' }

Invoke-Step 'Restore' { dotnet @restoreArgs }
Invoke-Step 'Build' { dotnet build $solution --configuration $Configuration --no-restore }
Invoke-Step 'Test' {
    dotnet test --solution $solution --configuration $Configuration --no-build `
        --results-directory (Join-Path $artifacts 'test-results') --report-xunit-trx
}

Invoke-Step 'Vulnerable dependency check' {
    $report = dotnet list $solution package --vulnerable --include-transitive 2>&1 | Out-String
    Write-Host $report
    if ($report -match 'has the following vulnerable packages') {
        Write-Error 'Vulnerable packages detected.'
        $global:LASTEXITCODE = 1
    }
}

if ($Publish) {
    $publishDirectory = Join-Path $artifacts 'publish/win-x64'

    # The helper first: the app embeds its digest and refuses to start any other (DEC-025).
    Invoke-Step 'Publish FreeAll.Helper (win-x64, self-contained)' {
        dotnet publish (Join-Path $root 'src/FreeAll.Helper/FreeAll.Helper.csproj') `
            --configuration $Configuration --runtime win-x64 --self-contained `
            --output $publishDirectory
    }

    # Must match ElevatedHelperClient.ComputeDigest: the executable, then the library.
    $sha = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    foreach ($name in 'FreeAll.Helper.exe', 'FreeAll.Helper.dll') {
        $sha.AppendData([System.IO.File]::ReadAllBytes((Join-Path $publishDirectory $name)))
    }
    $helperDigest = [System.Convert]::ToHexString($sha.GetHashAndReset())
    Write-Host "==> Helper digest: $helperDigest" -ForegroundColor Cyan

    Invoke-Step 'Publish FreeAll.Desktop (win-x64, self-contained)' {
        dotnet publish (Join-Path $root 'src/FreeAll.Desktop/FreeAll.Desktop.csproj') `
            --configuration $Configuration --runtime win-x64 --self-contained `
            --output $publishDirectory "-p:HelperSha256=$helperDigest"
    }

    # Signing is not configured yet (docs/19-blocked-features.md BLK-013).
    Write-Host '==> Signing skipped: no code-signing certificate configured.' -ForegroundColor Yellow
}

Write-Host 'Done.' -ForegroundColor Green
