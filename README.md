# PortMTR Enterprise

PortMTR Enterprise is a Windows MTR-style network diagnostic tool for ICMP, TCP, and UDP path testing. It is designed for cases where normal ICMP-only traceroute is not enough and you need port-aware visibility similar to Linux `mtr --tcp -P 443` or `mtr --udp -P 19328`.

The app provides live hop statistics, packet loss, latency, jitter, per-hop analysis, IPv4/IPv6 switching, JSON export, HTML export, and Npcap-backed TCP/UDP packet capture.

## Quick Install

After this repository is published to GitHub, users can install the latest MSI with a one-line PowerShell command:

```powershell
irm https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/PortMTR.Enterprise/main/install.ps1 | iex
```

Replace `YOUR_GITHUB_USERNAME/PortMTR.Enterprise` with the final GitHub repository path before publishing.

You can also override the repository without editing the script:

```powershell
$env:PORTMTR_REPO="YOUR_GITHUB_USERNAME/PortMTR.Enterprise"; irm https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/PortMTR.Enterprise/main/install.ps1 | iex
```

## Downloads

Recommended package:

- `PortMTR.Installer.msi` from the latest GitHub Release.

Portable package:

- `PortMTR.Enterprise-win-x64-portable.zip` from the latest GitHub Release.

The MSI is easiest for friends and normal users. The portable ZIP is useful when you want to extract and run the app without a formal install.

## Runtime Dependencies

| Dependency | Required | Why |
|---|---:|---|
| Windows 10/11 x64 | Yes | WPF desktop app, raw socket/capture behavior targets Windows x64 |
| Administrator launch | Recommended | Raw socket and capture workflows need elevated privileges |
| Npcap | Required for TCP/UDP capture | Used to observe ICMP Time Exceeded, ICMPv6, TCP, and UDP replies |
| .NET Runtime | No for release build | The published build is self-contained |
| GeoLite2-City.mmdb | Optional | Adds city/country lookup if placed beside the EXE |

Npcap download:

```text
https://npcap.com/#download
```

Install Npcap with WinPcap API-compatible mode enabled for best compatibility.

## Features

| Feature | Details |
|---|---|
| ICMP MTR | IPv4/IPv6 echo probing with TTL/hop-limit control |
| TCP MTR | Port-based TCP path probing with Npcap receive handling |
| UDP MTR | Port-based UDP path probing with Npcap receive handling |
| IPv4/IPv6 toggle | Lets you choose address family before resolving and probing |
| Live hop table | Loss %, sent, received, last/avg/best/worst RTT, jitter, stddev |
| Hop analysis | Flags loss, high latency, severe loss, destination, and unreachable hops |
| Live chart panel | Per-hop latency history for the selected hop |
| HTML export | Shareable diagnostic report |
| JSON export | Machine-readable report for automation or support tickets |
| Self-contained publish | No .NET runtime install required for users |
| MSI installer | WiX-based installer package for normal distribution |

## How TCP/UDP Probing Works

Traditional traceroute increments TTL and waits for routers to return ICMP Time Exceeded. Linux `mtr --tcp` and `mtr --udp` still depend on those ICMP responses, but the outbound probes are TCP or UDP instead of ICMP.

PortMTR follows the same model:

1. Resolve the target using IPv4 or IPv6 based on the UI toggle.
2. Send probes with increasing TTL or hop limit.
3. Capture ICMP/ICMPv6 Time Exceeded messages from intermediate routers.
4. Match replies back to the original probe using embedded packet details such as address, port, and protocol.
5. Treat a TCP response, UDP unreachable, or matching destination response as destination reached.
6. Continuously update per-hop loss and latency statistics like MTR.

For TCP/UDP modes, Npcap is used because Windows does not expose all of the packet details needed for accurate MTR-style matching through normal high-level sockets.

## Build From Source

Requirements:

- Windows 10/11 x64
- .NET 8 SDK
- WiX Toolset SDK restore access through NuGet
- Npcap installed locally for runtime TCP/UDP testing

Build the app:

```powershell
dotnet restore
dotnet build PortMTR.Enterprise.sln -c Release
```

Publish a self-contained portable build:

```powershell
dotnet publish PortMTR.Enterprise/PortMTR.Enterprise.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishProfile=win-x64-portable
```

Build the MSI:

```powershell
dotnet build PortMTR.Installer/PortMTR.Installer.wixproj -c Release -p:SuppressValidation=true
```

Or build everything with the helper script:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
```

## Release Process

This repository includes a GitHub Actions workflow at:

```text
.github/workflows/release.yml
```

When you push a tag like `v1.0.0`, GitHub Actions will:

1. Restore .NET packages.
2. Publish a self-contained Windows x64 portable build.
3. Build the WiX MSI installer.
4. Create a portable ZIP.
5. Upload the MSI and ZIP to the GitHub Release.

Example:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## One-Line Installer Details

The install script:

```text
install.ps1
```

Downloads the latest GitHub Release metadata, finds the MSI asset, downloads it to `%TEMP%`, and starts `msiexec` for installation.

Expected public command:

```powershell
irm https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/PortMTR.Enterprise/main/install.ps1 | iex
```

If PowerShell blocks scripts, run PowerShell as Administrator and use:

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
irm https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/PortMTR.Enterprise/main/install.ps1 | iex
```

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| TCP/UDP shows all `???` | Npcap missing or wrong adapter | Install Npcap and select the active interface |
| First run asks for admin | App uses raw socket/capture behavior | Accept UAC prompt |
| ICMP works but TCP/UDP does not | Npcap driver unavailable | Reinstall Npcap with WinPcap compatibility |
| IPv6 target does not resolve | Network or DNS does not provide IPv6 | Disable IPv6 toggle or test another host |
| GeoIP column is empty | GeoLite2 database missing | Place `GeoLite2-City.mmdb` beside the EXE |
| GitHub install script says repo placeholder | Repo slug not configured | Replace `YOUR_GITHUB_USERNAME/PortMTR.Enterprise` in `install.ps1` |

## Security Notes

The `irm ... | iex` install style is convenient, but users should only run it from a repository they trust. The safer alternative is to download the MSI from GitHub Releases and inspect the URL before running it.

## License

MIT License.

Third-party components keep their original licenses:

- SharpPcap
- PacketDotNet
- LiveChartsCore
- SkiaSharp
- CommunityToolkit.Mvvm
- MaxMind.GeoIP2

Npcap redistribution has separate licensing requirements. If you bundle Npcap inside a future installer, review the Npcap OEM redistribution terms first.
