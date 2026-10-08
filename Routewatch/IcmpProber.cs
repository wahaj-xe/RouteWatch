using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using RouteWatch.Models;

namespace RouteWatch.Protocols;

/// <summary>
/// High-precision ICMP traceroute prober using native Windows IP Helper ICMP API (System.Net.NetworkInformation.Ping).
/// Uses Windows' native ICMP API to capture intermediate router responses
/// (TtlExpired / TimeExceeded) and final destination replies across IPv4 and IPv6.
/// Packet payload and fragmentation settings match WinMTR's defaults.
/// Works with zero privilege requirements and no driver dependencies.
/// </summary>
public sealed class IcmpProber : IProber
{
    private byte[] _payload;

    public IcmpProber(int packetSize = 64)
    {
        int size = Math.Clamp(packetSize, 16, 1400);
        _payload = CreatePayload(size);
    }

    public void SetPacketSize(int size)
    {
        int clamped = Math.Clamp(size, 16, 1400);
        _payload = CreatePayload(clamped);
    }

    public async Task<ProbeResult> ProbeAsync(
        IPAddress target, int ttl, int flowId, int probeId, int port,
        TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var options = new PingOptions(ttl, true);
            var sw = Stopwatch.StartNew();

            PingReply reply = await ping.SendPingAsync(
                target, timeout, _payload, options, ct).ConfigureAwait(false);

            sw.Stop();
            double rtt = sw.Elapsed.TotalMilliseconds;

            if (reply.Status == IPStatus.Success && reply.Address != null)
            {
                bool isDest = reply.Address.Equals(target);
                return new ProbeResult(ttl, reply.Address, rtt, false, isDest, ProbeProtocol.ICMP, 0);
            }

            if ((reply.Status == IPStatus.TtlExpired ||
                 reply.Status == IPStatus.TimeExceeded ||
                 reply.Status == IPStatus.TtlReassemblyTimeExceeded) && reply.Address != null)
            {
                return new ProbeResult(ttl, reply.Address, rtt, false, false, ProbeProtocol.ICMP, 0);
            }

            if (reply.Address != null &&
                !reply.Address.Equals(IPAddress.Any) &&
                !reply.Address.Equals(IPAddress.None))
            {
                bool isDest = reply.Address.Equals(target);
                return new ProbeResult(ttl, reply.Address, rtt, false, isDest, ProbeProtocol.ICMP, 0);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Transient timeout / route drops handled gracefully as timeout
        }

        return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.ICMP, 0);
    }

    private static byte[] CreatePayload(int size) => Enumerable.Repeat((byte)32, size).ToArray();

    public void Dispose() { }
}
