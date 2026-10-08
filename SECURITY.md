# Security Policy

## Security Scope

RouteWatch is a local Windows diagnostic tool. It sends network probes and captures selected ICMP/TCP/UDP traffic through Npcap.

The application is not designed to be a network service and should not listen for remote control traffic. The main realistic attack surface is local execution, malformed packet capture input, report export content, installer delivery, and privileged runtime behavior.

## Runtime Security Assumptions

- Run the app only from a trusted installer or release artifact.
- TCP and UDP tracing require Npcap. Install Npcap from the official project unless you have a trusted internal package.
- Administrator privileges may be required for packet capture and raw socket behavior.
- Do not run the app on untrusted systems as a substitute for a hardened packet analyzer sandbox.

## Hardening Measures

- User-controlled report values are HTML-encoded before export.
- JSON export uses `System.Text.Json` serialization rather than manual JSON string construction.
- Target, port, hop count, timeout, interval, packet size, and parallel probe settings are validated before probing starts.
- Packet capture handlers ignore malformed or truncated packets instead of letting parser exceptions escape the capture callback.
- The global UI exception handler does not expose stack traces to end users.
- The one-line installer downloads a release package over HTTPS, checks its byte size against GitHub release metadata, and verifies its SHA-256 against the release's `checksums.txt`. It refuses to run packages when either check cannot be completed.
- Release builds are self-contained, reducing runtime dependency drift.

## Known Limitations

- No application can be guaranteed to withstand every possible attack.
- Packet parsing still depends on third-party libraries: SharpPcap, PacketDotNet, and Npcap.
- The MSI is not currently Authenticode-signed. Windows SmartScreen may warn users, and users cannot cryptographically verify publisher identity from the MSI alone.
- The `irm ... | iex` install method is convenient but inherently risky. Users should only run it from a repository they trust.
- Npcap redistribution has separate licensing and trust-chain considerations. This project does not currently bundle the Npcap installer.

## Recommended Release Security Checklist

Before publishing a public release:

1. Build the release from a clean checkout.
2. Run `dotnet list package --vulnerable --include-transitive`.
3. Build the MSI and portable ZIP through GitHub Actions.
4. Sign the MSI and EXE with an Authenticode certificate.
5. Publish SHA256 checksums for release artifacts.
6. Verify the one-line installer points to the intended GitHub repository.
7. Test install/uninstall on a clean Windows VM.
8. Test TCP, UDP, ICMP, IPv4, and IPv6 modes with Npcap installed.

## Reporting Vulnerabilities

If this project is published on GitHub, use GitHub Security Advisories for private vulnerability reports. If advisories are not enabled, open a minimal issue that states a security concern exists without posting exploit details publicly.
