using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using RouteWatch.Models;

namespace RouteWatch.Protocols;

/// <summary>
/// TCP traceroute prober.
/// Uses crafted SYN packets over Npcap/SharpPcap when available so ICMP
/// Time Exceeded replies are correlated to the same TCP flow, closer to how
/// Linux mtr --tcp works. Falls back to socket-based probing if injection
/// context can't be prepared.
/// </summary>
public sealed class TcpProber : IProber
{
    private readonly ILiveDevice? _device;
    private readonly object _contextLock = new();
    private CraftedContext? _context;
    private int _captureStarted;

    public TcpProber() { }

    public TcpProber(ILiveDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public async Task<ProbeResult> ProbeAsync(
        IPAddress target, int ttl, int flowId, int probeId, int port,
        TimeSpan timeout, CancellationToken ct = default)
    {
        if (_device == null)
        {
            return await Task.Run(() => ProbeWithSocketsLimited(target, ttl, probeId, port, timeout, ct), ct)
                             .ConfigureAwait(false);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            if (target.AddressFamily == AddressFamily.InterNetworkV6)
                return await Task.Run(() => ProbeIpv6WithSocketsAndCapture(_device, target, ttl, flowId, probeId, port, timeout, cts.Token), cts.Token)
                                 .ConfigureAwait(false);

            if (_device is LibPcapLiveDevice liveDevice)
                return await Task.Run(() => ProbeCrafted(liveDevice, target, ttl, flowId, probeId, port, timeout, cts.Token), cts.Token)
                                 .ConfigureAwait(false);

            return await Task.Run(() => ProbeWithSockets(target, ttl, probeId, port, timeout, cts.Token), cts.Token)
                             .ConfigureAwait(false);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private ProbeResult ProbeCrafted(
        LibPcapLiveDevice device,
        IPAddress target,
        int ttl,
        int flowId,
        int probeId,
        int port,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            var context = EnsureContext(device, target);
            ushort srcPort = (ushort)(20000 + (Math.Abs((flowId * 31) + (probeId * 7) + ttl) % 40000));
            uint sequence = (uint)((flowId << 16) ^ probeId ^ Environment.TickCount);
            ProbeResult? result = null;
            var sw = Stopwatch.StartNew();
            DateTime sentAtUtc = DateTime.UtcNow;

            void OnPacket(object sender, PacketCapture e)
            {
                try
                {
                    if (result != null)
                        return;

                    var raw = Packet.ParsePacket(e.GetPacket().LinkLayerType, e.Data.ToArray());
                    var outerIp = raw.Extract<IPv4Packet>();
                    if (outerIp == null)
                        return;

                    double rttMs = CaptureRttMilliseconds(e, sentAtUtc, sw);

                    if (raw.Extract<IcmpV4Packet>() is IcmpV4Packet icmp &&
                        TryMatchIcmpTcpPayload(icmp.PayloadData, srcPort, port))
                    {
                        byte type = (byte)((int)icmp.TypeCode >> 8);
                        if (type == IcmpUtil.TypeTimeExceeded || type == IcmpUtil.TypeDestUnreach)
                        {
                            result = new ProbeResult(ttl, outerIp.SourceAddress, rttMs,
                                                     false, false, ProbeProtocol.TCP, port);
                        }
                        return;
                    }

                    if (raw.Extract<TcpPacket>() is not TcpPacket tcp)
                        return;

                    if (!outerIp.SourceAddress.Equals(target))
                        return;

                    if (tcp.SourcePort != port || tcp.DestinationPort != srcPort)
                        return;

                    bool matchesSeq = (tcp.AcknowledgmentNumber == sequence + 1) ||
                                      (tcp.SequenceNumber == sequence) ||
                                      (tcp.Acknowledgment && tcp.AcknowledgmentNumber != 0);

                    if (((tcp.Synchronize && tcp.Acknowledgment) || tcp.Reset) && matchesSeq)
                    {
                        result = new ProbeResult(ttl, outerIp.SourceAddress, rttMs,
                                                 false, true, ProbeProtocol.TCP, port);
                    }
                }
                catch
                {
                    // Ignore malformed or truncated packets captured from the network.
                }
            }

            device.OnPacketArrival += OnPacket;
            try
            {
                EnsureCaptureStarted(device);
                sentAtUtc = DateTime.UtcNow;
                SendSyn(device, context, target, ttl, srcPort, (ushort)port, sequence, probeId);

                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline && result == null)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(2);
                }

                return result ?? new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);
            }
            finally
            {
                device.OnPacketArrival -= OnPacket;
            }
        }
        catch
        {
            return ProbeWithSockets(target, ttl, probeId, port, timeout, ct);
        }
    }

    private void EnsureCaptureStarted(ILiveDevice device)
    {
        if (Interlocked.Exchange(ref _captureStarted, 1) == 1)
            return;

        device.StartCapture();
    }

    private static double CaptureRttMilliseconds(PacketCapture capture, DateTime sentAtUtc, Stopwatch stopwatch)
    {
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private void SendSyn(
        LibPcapLiveDevice device,
        CraftedContext context,
        IPAddress target,
        int ttl,
        ushort srcPort,
        ushort dstPort,
        uint sequence,
        int probeId)
    {
        var tcp = new TcpPacket(srcPort, dstPort)
        {
            SequenceNumber = sequence,
            Synchronize = true,
            WindowSize = 64240
        };

        var ip = new IPv4Packet(context.LocalAddress, target)
        {
            TimeToLive = ttl,
            Id = (ushort)(probeId & 0xFFFF),
            Protocol = PacketDotNet.ProtocolType.Tcp,
            PayloadPacket = tcp
        };

        tcp.UpdateTcpChecksum();
        ip.UpdateIPChecksum();

        var ethernet = new EthernetPacket(context.LocalMac, context.NextHopMac, EthernetType.IPv4)
        {
            PayloadPacket = ip
        };
        ethernet.UpdateCalculatedValues();

        device.SendPacket(ethernet);
    }

    private CraftedContext EnsureContext(LibPcapLiveDevice device, IPAddress target)
    {
        lock (_contextLock)
        {
            if (_context != null)
                return _context;

            IPAddress? localAddress = null;
            IPAddress? netmask = null;

            foreach (var address in device.Addresses)
            {
                if (IPAddress.TryParse(address.Addr?.ToString(), out var ip) &&
                    ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    localAddress = ip;
                    if (IPAddress.TryParse(address.Netmask?.ToString(), out var mask) &&
                        mask.AddressFamily == AddressFamily.InterNetwork)
                    {
                        netmask = mask;
                    }
                    break;
                }
            }

            if (localAddress == null)
                throw new InvalidOperationException("Selected capture device has no IPv4 address.");

            var localMac = device.MacAddress;
            if (localMac == null || localMac.Equals(PhysicalAddress.None))
                throw new InvalidOperationException("Selected capture device has no MAC address.");

            IPAddress nextHop = GetNextHopAddress(device, target, localAddress, netmask);
            PhysicalAddress nextHopMac = ResolveMacAddress(nextHop, localAddress);

            _context = new CraftedContext(localAddress, localMac, nextHop, nextHopMac);
            return _context;
        }
    }

    private static IPAddress GetNextHopAddress(
        LibPcapLiveDevice device,
        IPAddress target,
        IPAddress localAddress,
        IPAddress? netmask)
    {
        if (netmask != null && IsInSameSubnet(localAddress, target, netmask))
            return target;

        var gateway = device.Interface.GatewayAddresses
            .FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork);
        return gateway ?? target;
    }

    private static bool IsInSameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var aBytes = a.GetAddressBytes();
        var bBytes = b.GetAddressBytes();
        var mBytes = mask.GetAddressBytes();

        for (int i = 0; i < 4; i++)
        {
            if ((aBytes[i] & mBytes[i]) != (bBytes[i] & mBytes[i]))
                return false;
        }

        return true;
    }

    private static PhysicalAddress ResolveMacAddress(IPAddress destination, IPAddress source)
    {
        byte[] mac = new byte[6];
        uint length = (uint)mac.Length;
        int error = SendARP(ToUInt32(destination), ToUInt32(source), mac, ref length);
        if (error != 0 || length == 0)
            throw new InvalidOperationException($"SendARP failed for {destination} (error {error}).");

        return new PhysicalAddress(mac.Take((int)length).ToArray());
    }

    private static uint ToUInt32(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            throw new ArgumentException("IPv4 address required.", nameof(address));

        return BitConverter.ToUInt32(bytes, 0);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physicalAddrLen);

    private ProbeResult ProbeIpv6WithSocketsAndCapture(
        ILiveDevice device,
        IPAddress target,
        int ttl,
        int flowId,
        int probeId,
        int port,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Socket? tcpSock = null;
        ProbeResult? result = null;
        ushort srcPort = 0;
        var sw = Stopwatch.StartNew();
        DateTime sentAtUtc = DateTime.UtcNow;

        void OnPacket(object sender, PacketCapture e)
        {
            try
            {
                if (result != null)
                    return;

                var raw = Packet.ParsePacket(e.GetPacket().LinkLayerType, e.Data.ToArray());
                var outerIp = raw.Extract<IPv6Packet>();
                if (outerIp == null)
                    return;

                double rttMs = CaptureRttMilliseconds(e, sentAtUtc, sw);

                if (raw.Extract<IcmpV6Packet>() is IcmpV6Packet icmpv6 &&
                    (icmpv6.Type == IcmpV6Type.TimeExceeded || icmpv6.Type == IcmpV6Type.DestinationUnreachable) &&
                    TryMatchIcmpTcpPayloadIPv6(raw.Bytes, icmpv6.PayloadData, srcPort, port))
                {
                    bool isDestination = outerIp.SourceAddress.Equals(target);
                    result = new ProbeResult(ttl, outerIp.SourceAddress, rttMs,
                                             false, isDestination, ProbeProtocol.TCP, port);
                    return;
                }

                if (raw.Extract<TcpPacket>() is not TcpPacket tcp)
                    return;

                if (!outerIp.SourceAddress.Equals(target))
                    return;

                if (tcp.SourcePort != port || tcp.DestinationPort != srcPort)
                    return;

                if ((tcp.Synchronize && tcp.Acknowledgment) || tcp.Reset)
                {
                    result = new ProbeResult(ttl, outerIp.SourceAddress, rttMs,
                                             false, true, ProbeProtocol.TCP, port);
                }
            }
            catch
            {
                // Ignore malformed or truncated packets captured from the network.
            }
        }

        device.OnPacketArrival += OnPacket;
        try
        {
            EnsureCaptureStarted(device);

            tcpSock = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            tcpSock.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IpTimeToLive, ttl);

            bool bindSuccess = false;
            try
            {
                // TCP sockets enter transient states after close; reusing one
                // fixed source port causes false loss on later MTR cycles.
                tcpSock.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                bindSuccess = true;
                srcPort = (ushort)((IPEndPoint)tcpSock.LocalEndPoint!).Port;
            }
            catch (SocketException)
            {
                bindSuccess = false;
            }

            if (!bindSuccess)
                return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);

            tcpSock.Blocking = false;
            sentAtUtc = DateTime.UtcNow;

            try { tcpSock.Connect(target, port); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                              ex.SocketErrorCode == SocketError.InProgress) { }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                return new ProbeResult(ttl, target, sw.Elapsed.TotalMilliseconds, false, true, ProbeProtocol.TCP, port);
            }

            long deadlineTicks = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadlineTicks && result == null)
            {
                ct.ThrowIfCancellationRequested();
                long remaining = deadlineTicks - Stopwatch.GetTimestamp();
                int remainMs = (int)Math.Max(1, remaining * 1000 / Stopwatch.Frequency);
                int sliceUs = Math.Min(remainMs, 25) * 1000;

                var writeList = new List<Socket> { tcpSock };
                var errorList = new List<Socket> { tcpSock };
                Socket.Select(null, writeList, errorList, sliceUs);

                if (writeList.Count > 0 || errorList.Count > 0)
                {
                    int err = (int)tcpSock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
                    if (err == 0 || err == (int)SocketError.ConnectionRefused)
                        return new ProbeResult(ttl, target, sw.Elapsed.TotalMilliseconds, false, true, ProbeProtocol.TCP, port);
                }
            }

            return result ?? new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);
        }
        catch
        {
            return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);
        }
        finally
        {
            device.OnPacketArrival -= OnPacket;
            try { tcpSock?.Close(); tcpSock?.Dispose(); } catch { }
        }
    }

    private ProbeResult ProbeWithSocketsLimited(IPAddress target, int ttl, int probeId, int port, TimeSpan timeout, CancellationToken ct)
    {
        Socket? tcpSock = null;
        try
        {
            int srcPort = 20000 + (Math.Abs(probeId) % 40000);
            AddressFamily family = target.AddressFamily;
            tcpSock = new Socket(family, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            SocketOptionLevel ttlLevel = family == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
            tcpSock.SetSocketOption(ttlLevel, SocketOptionName.IpTimeToLive, ttl);
            IPAddress bindAddress = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;

            bool bindSuccess = false;
            try
            {
                tcpSock.Bind(new IPEndPoint(bindAddress, srcPort));
                bindSuccess = true;
            }
            catch (SocketException)
            {
                try
                {
                    tcpSock.Bind(new IPEndPoint(bindAddress, 0));
                    bindSuccess = true;
                }
                catch (SocketException) { }
            }

            if (!bindSuccess)
                return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);

            tcpSock.Blocking = false;
            var sw = Stopwatch.StartNew();

            try { tcpSock.Connect(target, port); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                              ex.SocketErrorCode == SocketError.InProgress) { }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                return new ProbeResult(ttl, target, sw.Elapsed.TotalMilliseconds, false, true, ProbeProtocol.TCP, port);
            }

            long deadlineTicks = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadlineTicks)
            {
                ct.ThrowIfCancellationRequested();
                var writeList = new List<Socket> { tcpSock };
                var errorList = new List<Socket> { tcpSock };
                Socket.Select(null, writeList, errorList, 0);

                if (writeList.Count > 0 || errorList.Count > 0)
                {
                    int err = (int)tcpSock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
                    if ((err == 0 && tcpSock.Connected) || err == (int)SocketError.ConnectionRefused)
                        return new ProbeResult(ttl, target, sw.Elapsed.TotalMilliseconds, false, true, ProbeProtocol.TCP, port);
                }
            }

            return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);
        }
        catch
        {
            return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);
        }
        finally
        {
            try { tcpSock?.Close(); tcpSock?.Dispose(); } catch { }
        }
    }

    private ProbeResult ProbeWithSockets(IPAddress target, int ttl, int probeId, int port, TimeSpan timeout, CancellationToken ct)
    {
        Socket? icmpRecv = null;
        Socket? tcpSock = null;
        try
        {
            int srcPort = 20000 + (Math.Abs(probeId) % 40000);
            AddressFamily family = target.AddressFamily;
            IPAddress bindAddress = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;

            try
            {
                var icmpProto = family == AddressFamily.InterNetworkV6 ? System.Net.Sockets.ProtocolType.IcmpV6 : System.Net.Sockets.ProtocolType.Icmp;
                icmpRecv = new Socket(family, SocketType.Raw, icmpProto);
                icmpRecv.Bind(new IPEndPoint(bindAddress, 0));
            }
            catch (SocketException)
            {
                icmpRecv = null;
            }

            tcpSock = new Socket(family, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            SocketOptionLevel ttlLevel = family == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
            tcpSock.SetSocketOption(ttlLevel, SocketOptionName.IpTimeToLive, ttl);

            bool bindSuccess = false;
            try
            {
                tcpSock.Bind(new IPEndPoint(bindAddress, srcPort));
                bindSuccess = true;
            }
            catch (SocketException)
            {
                try
                {
                    tcpSock.Bind(new IPEndPoint(bindAddress, 0));
                    bindSuccess = true;
                    srcPort = ((IPEndPoint)tcpSock.LocalEndPoint!).Port;
                }
                catch (SocketException)
                {
                    bindSuccess = false;
                }
            }

            if (!bindSuccess)
                return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);

            tcpSock.Blocking = false;

            var sw = Stopwatch.StartNew();
            long deadlineTicks = Stopwatch.GetTimestamp() +
                                 (long)(timeout.TotalSeconds * Stopwatch.Frequency);

            try { tcpSock.Connect(target, port); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                              ex.SocketErrorCode == SocketError.InProgress) { }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                return new ProbeResult(ttl, target, sw.Elapsed.TotalMilliseconds, false, true, ProbeProtocol.TCP, port);
            }

            var recvBuf = new byte[1500];
            EndPoint remote = new IPEndPoint(bindAddress, 0);

            while (Stopwatch.GetTimestamp() < deadlineTicks)
            {
                ct.ThrowIfCancellationRequested();

                long remaining = deadlineTicks - Stopwatch.GetTimestamp();
                int remainMs = (int)Math.Max(1, remaining * 1000 / Stopwatch.Frequency);
                int sliceUs = Math.Min(remainMs, 25) * 1000;

                var readList = new List<Socket>();
                if (icmpRecv != null) readList.Add(icmpRecv);
                var writeList = new List<Socket> { tcpSock };
                var errorList = new List<Socket> { tcpSock };
                Socket.Select(readList, writeList, errorList, sliceUs);

                if (readList.Count > 0)
                {
                    while (icmpRecv != null && icmpRecv.Poll(0, SelectMode.SelectRead))
                    {
                        int n = icmpRecv.ReceiveFrom(recvBuf, ref remote);
                        double rtt = sw.Elapsed.TotalMilliseconds;

                        if (n >= 56)
                        {
                            int ipLen = (recvBuf[0] & 0x0F) * 4;
                            if (ipLen >= 20 && n > ipLen && recvBuf[ipLen] == IcmpUtil.TypeTimeExceeded)
                            {
                                int icmpPayloadOffset = ipLen + 8;
                                if (n > icmpPayloadOffset && icmpPayloadOffset < n)
                                {
                                    int innerIpLen = (recvBuf[icmpPayloadOffset] & 0x0F) * 4;
                                    int innerTcpOff = icmpPayloadOffset + innerIpLen;
                                    if (n > innerTcpOff + 4)
                                    {
                                        int innerSrcPort = (recvBuf[innerTcpOff] << 8) | recvBuf[innerTcpOff + 1];
                                        int innerDstPort = (recvBuf[innerTcpOff + 2] << 8) | recvBuf[innerTcpOff + 3];
                                        if (innerSrcPort == srcPort && innerDstPort == port)
                                        {
                                            string src = $"{recvBuf[12]}.{recvBuf[13]}.{recvBuf[14]}.{recvBuf[15]}";
                                            return new ProbeResult(ttl, IPAddress.Parse(src), rtt, false, false, ProbeProtocol.TCP, port);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (writeList.Count > 0 || errorList.Count > 0)
                {
                    int err = (int)tcpSock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
                    double rtt = sw.Elapsed.TotalMilliseconds;
                    if (err == 0 && tcpSock.Connected)
                    {
                        return new ProbeResult(ttl, target, rtt, false, true, ProbeProtocol.TCP, port);
                    }
                    if (err == (int)SocketError.ConnectionRefused && ttl > 1)
                    {
                        return new ProbeResult(ttl, target, rtt, false, true, ProbeProtocol.TCP, port);
                    }
                }
            }

            return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);
        }
        catch
        {
            return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.TCP, port);
        }
        finally
        {
            try { tcpSock?.Close(); tcpSock?.Dispose(); } catch { }
            try { icmpRecv?.Close(); icmpRecv?.Dispose(); } catch { }
        }
    }

    private static bool TryMatchIcmpTcpPayload(byte[]? payload, int expectedSrcPort, int expectedDstPort)
    {
        if (payload == null || payload.Length < 28)
            return false;

        int innerIpHeaderLength = (payload[0] & 0x0F) * 4;
        if (payload.Length < innerIpHeaderLength + 4)
            return false;

        int tcpOffset = innerIpHeaderLength;
        int innerSrcPort = (payload[tcpOffset] << 8) | payload[tcpOffset + 1];
        int innerDstPort = (payload[tcpOffset + 2] << 8) | payload[tcpOffset + 3];
        return innerSrcPort == expectedSrcPort && innerDstPort == expectedDstPort;
    }

    private static bool TryMatchIcmpTcpPayloadIPv6(byte[]? packetBytes, byte[]? payload, int expectedSrcPort, int expectedDstPort)
    {
        if (TryMatchTcpPortsAfterIpv6Header(payload, expectedSrcPort, expectedDstPort))
            return true;

        if (packetBytes == null || packetBytes.Length < 4)
            return false;

        byte srcHigh = (byte)(expectedSrcPort >> 8);
        byte srcLow = (byte)(expectedSrcPort & 0xFF);
        byte dstHigh = (byte)(expectedDstPort >> 8);
        byte dstLow = (byte)(expectedDstPort & 0xFF);

        // ICMPv6 errors carry the invoking IPv6 packet. PacketDotNet versions
        // expose that blob at slightly different offsets, so scan for the TCP
        // port tuple instead of trusting one fixed PayloadData layout.
        for (int i = 0; i <= packetBytes.Length - 4; i++)
        {
            if (packetBytes[i] == srcHigh &&
                packetBytes[i + 1] == srcLow &&
                packetBytes[i + 2] == dstHigh &&
                packetBytes[i + 3] == dstLow)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchTcpPortsAfterIpv6Header(byte[]? payload, int expectedSrcPort, int expectedDstPort)
    {
        if (payload == null)
            return false;

        int[] candidateOffsets = { 40, 48, 0, 8 };
        foreach (int tcpOffset in candidateOffsets)
        {
            if (payload.Length < tcpOffset + 4)
                continue;

            int innerSrcPort = (payload[tcpOffset] << 8) | payload[tcpOffset + 1];
            int innerDstPort = (payload[tcpOffset + 2] << 8) | payload[tcpOffset + 3];
            if (innerSrcPort == expectedSrcPort && innerDstPort == expectedDstPort)
                return true;
        }

        return false;
    }

    public void Dispose() { }

    private sealed record CraftedContext(
        IPAddress LocalAddress,
        PhysicalAddress LocalMac,
        IPAddress NextHopAddress,
        PhysicalAddress NextHopMac);
}
