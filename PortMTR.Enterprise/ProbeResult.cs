using System.Net;

namespace PortMTR.Enterprise.Models;

/// <summary>Result of a single TTL probe to one hop.</summary>
public sealed record ProbeResult(
    int            Ttl,
    IPAddress?     ResponderIp,
    double         RttMs,
    bool           IsTimeout,
    bool           IsDestination,
    ProbeProtocol  Protocol,
    int            Port
);

public enum ProbeProtocol { ICMP, TCP, UDP }

public enum HopFlag
{
    None,
    HighLatency,        // RTT > 2× previous hop
    PacketLoss,         // Loss > 5 %
    SeverePacketLoss,   // Loss > 25 %
    Unreachable,        // All probes timed out
    Destination,        // Final hop
    AsymmetricRoute,    // Sudden RTT drop (reply-path ICMP re-routing)
}
