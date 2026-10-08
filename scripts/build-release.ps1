$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $Root "Routewatch\bin\Publish\win-x64-portable"
$ArtifactsDir = Join-Path $Root "artifacts"
$ZipPath = Join-Path $ArtifactsDir "RouteWatch-1.0.0-win-x64-portable.zip"
$MsiSource = Join-Path $Root "Routewatch.Installer\bin\x64\Release\RouteWatch-1.0.0-Setup.msi"
$MsiOut = Join-Path $ArtifactsDir "RouteWatch-1.0.0-Setup.msi"

New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

Write-Host "Restoring packages..." -ForegroundColor Cyan
& "C:\Users\pc\.dotnet\dotnet.exe" restore (Join-Path $Root "RouteWatch.sln")

Write-Host "Publishing self-contained portable build..." -ForegroundColor Cyan
& "C:\Users\pc\.dotnet\dotnet.exe" publish (Join-Path $Root "Routewatch\RouteWatch.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishProfile=win-x64-portable

Write-Host "Building MSI installer..." -ForegroundColor Cyan
& "C:\Users\pc\.dotnet\dotnet.exe" build (Join-Path $Root "Routewatch.Installer\RouteWatch.Installer.wixproj") `
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
