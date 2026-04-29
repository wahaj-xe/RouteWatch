# GitHub Deployment Guide

This project is ready to publish to GitHub, but the repository URL must be known before the one-line installer can be finalized.

## 1. Create the GitHub Repository

Create a new repository on GitHub, for example:

```text
YOUR_GITHUB_USERNAME/PortMTR.Enterprise
```

Recommended visibility:

- Public if friends should install with a simple unauthenticated `irm ... | iex` command.
- Private only if installers should require GitHub authentication.

## 2. Update Installer Repo Slug

Replace the placeholder in these files:

```text
install.ps1
README.md
```

Find:

```text
YOUR_GITHUB_USERNAME/PortMTR.Enterprise
```

Replace with:

```text
your-real-github-user-or-org/your-real-repo
```

Example:

```text
kronos/PortMTR.Enterprise
```

## 3. Push Source Code

From the project root:

```powershell
git init
git add .
git commit -m "Initial PortMTR Enterprise release"
git branch -M main
git remote add origin https://github.com/YOUR_GITHUB_USERNAME/PortMTR.Enterprise.git
git push -u origin main
```

## 4. Create a Release

The repository includes this GitHub Actions workflow:

```text
.github/workflows/release.yml
```

Push a version tag to trigger an automatic release build:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The workflow creates:

```text
PortMTR.Installer.msi
PortMTR.Enterprise-win-x64-portable.zip
```

and uploads both to the GitHub Release.

## 5. Public One-Line Install Command

After the release exists, users can install with:

```powershell
irm https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/PortMTR.Enterprise/main/install.ps1 | iex
```

If PowerShell policy blocks execution:

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
irm https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/PortMTR.Enterprise/main/install.ps1 | iex
```

## 6. Dependencies for Users

Users need:

- Windows 10/11 x64.
- Administrator approval when the app launches.
- Npcap for TCP/UDP probing.

Npcap:

```text
https://npcap.com/#download
```

The release build is self-contained, so users do not need to install the .NET runtime.

## Current Local Artifacts

The current machine already has release artifacts built at:

```text
artifacts/PortMTR.Installer.msi
artifacts/PortMTR.Enterprise-win-x64-portable.zip
```

If GitHub Actions is not available, upload those two files manually to a GitHub Release.
