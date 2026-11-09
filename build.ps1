#Requires -Version 7
<#
.SYNOPSIS
    Builds, tests and optionally publishes FreeAll. CI runs this same script.

.PARAMETER Configuration
    Debug or Release.

.PARAMETER Locked
    Restore in locked mode so dependency versions must match the committed packages.lock.json files.

.PARAMETER Publish
    Publish the self-contained win-x64 Desktop app and Helper to ./artifacts/publish, then build the
    per-user installer, smoke-test install and uninstall on throwaway folders, and write the release files
    (setup, SHA-256 checksums, SBOM) to ./artifacts/release.

.PARAMETER UpdateManifestUrl
    HTTPS address of the signed update manifest, embedded in the app (TD-050). Defaults to the
    FREEALL_UPDATE_MANIFEST_URL environment variable. Without it and a key, the build can't check for updates.

.PARAMETER UpdatePublicKey
    The project's update public key (base64 SubjectPublicKeyInfo, P-256), from tools/updates/New-UpdateKey.ps1.
    Defaults to the FREEALL_UPDATE_PUBLIC_KEY environment variable.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $Locked,
    [switch] $Publish,
    [string] $UpdateManifestUrl = $env:FREEALL_UPDATE_MANIFEST_URL,
    [string] $UpdatePublicKey = $env:FREEALL_UPDATE_PUBLIC_KEY
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

# Runs a program with each argument quoted properly (Start-Process doesn't quote) and returns its exit code.
function Invoke-Program([string] $Path, [string[]] $Arguments) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($Path)
    $start.UseShellExecute = $false
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    return $process.ExitCode
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

    $desktopArgs = @("-p:HelperSha256=$helperDigest")
    if ($UpdateManifestUrl -and $UpdatePublicKey) {
        if (-not $UpdateManifestUrl.StartsWith('https://', [StringComparison]::Ordinal)) { throw 'The update manifest address must be HTTPS.' }
        $desktopArgs += "-p:UpdateManifestUrl=$UpdateManifestUrl", "-p:UpdatePublicKey=$UpdatePublicKey"
        Write-Host "==> Update checks: $UpdateManifestUrl" -ForegroundColor Cyan
    }
    else {
        Write-Host '==> Update checks not configured: no manifest address and key (BLK-021).' -ForegroundColor Yellow
    }

    Invoke-Step 'Publish FreeAll.Desktop (win-x64, self-contained)' {
        dotnet publish (Join-Path $root 'src/FreeAll.Desktop/FreeAll.Desktop.csproj') `
            --configuration $Configuration --runtime win-x64 --self-contained `
            --output $publishDirectory @desktopArgs
    }

    # Native AOT needs the MSVC linker; its locator script expects vswhere.exe on PATH.
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if ((Test-Path (Join-Path $vsInstaller 'vswhere.exe')) -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
        $env:PATH = "$vsInstaller;$env:PATH"
    }

    $installerDirectory = Join-Path $artifacts 'installer/win-x64'
    Invoke-Step 'Publish FreeAll.Installer (win-x64, Native AOT)' {
        dotnet publish (Join-Path $root 'src/FreeAll.Installer/FreeAll.Installer.csproj') `
            --configuration $Configuration --runtime win-x64 --output $installerDirectory
    }

    $version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $release = Join-Path $artifacts 'release'
    New-Item -ItemType Directory -Force $release | Out-Null
    $setup = Join-Path $release "FreeAll-Setup-$version-x64.exe"
    Invoke-Step "Build the setup file ($version)" {
        $global:LASTEXITCODE = Invoke-Program (Join-Path $installerDirectory 'FreeAll.Installer.exe') @('/pack', $publishDirectory, $version, $setup)
    }

    # Install and uninstall on throwaway folders and a test-only registry key: never the real locations.
    Invoke-Step 'Installer smoke test' {
        $smoke = Join-Path $artifacts ("installer-smoke/" + [guid]::NewGuid().ToString('N'))
        $key = 'Software\FreeAll.Tests\smoke-' + [guid]::NewGuid().ToString('N')
        $redirect = '/root', (Join-Path $smoke 'Programs\FreeAll'), '/start-menu', (Join-Path $smoke 'StartMenu'),
            '/data', (Join-Path $smoke 'Data'), '/registry-key', $key, '/log', (Join-Path $smoke 'setup.log')
        New-Item -ItemType Directory -Force $smoke | Out-Null
        try {
            $installed = Invoke-Program $setup (@('/silent') + $redirect)
            if ($installed -ne 0) { throw "Silent install failed with exit code $installed." }
            $installRoot = Join-Path $smoke 'Programs\FreeAll'
            foreach ($required in 'FreeAll.exe', 'FreeAll.Helper.exe', 'FreeAll.Installer.exe', 'FreeAll.install.json') {
                if (-not (Test-Path (Join-Path $installRoot $required))) { throw "The installation has no $required." }
            }
            if (-not (Test-Path "HKCU:\$key")) { throw 'The uninstall entry was not written.' }

            # The installed uninstaller, as Settings > Apps runs it.
            $removed = Invoke-Program (Join-Path $installRoot 'FreeAll.Installer.exe') (@('/uninstall', '/silent') + $redirect)
            if ($removed -ne 0) { throw "Silent uninstall failed with exit code $removed." }
            if ((Test-Path $installRoot) -or (Test-Path "HKCU:\$key")) { throw 'Uninstalling left the program folder or its entry behind.' }
            Write-Host "Installed and uninstalled $setup"
            $global:LASTEXITCODE = 0
        }
        finally {
            Remove-Item "HKCU:\$key" -Recurse -ErrorAction SilentlyContinue
        }
    }

    Invoke-Step 'Checksums and SBOM' {
        & (Join-Path $root 'tools/New-Sbom.ps1') -Version $version -Output (Join-Path $release "FreeAll-$version-x64.spdx.json")
        Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
            '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
        } | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
        Get-Content (Join-Path $release 'SHA256SUMS.txt') | Write-Host
        $global:LASTEXITCODE = 0
    }

    # Signing is not configured yet (docs/19-blocked-features.md BLK-013, DEC-025).
    Write-Host '==> Signing skipped: no code-signing certificate configured.' -ForegroundColor Yellow
}

Write-Host 'Done.' -ForegroundColor Green
