using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PacketDotNet;
using SharpPcap;
using RouteWatch.Models;

namespace RouteWatch.Protocols;

/// <summary>
/// UDP traceroute prober backed by SharpPcap/Npcap.
///
/// Mechanism:
///   1. Bind a UDP socket to a stable local source port, set TTL.
///   2. Send a small UDP payload to target:dstPort.
///   3. A Npcap capture filter intercepts ICMP replies on the same device.
///   4. Match ICMP Time-Exceeded or Port-Unreachable by comparing inner UDP ports.
///
/// Prerequisites: Npcap installed with WinPcap compatibility mode.
/// </summary>
public sealed class UdpProber : IProber
{
    private readonly ILiveDevice _device;
    private int _captureStarted;

    public UdpProber(ILiveDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public async Task<ProbeResult> ProbeAsync(
        IPAddress target, int ttl, int flowId, int probeId, int port,
        TimeSpan timeout, CancellationToken ct = default)
    {
        return await Task.Run(() => ProbeSynchronous(target, ttl, flowId, port, timeout), ct)
                         .ConfigureAwait(false);
    }

    private ProbeResult ProbeSynchronous(IPAddress target, int ttl, int flowId, int dstPort, TimeSpan timeout)
    {
        int srcPort = 20000 + (Math.Abs((flowId * 31) + ttl) % 40000);
        AddressFamily family = target.AddressFamily;

        Socket? udpSock = null;
        ProbeResult? result = null;
        var sw = Stopwatch.StartNew();
        DateTime sentAtUtc = DateTime.UtcNow;

        void OnPacket(object sender, PacketCapture e)
        {
            try
            {
                if (result != null) return;

                var raw = Packet.ParsePacket(e.GetPacket().LinkLayerType, e.Data.ToArray());
                double rtt = CaptureRttMilliseconds(e, sentAtUtc, sw);

                if (family == AddressFamily.InterNetworkV6)
                {
                    var ipv6 = raw.Extract<IPv6Packet>();
                    var icmpv6 = raw.Extract<IcmpV6Packet>();
                    if (ipv6 == null || icmpv6 == null) return;

                    if (icmpv6.Type == IcmpV6Type.TimeExceeded &&
                        TryMatchIcmpUdpPayloadIPv6(icmpv6.PayloadData, srcPort, dstPort))
                    {
                        result = new ProbeResult(ttl, ipv6.SourceAddress, rtt,
                                                 false, false, ProbeProtocol.UDP, dstPort);
                    }
                    else if (icmpv6.Type == IcmpV6Type.DestinationUnreachable && icmpv6.Code == 4 &&
                             TryMatchIcmpUdpPayloadIPv6(icmpv6.PayloadData, srcPort, dstPort))
                    {
                        bool isDest = ipv6.SourceAddress.Equals(target);
                        result = new ProbeResult(ttl, ipv6.SourceAddress, rtt,
                                                 false, isDest, ProbeProtocol.UDP, dstPort);
                    }

                    return;
                }

                var ipv4 = raw.Extract<IPv4Packet>();
                var icmp = raw.Extract<IcmpV4Packet>();
                if (ipv4 == null || icmp == null) return;

                if ((int)icmp.TypeCode == 0x0b00 && TryMatchIcmpUdpPayload(icmp.PayloadData, srcPort, dstPort))
                {
                    result = new ProbeResult(ttl, ipv4.SourceAddress, rtt,
                                             false, false, ProbeProtocol.UDP, dstPort);
                }
                else if ((int)icmp.TypeCode == 0x0303 && TryMatchIcmpUdpPayload(icmp.PayloadData, srcPort, dstPort))
                {
                    bool isDest = ipv4.SourceAddress.Equals(target);
                    result = new ProbeResult(ttl, ipv4.SourceAddress, rtt,
                                             false, isDest, ProbeProtocol.UDP, dstPort);
                }
            }
            catch
            {
                // Ignore malformed or truncated packets captured from the network.
            }
        }

        _device.OnPacketArrival += OnPacket;

        try
        {
            EnsureCaptureStarted();

            udpSock = new Socket(family, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            SocketOptionLevel ttlLevel = family == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
            udpSock.SetSocketOption(ttlLevel, SocketOptionName.IpTimeToLive, ttl);
            IPAddress bindAddress = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
            udpSock.Bind(new IPEndPoint(bindAddress, srcPort));

            byte[] payload = new byte[32];
            payload[0] = (byte)'P'; payload[1] = (byte)'M'; payload[2] = (byte)'T'; payload[3] = (byte)'R';
            sentAtUtc = DateTime.UtcNow;
            udpSock.SendTo(payload, new IPEndPoint(target, dstPort));

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && result == null)
            {
                if (udpSock.Poll(0, SelectMode.SelectRead))
                {
                    IPAddress remoteAddress = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                    EndPoint remote = new IPEndPoint(remoteAddress, 0);
                    var buffer = new byte[2048];
                    int n = udpSock.ReceiveFrom(buffer, ref remote);
                    if (n >= 0 && remote is IPEndPoint remoteEp && remoteEp.Address.Equals(target))
                    {
                        double rtt = sw.Elapsed.TotalMilliseconds;
                        return new ProbeResult(ttl, remoteEp.Address, rtt, false, true, ProbeProtocol.UDP, dstPort);
                    }
                }

                Thread.Sleep(5);
            }

            return result ?? new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.UDP, dstPort);
        }
        catch (SocketException)
        {
            return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.UDP, dstPort);
        }
        finally
        {
            _device.OnPacketArrival -= OnPacket;
            udpSock?.Close();
            udpSock?.Dispose();
        }
    }

    private void EnsureCaptureStarted()
    {
        if (Interlocked.Exchange(ref _captureStarted, 1) == 1)
            return;

        _device.StartCapture();
    }

    private static double CaptureRttMilliseconds(PacketCapture capture, DateTime sentAtUtc, Stopwatch stopwatch)
    {
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static bool TryMatchIcmpUdpPayload(byte[]? payload, int expectedSrcPort, int expectedDstPort)
    {
        if (payload == null || payload.Length < 28)
            return false;

        int innerIpHeaderLength = (payload[0] & 0x0F) * 4;
        if (payload.Length < innerIpHeaderLength + 4)
            return false;

        int udpOffset = innerIpHeaderLength;
        int innerSrcPort = (payload[udpOffset] << 8) | payload[udpOffset + 1];
        int innerDstPort = (payload[udpOffset + 2] << 8) | payload[udpOffset + 3];
        return innerSrcPort == expectedSrcPort && innerDstPort == expectedDstPort;
    }

    private static bool TryMatchIcmpUdpPayloadIPv6(byte[]? payload, int expectedSrcPort, int expectedDstPort)
    {
        if (payload == null || payload.Length < 44)
            return false;

        int udpOffset = 40;
        int innerSrcPort = (payload[udpOffset] << 8) | payload[udpOffset + 1];
        int innerDstPort = (payload[udpOffset + 2] << 8) | payload[udpOffset + 3];
        return innerSrcPort == expectedSrcPort && innerDstPort == expectedDstPort;
    }

    public void Dispose() { }
}
