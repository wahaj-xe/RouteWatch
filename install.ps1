<#
.SYNOPSIS
    RouteWatch 1-Liner PowerShell Installer
.DESCRIPTION
    Downloads and installs the latest RouteWatch MSI package from GitHub Releases.
.EXAMPLE
    irm https://raw.githubusercontent.com/wahaj-xe/RouteWatch/main/install.ps1 | iex
#>
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Check 64-bit architecture
if ([IntPtr]::Size -ne 8) {
    Write-Error "RouteWatch requires a 64-bit version of Windows (x64)."
    exit 1
}

$Repo = $env:ROUTEWATCH_REPO
if ([string]::IsNullOrWhiteSpace($Repo)) {
    $Repo = "wahaj-xe/RouteWatch"
}

if ($Repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Invalid GitHub repository '$Repo'. Expected format: 'owner/repo'."
}

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║        RouteWatch — Network Path Diagnostics & Telemetry         ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "Repository: https://github.com/$Repo" -ForegroundColor DarkGray
Write-Host "Connecting to GitHub API to query latest release..." -ForegroundColor Yellow

$ApiUrl = "https://api.github.com/repos/$Repo/releases/latest"
$Headers = @{
    "User-Agent" = "RouteWatch-Installer-PS"
    "Accept"     = "application/vnd.github+json"
}

try {
    $Release = Invoke-RestMethod -Uri $ApiUrl -Headers $Headers
}
catch {
    throw "Could not fetch verified release metadata from GitHub API: $($_.Exception.Message)"
}

# Find MSI asset first, then EXE setup
$Asset = $Release.assets | Where-Object {
    $_.name -like "RouteWatch*Setup.msi" -or $_.name -like "RouteWatch*.msi"
} | Select-Object -First 1
if ($null -eq $Asset) {
    $Asset = $Release.assets | Where-Object { $_.name -like "RouteWatch*Setup.exe" } | Select-Object -First 1
}

if ($null -eq $Asset) {
    throw "The latest release does not contain a RouteWatch MSI or setup EXE."
}
if ([long]$Asset.size -le 0) {
    throw "GitHub reported an invalid package size for '$($Asset.name)'."
}

$ChecksumsAsset = $Release.assets | Where-Object { $_.name -eq "checksums.txt" } | Select-Object -First 1
if ($null -eq $ChecksumsAsset) {
    throw "The latest release does not contain checksums.txt; refusing to install an unverified package."
}

$InstallDir = Join-Path $env:TEMP ("RouteWatch-Install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $InstallDir -ErrorAction Stop | Out-Null

try {
    $OutPath = Join-Path $InstallDir $Asset.name
    $ChecksumsPath = Join-Path $InstallDir "checksums.txt"

    Write-Host "Downloading $($Asset.name)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $Asset.browser_download_url -OutFile $OutPath -UseBasicParsing

    Write-Host "Downloading release checksums..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $ChecksumsAsset.browser_download_url -OutFile $ChecksumsPath -UseBasicParsing

    $DownloadedSize = (Get-Item -LiteralPath $OutPath).Length
    if ($DownloadedSize -ne [long]$Asset.size) {
        throw "Downloaded file size mismatch. Expected $($Asset.size) bytes, received $DownloadedSize bytes."
    }

    $ExpectedHash = $null
    foreach ($line in [System.IO.File]::ReadAllLines($ChecksumsPath)) {
        if ($line -match '^\s*(?<hash>[A-Fa-f0-9]{64})\s+\*?(?<name>.+?)\s*$' -and
            [string]::Equals($Matches["name"], $Asset.name, [StringComparison]::Ordinal)) {
            $ExpectedHash = $Matches["hash"]
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($ExpectedHash)) {
        throw "No SHA-256 checksum for '$($Asset.name)' was found in the release checksum file."
    }

    $ActualHash = (Get-FileHash -LiteralPath $OutPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($ExpectedHash, $ActualHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 verification failed for '$($Asset.name)'. Refusing to run the installer."
    }

    Write-Host "Verified package size and SHA-256 checksum." -ForegroundColor Green
    Write-Host "Download complete ($([math]::Round($DownloadedSize / 1MB, 1)) MB)." -ForegroundColor Green
    Write-Host "Launching installer. Please approve the Windows UAC elevation prompt..." -ForegroundColor Yellow

    if ($Asset.name.EndsWith(".msi", [StringComparison]::OrdinalIgnoreCase)) {
        $Process = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i `"$OutPath`"" -Wait -PassThru
    }
    else {
        $Process = Start-Process -FilePath $OutPath -Wait -PassThru
    }

    if ($Process.ExitCode -eq 0) {
        Write-Host ""
        Write-Host "✓ RouteWatch installed successfully!" -ForegroundColor Green
        Write-Host "  You can launch RouteWatch from your Start Menu or Desktop shortcut." -ForegroundColor Cyan
        Write-Host ""
    }
    elseif ($Process.ExitCode -eq 1602) {
        Write-Host "Installation was cancelled by user." -ForegroundColor Yellow
    }
    else {
        Write-Warning "Installer exited with code $($Process.ExitCode)."
    }
}
finally {
    try {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction Stop
    }
    catch {
        Write-Warning "Could not remove temporary installer files at '$InstallDir': $($_.Exception.Message)"
    }
}
