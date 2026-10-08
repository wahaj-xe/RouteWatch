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

$InstallDir = Join-Path $env:TEMP "RouteWatch-Install"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

$ApiUrl = "https://api.github.com/repos/$Repo/releases/latest"
$Headers = @{
    "User-Agent" = "RouteWatch-Installer-PS"
    "Accept"     = "application/vnd.github+json"
}

try {
    $Release = Invoke-RestMethod -Uri $ApiUrl -Headers $Headers
}
catch {
    Write-Warning "Could not fetch release metadata from GitHub API: $_"
    Write-Host "Falling back to direct release download..." -ForegroundColor Yellow
    $Release = $null
}

if ($null -ne $Release -and $null -ne $Release.assets) {
    # Find MSI asset first, then EXE setup
    $Asset = $Release.assets | Where-Object { $_.name -like "RouteWatch*Setup.msi" -or $_.name -like "RouteWatch*.msi" } | Select-Object -First 1
    if ($null -eq $Asset) {
        $Asset = $Release.assets | Where-Object { $_.name -like "RouteWatch*Setup.exe" } | Select-Object -First 1
    }
}

if ($null -ne $Asset) {
    $DownloadUri = [Uri]$Asset.browser_download_url
    $FileName = $Asset.name
    $FileSize = $Asset.size
}
else {
    # Direct fallback URL
    $FileName = "RouteWatch.msi"
    $DownloadUri = [Uri]"https://github.com/$Repo/releases/latest/download/$FileName"
    $FileSize = 0
}

$OutPath = Join-Path $InstallDir $FileName

Write-Host "Downloading $FileName..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $DownloadUri.AbsoluteUri -OutFile $OutPath -UseBasicParsing

if (-not (Test-Path $OutPath) -or (Get-Item $OutPath).Length -lt 1000000) {
    throw "Downloaded installer file appears corrupted or invalid size."
}

Write-Host "Download complete ($([math]::Round((Get-Item $OutPath).Length / 1MB, 1)) MB)." -ForegroundColor Green
Write-Host "Launching installer. Please approve the Windows UAC elevation prompt..." -ForegroundColor Yellow

if ($FileName.EndsWith(".msi")) {
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

# Cleanup installer download
Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
