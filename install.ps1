$ErrorActionPreference = "Stop"

$Repo = $env:PORTMTR_REPO
if ([string]::IsNullOrWhiteSpace($Repo)) {
    $Repo = "YOUR_GITHUB_USERNAME/PortMTR.Enterprise"
}

if ($Repo -like "YOUR_GITHUB_USERNAME/*") {
    throw "PortMTR installer is not configured yet. Replace YOUR_GITHUB_USERNAME/PortMTR.Enterprise in install.ps1 with your GitHub repo, or run: `$env:PORTMTR_REPO='owner/repo'; irm <raw-install-url> | iex"
}

$InstallDir = Join-Path $env:TEMP "PortMTR-Install"
$MsiPath = Join-Path $InstallDir "PortMTR.Installer.msi"
$ApiUrl = "https://api.github.com/repos/$Repo/releases/latest"

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

Write-Host "PortMTR Enterprise installer" -ForegroundColor Cyan
Write-Host "Repository: $Repo"
Write-Host "Checking latest GitHub release..."

$Headers = @{
    "User-Agent" = "PortMTR-Installer"
    "Accept" = "application/vnd.github+json"
}

$Release = Invoke-RestMethod -Uri $ApiUrl -Headers $Headers
$Asset = $Release.assets | Where-Object { $_.name -like "*.msi" } | Select-Object -First 1

if ($null -eq $Asset) {
    throw "No MSI asset found on the latest release for $Repo."
}

Write-Host "Downloading $($Asset.name)..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $Asset.browser_download_url -OutFile $MsiPath -UseBasicParsing

Write-Host "Starting MSI installer. Approve the UAC prompt if Windows asks." -ForegroundColor Green
$Process = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i `"$MsiPath`"" -Wait -PassThru

if ($Process.ExitCode -ne 0) {
    throw "MSI installer failed with exit code $($Process.ExitCode)."
}

Write-Host "PortMTR Enterprise installed successfully." -ForegroundColor Green
