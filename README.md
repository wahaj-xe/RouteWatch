<div align="center">

# 🌐 RouteWatch
### Next-Generation Network Path Diagnostics & Live Socket Telemetry for Windows

[![CI Pipeline](https://github.com/wahaj-xe/RouteWatch/actions/workflows/ci.yml/badge.svg)](https://github.com/wahaj-xe/RouteWatch/actions/workflows/ci.yml)
[![Latest Release](https://img.shields.io/github/v/release/wahaj-xe/RouteWatch?color=00e5ff&label=Release)](https://github.com/wahaj-xe/RouteWatch/releases/latest)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-blue.svg)](https://github.com/wahaj-xe/RouteWatch)
[![.NET](https://img.shields.io/badge/.NET-8.0%20(Self--Contained)-512BD4.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

*A modern, high-precision alternative to legacy WinMTR and classic traceroute, engineered for true multi-protocol path diagnostics, zero false packet loss, and live application process socket tracing.*

[**Quick Install**](#-quick-install-powershell-one-liner) • [**Downloads**](#-download-options) • [**Why RouteWatch?**](#-how-routewatch-overcomes-traditional-winmtr-issues) • [**Prerequisites**](#-prerequisites--system-requirements) • [**Building from Source**](#-building-from-source)

---

</div>

## ⚡ Quick Install (PowerShell One-Liner)

Install the latest official Windows MSI release with a single PowerShell command (no manual downloads or extra clicks required):

```powershell
irm https://raw.githubusercontent.com/wahaj-xe/RouteWatch/main/install.ps1 | iex
```

> **Note:** Open PowerShell as Administrator. The installer queries the latest GitHub Release, verifies package integrity, and registers desktop and start menu shortcuts.

---

## 📦 Download Options

Every release is packaged and published as self-contained Windows x64 binaries:

| Package | Format | Best For | Description |
| :--- | :---: | :--- | :--- |
| **Windows Installer** | `.msi` | **Recommended** | Enterprise WiX installer. Supports silent deployment (`msiexec /i`), adds Start Menu & Desktop shortcuts. |
| **Setup Wizard** | `.exe` | Standard Setup | Inno Setup interactive graphical wizard. |
| **Portable Archive** | `.zip` | Zero Install | Standalone archive. Extract anywhere and launch `RouteWatch.exe` immediately without administrative registry changes. |

👉 **[Download Latest Release from GitHub](https://github.com/wahaj-xe/RouteWatch/releases/latest)**

---

## 💡 How RouteWatch Overcomes Traditional WinMTR Issues

While WinMTR has served network engineers for over two decades, modern network architectures, cloud hyper-scalers, and ISP routers expose critical flaws in legacy tooling. Here is how RouteWatch solves them:

```
┌───────────────────────────────────────────────────────────────────────────────────────────────┐
│                                   FEATURE COMPARISON MATRIX                                   │
├──────────────────────────────────────┬─────────────────────────┬──────────────────────────────┤
│ Capability                           │ Legacy WinMTR           │ RouteWatch                   │
├──────────────────────────────────────┼─────────────────────────┼──────────────────────────────┤
│ 0% False Loss on Intermediate Hops   │ ❌ High (Bursts trigger │ ✅ 25ms hardware pacing +    │
│                                      │ router ICMP drop limit) │ 750ms jitter sleep floor     │
│ Protocol Support                     │ ❌ ICMP Echo only       │ ✅ ICMP, TCP (SYN), and UDP  │
│ Specific Port Diagnostics            │ ❌ Port-blind           │ ✅ Custom TCP/UDP ports      │
│ Active Process Socket Correlator     │ ❌ Not available        │ ✅ Integrated ETW Monitor    │
│ CDN / Streaming Endpoint Sniffing    │ ❌ Manual guesswork     │ ✅ 1-Click ⚡ trace to CDN   │
│ Live Throughput (Mb/s & Kb/s)        │ ❌ Not available        │ ✅ Real-time Tx/Rx metrics   │
│ IPv6 Dual-Stack Support              │ ❌ Poor / Inflexible    │ ✅ Tri-State auto-preference │
│ Timer Resolution                     │ ⚠️ Low (~15ms Windows)  │ ✅ Microsecond QPC timers    │
│ Modern Minimalist UI & Dark Mode     │ ❌ Windows 98 dialog    │ ✅ Linear/Raycast aesthetic  │
│ Automated Options & Parity Defaults  │ ⚠️ Hard to navigate     │ ✅ Clean Options modal       │
└──────────────────────────────────────┴─────────────────────────┴──────────────────────────────┘
```

### 1. Zero False Packet Loss (Router Control-Plane ICMP Rate Limiting)
- **The WinMTR Flaw:** WinMTR fires bursts of TTL packets consecutively across all hops. Modern enterprise and residential ONT routers (Huawei, Cisco, Juniper, Mikrotik) enforce hardware rate limiters on CPU-generated ICMP *Time Exceeded* packets. As a result, WinMTR falsely reports 5%–15% packet loss on Hop 1 or intermediate provider backbones.
- **The RouteWatch Fix:** RouteWatch implements a strict **25ms sequential inter-hop pacing delay** and a **750ms minimum inter-cycle pause floor**. In live testing, this drops false packet loss on local routers from 8.0% directly to **0.0%**.

### 2. Multi-Protocol Tracing: ICMP vs. TCP SYN vs. UDP
- **The WinMTR Flaw:** Cloud providers (AWS, Cloudflare, Akamai), corporate firewalls, and ISP borders frequently drop or deprioritize ICMP traffic while passing TCP web traffic at line speed. WinMTR displays `???` or high latency that doesn't reflect actual application health.
- **The RouteWatch Fix:** RouteWatch lets you probe using **TCP SYN** on application ports (e.g. `80`, `443`) or **UDP** on traceroute ports (`33434`). You trace the exact path and firewall rules your application uses.

### 3. Integrated Resource Monitor & 1-Click CDN Sniffer
- **The WinMTR Flaw:** If a YouTube Music stream stutters, a game lags, or a Discord call drops, you have to manually open separate tools, hunt for the remote IP, and manually copy-paste it into WinMTR.
- **The RouteWatch Fix:** RouteWatch features a built-in **Resource Monitor** powered by Windows ETW kernel events:
  - Lists every active network process (e.g. `chrome.exe`, `spotify.exe`, `discord.exe`).
  - Measures real-time send/receive bandwidth in **Mb/s and Kb/s**.
  - Displays remote connection endpoints and ports.
  - Features a **1-Click ⚡ Trace** button that immediately launches a multi-hop MTR against the active CDN server or game host.

### 4. Tri-State IPv6 Preference
- Configurable address family priority:
  - `[ - ] Auto-Prefer IPv6` (Default): Uses IPv6 (AAAA) if the destination supports it, automatically falling back to IPv4.
  - `[ ✓ ] IPv6 Only`: Forces IPv6 end-to-end.
  - `[   ] IPv4 Only`: Forces IPv4 resolution.

### 5. High-Resolution QPC Timers & Route Jitter Telemetry
- Uses `QueryPerformanceCounter` to measure round-trip times with microsecond precision.
- Calculates per-hop standard deviation, route jitter, and a composite **Route Stability Index (0–100)** to pinpoint intermittent bottlenecks.

---

## ⚙️ WinMTR Parity Options Modal

Access the dedicated **`⚙ Options...`** dialog to configure the prober engine:

- **Ping size (bytes):** Configurable packet payload size (Default: `64 bytes`, matching standard WinMTR ping packets).
- **Interval (seconds):** Cycle pacing interval (Default: `1.0s`).
- **Max hosts in LRU list (Hops):** Maximum TTL limit (Default: `30 hops`).
- **Probe Timeout (seconds):** Maximum wait time before marking packet loss (Default: `2.0s`).
- **Parallel Probes:** Probe concurrency (Default: `1` sequential pacing to protect router control planes).
- **Resolve Hostnames:** Reverse DNS PTR lookup toggle.
- **Reset to Optimal Defaults:** 1-click button restoring recommended WinMTR settings.

---

## 📋 Prerequisites & System Requirements

| Requirement | Status | Details |
| :--- | :---: | :--- |
| **Operating System** | Required | Windows 10 (1809+) or Windows 11 (64-bit), Windows Server 2016+ |
| **Administrator Rights** | Recommended | Required for raw ICMP/TCP socket operations and ETW kernel monitoring |
| **.NET 8 Runtime** | Bundled | Not required. Installers and portable packages are **self-contained**. |
| **Npcap Driver** | Optional | Only needed for raw UDP capture mode. Native ICMP and TCP modes operate **without any driver installations**. |

> **Optional Npcap Driver:** If you need raw UDP capture, install [Npcap](https://npcap.com/#download) with *WinPcap API-compatible mode* enabled.

---

## 🛠️ Building from Source

### Requirements
- Windows 10/11 x64
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- WiX Toolset v5 (restored automatically via NuGet)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (optional, for `.exe` setup builds)

### Build Commands
```powershell
# 1. Clone the repository
git clone https://github.com/wahaj-xe/RouteWatch.git
cd RouteWatch

# 2. Restore NuGet packages
dotnet restore RouteWatch.sln

# 3. Build in Release mode
dotnet build RouteWatch.sln -c Release

# 4. Publish self-contained portable package
dotnet publish Routewatch/RouteWatch.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=win-x64-portable

# 5. Build WiX MSI Installer
dotnet build Routewatch.Installer/RouteWatch.Installer.wixproj -c Release -p:SuppressValidation=true
```

Or run the automated release script:
```powershell
.\scripts\build-release.ps1
```

---

## 🚀 CI/CD Pipeline

The project includes automated GitHub Actions workflows:

- **Continuous Integration (`.github/workflows/ci.yml`):** Automatically restores, builds, compiles the WiX MSI package, and archives artifacts on every push or pull request to `main`.
- **Automated Releases (`.github/workflows/release.yml`):** Triggered when a version tag (`v*`) is pushed. Automatically compiles the MSI, Inno Setup EXE, portable ZIP, generates SHA256 checksums, and publishes a new GitHub Release.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
