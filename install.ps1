$ErrorActionPreference = "Stop"

$Repo = $env:RouteWatch_REPO
if ([string]::IsNullOrWhiteSpace($Repo)) {
    $Repo = "wahaj-xe/RouteWatch"
}

}

$InstallDir = Join-Path $env:TEMP "RouteWatch-install"
$MsiPath = Join-Path $InstallDir "RouteWatch.Installer.msi"
$ApiUrl = "https://api.github.com/repos/RouteWatch/releases/latest"

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

Write-Host "RouteWatch installer" -ForegroundColor Cyan
Write-Host "Repository: RouteWatch"
Write-Host "Checking latest GitHub release..."

$Headers = @{
    "User-Agent" = "RouteWatch-Installer"
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

Write-Host "RouteWatch installed successfully." -ForegroundColor Green
