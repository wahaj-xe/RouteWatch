using System.Net;
using RouteWatch.Models;

namespace RouteWatch.Protocols;

/// <summary>Contract for all probe implementations.</summary>
public interface IProber : IDisposable
{
    /// <summary>
    /// Send one probe with the given TTL and wait for a reply.
    /// Returns a ProbeResult; never throws on timeout.
    /// </summary>
    Task<ProbeResult> ProbeAsync(
        IPAddress    target,
        int          ttl,
        int          flowId,
        int          probeId,
        int          port,
        TimeSpan     timeout,
        CancellationToken ct = default);
}

/// <summary>Shared ICMP packet helpers used by multiple probers.</summary>
public static class IcmpUtil
{
    public const byte TypeEchoRequest  = 8;
    public const byte TypeEchoReply    = 0;
    public const byte TypeTimeExceeded = 11;
    public const byte TypeDestUnreach  = 3;
    public const byte CodePortUnreach  = 3;

    /// <summary>Standard Internet checksum (RFC 1071).</summary>
    public static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        int len = data.Length;
        while (i < len - 1)
        {
            sum += (uint)((data[i] << 8) | data[i + 1]);
            i += 2;
        }
        if (i < len)
            sum += (uint)(data[i] << 8);
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>Build an ICMP Echo Request packet.</summary>
    public static byte[] BuildEchoRequest(ushort id, ushort seq, int dataSize = 32)
    {
        int total = 8 + dataSize;
        var pkt = new byte[total];
        pkt[0] = TypeEchoRequest;
        pkt[1] = 0;
        pkt[4] = (byte)(id >> 8);   pkt[5] = (byte)(id & 0xFF);
        pkt[6] = (byte)(seq >> 8);  pkt[7] = (byte)(seq & 0xFF);

        for (int i = 8; i < total; i++) pkt[i] = (byte)(i & 0xFF);

        ushort chk = Checksum(pkt);
        pkt[2] = (byte)(chk >> 8);
        pkt[3] = (byte)(chk & 0xFF);
        return pkt;
    }

    /// <summary>Parse an ICMP Time Exceeded reply; extract responder IP and verify inner packet ID/seq.</summary>
    public static (bool matched, string responderIp) ParseTimeExceeded(
        byte[] raw, int rawLen, ushort expectedId, ushort expectedSeq)
    {
        if (rawLen < 56) return (false, string.Empty);

        int ipHeaderLen = (raw[0] & 0x0F) * 4;
        if (ipHeaderLen < 20 || rawLen < ipHeaderLen + 8 || rawLen < 20)
            return (false, string.Empty);

        byte icmpType = raw[ipHeaderLen];
        string src = $"{raw[12]}.{raw[13]}.{raw[14]}.{raw[15]}";

        if (icmpType != TypeTimeExceeded) return (false, string.Empty);

        int innerIcmpOffset = ipHeaderLen + 8 + 20;
        if (rawLen < innerIcmpOffset + 8) return (false, string.Empty);

        ushort innerId  = (ushort)((raw[innerIcmpOffset + 4] << 8) | raw[innerIcmpOffset + 5]);
        ushort innerSeq = (ushort)((raw[innerIcmpOffset + 6] << 8) | raw[innerIcmpOffset + 7]);

        return (innerId == expectedId && innerSeq == expectedSeq, src);
    }

    /// <summary>Parse an ICMP Echo Reply; verify ID and seq.</summary>
    public static (bool matched, string responderIp) ParseEchoReply(
        byte[] raw, int rawLen, ushort expectedId, ushort expectedSeq)
    {
        if (rawLen < 28) return (false, string.Empty);

        int ipHeaderLen = (raw[0] & 0x0F) * 4;
        if (ipHeaderLen < 20 || rawLen < ipHeaderLen + 8 || rawLen < 20)
            return (false, string.Empty);

        byte icmpType = raw[ipHeaderLen];
        if (icmpType != TypeEchoReply) return (false, string.Empty);

        string src = $"{raw[12]}.{raw[13]}.{raw[14]}.{raw[15]}";
        ushort id  = (ushort)((raw[ipHeaderLen + 4] << 8) | raw[ipHeaderLen + 5]);
        ushort seq = (ushort)((raw[ipHeaderLen + 6] << 8) | raw[ipHeaderLen + 7]);

        return (id == expectedId && seq == expectedSeq, src);
    }
}
