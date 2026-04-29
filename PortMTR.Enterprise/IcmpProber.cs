using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PortMTR.Enterprise.Models;

namespace PortMTR.Enterprise.Protocols;

/// <summary>
/// ICMP traceroute prober using raw sockets.
/// Sends ICMP Echo Request with increasing TTL; listens for
/// Time Exceeded (intermediate hops) or Echo Reply (destination).
/// Requires Administrator privileges.
/// </summary>
public sealed class IcmpProber : IProber
{
    private static readonly ushort s_pid = (ushort)(Environment.ProcessId & 0xFFFF);

    public async Task<ProbeResult> ProbeAsync(
        IPAddress target, int ttl, int flowId, int probeId, int port,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        return await Task.Run(() => ProbeSynchronous(target, ttl, (ushort)(probeId & 0xFFFF), timeout), cts.Token)
                         .ConfigureAwait(false);
    }

    private ProbeResult ProbeSynchronous(IPAddress target, int ttl, ushort seq, TimeSpan timeout)
    {
        if (target.AddressFamily == AddressFamily.InterNetworkV6)
            return ProbeIpv6WithPing(target, ttl, timeout);

        Socket? sendSock = null;
        Socket? recvSock = null;
        try
        {
            sendSock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, System.Net.Sockets.ProtocolType.Icmp);
            recvSock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, System.Net.Sockets.ProtocolType.Icmp);

            sendSock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
            recvSock.Bind(new IPEndPoint(IPAddress.Any, 0));

            byte[] pkt = IcmpUtil.BuildEchoRequest(s_pid, seq);
            var sw = Stopwatch.StartNew();

            sendSock.SendTo(pkt, new IPEndPoint(target, 0));

            long deadlineTicks = Stopwatch.GetTimestamp() +
                                 (long)(timeout.TotalSeconds * Stopwatch.Frequency);

            var recvBuf = new byte[1500];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            while (Stopwatch.GetTimestamp() < deadlineTicks)
            {
                long remaining = deadlineTicks - Stopwatch.GetTimestamp();
                int remainMs = (int)(remaining * 1000 / Stopwatch.Frequency);
                if (remainMs <= 0) break;

                if (!recvSock.Poll(remainMs * 1000, SelectMode.SelectRead)) break;

                int n = recvSock.ReceiveFrom(recvBuf, ref remote);
                double rtt = sw.Elapsed.TotalMilliseconds;

                var (matchedTe, respIpTe) = IcmpUtil.ParseTimeExceeded(recvBuf, n, s_pid, seq);
                if (matchedTe)
                    return new ProbeResult(ttl, IPAddress.Parse(respIpTe), rtt, false, false, ProbeProtocol.ICMP, 0);

                var (matchedEr, respIpEr) = IcmpUtil.ParseEchoReply(recvBuf, n, s_pid, seq);
                if (matchedEr)
                {
                    IPAddress respIp = IPAddress.Parse(respIpEr);
                    bool isDestination = respIp.Equals(target);
                    return new ProbeResult(ttl, respIp, rtt, false, isDestination, ProbeProtocol.ICMP, 0);
                }
            }

            return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.ICMP, 0);
        }
        catch (SocketException)
        {
            return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.ICMP, 0);
        }
        finally
        {
            sendSock?.Close(); sendSock?.Dispose();
            recvSock?.Close(); recvSock?.Dispose();
        }
    }

    private static ProbeResult ProbeIpv6WithPing(IPAddress target, int ttl, TimeSpan timeout)
    {
        try
        {
            using var ping = new Ping();
            byte[] payload = Enumerable.Range(0, 32).Select(i => (byte)(i & 0xFF)).ToArray();
            var options = new PingOptions(ttl, false);
            var sw = Stopwatch.StartNew();
            PingReply reply = ping.Send(target, Math.Max(1, (int)timeout.TotalMilliseconds), payload, options);
            double rtt = reply.RoundtripTime > 0 ? reply.RoundtripTime : sw.Elapsed.TotalMilliseconds;

            if (reply.Status == IPStatus.Success && reply.Address != null)
                return new ProbeResult(ttl, reply.Address, rtt, false, true, ProbeProtocol.ICMP, 0);

            if (reply.Status == IPStatus.TtlExpired && reply.Address != null)
                return new ProbeResult(ttl, reply.Address, rtt, false, false, ProbeProtocol.ICMP, 0);
        }
        catch
        {
            // Treat Ping exceptions like probe timeouts to keep engine cycles healthy.
        }

        return new ProbeResult(ttl, null, -1, true, false, ProbeProtocol.ICMP, 0);
    }

    public void Dispose() { }
}
