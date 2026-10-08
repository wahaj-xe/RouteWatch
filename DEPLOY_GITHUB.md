# GitHub Deployment & Releases

RouteWatch is published at [wahaj-xe/RouteWatch](https://github.com/wahaj-xe/RouteWatch). GitHub Actions builds and publishes release packages from version tags.

## Build and Validate

The `CI` workflow runs on pushes and pull requests to `main`. It restores and builds the solution, publishes the self-contained Windows x64 application, builds the MSI, and uploads CI artifacts.

For a local build on Windows with the .NET 8 SDK:

```powershell
dotnet restore RouteWatch.sln
dotnet build RouteWatch.sln -c Release
```

## Publish a Release

After the changes for a release have been pushed to `main` and CI passes, create and push a semantic version tag:

```powershell
git tag -a v1.0.1 -m "RouteWatch v1.0.1"
git push origin v1.0.1
```

Use the next unused `vMAJOR.MINOR.PATCH` version. The release workflow validates that format and publishes:

- `RouteWatch-{version}-Setup.exe`
- `RouteWatch.msi`
- `RouteWatch-{version}-win-x64-portable.zip`
- `checksums.txt` with SHA-256 hashes for the packages

The MSI, setup EXE, and application assembly are built with the version from the tag. The workflow also uploads the files as GitHub Actions artifacts.

## Install

Users can install the latest published release from PowerShell:

```powershell
irm https://raw.githubusercontent.com/wahaj-xe/RouteWatch/main/install.ps1 | iex
```

The script queries GitHub release metadata and verifies the package size and SHA-256 checksum before starting the installer. It stops rather than installing if metadata or checksums are unavailable or invalid.

## Optional GeoLite2 Databases

City/country lookups require `GeoLite2-City.mmdb`. Autonomous System lookups require `GeoLite2-ASN.mmdb`. These optional databases are not committed to the repository; place either database next to `RouteWatch.exe` or in a `Data` subdirectory. Obtain databases through MaxMind's [GeoLite2 downloads](https://dev.maxmind.com/geoip/geolite2-free-geolocation-data).
