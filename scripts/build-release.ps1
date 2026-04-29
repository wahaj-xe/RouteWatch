$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $Root "PortMTR.Enterprise\bin\Publish\win-x64-portable"
$ArtifactsDir = Join-Path $Root "artifacts"
$ZipPath = Join-Path $ArtifactsDir "PortMTR.Enterprise-win-x64-portable.zip"
$MsiSource = Join-Path $Root "PortMTR.Installer\bin\x64\Release\PortMTR.Installer.msi"
$MsiOut = Join-Path $ArtifactsDir "PortMTR.Installer.msi"

New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

Write-Host "Restoring packages..." -ForegroundColor Cyan
dotnet restore (Join-Path $Root "PortMTR.Enterprise.sln")

Write-Host "Publishing self-contained portable build..." -ForegroundColor Cyan
dotnet publish (Join-Path $Root "PortMTR.Enterprise\PortMTR.Enterprise.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishProfile=win-x64-portable

Write-Host "Building MSI installer..." -ForegroundColor Cyan
dotnet build (Join-Path $Root "PortMTR.Installer\PortMTR.Installer.wixproj") `
    -c Release `
    -p:SuppressValidation=true

if (Test-Path $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Write-Host "Creating portable ZIP..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force

Copy-Item -LiteralPath $MsiSource -Destination $MsiOut -Force

Write-Host "Release artifacts ready:" -ForegroundColor Green
Write-Host "  $MsiOut"
Write-Host "  $ZipPath"
