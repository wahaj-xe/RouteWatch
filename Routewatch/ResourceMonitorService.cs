using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using RouteWatch.Capture;
using RouteWatch.Models;
using SharpPcap;
using SharpPcap.LibPcap;

namespace RouteWatch.Services;

/// <summary>
/// Real-time Windows Resource Monitor service matching resmon.exe accuracy.
/// Uses Event Tracing for Windows (ETW) Kernel Network provider for zero-loss,
/// microsecond-level process and socket throughput tracking.
/// Features seamless fallback to high-performance SharpPcap span capture.
/// </summary>
public sealed class ResourceMonitorService : IDisposable
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    // Caches and socket-to-PID lookup
    private readonly ConcurrentDictionary<int, string> _processNameCache = new();
    private readonly ConcurrentDictionary<string, string> _dnsCache = new();
    private readonly ConcurrentDictionary<string, byte> _dnsPending = new();
    private readonly ConcurrentDictionary<string, int> _socketToPid = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _endpointToPid = new(StringComparer.OrdinalIgnoreCase);

    // Bandwidth accumulators
    private readonly ConcurrentDictionary<int, long> _bytesSentByPid = new();
    private readonly ConcurrentDictionary<int, long> _bytesRcvByPid = new();
    private readonly ConcurrentDictionary<string, long> _bytesSentBySocket = new();
    private readonly ConcurrentDictionary<string, long> _bytesRcvBySocket = new();
    private readonly ConcurrentDictionary<string, long> _bytesSentByEndpoint = new();
    private readonly ConcurrentDictionary<string, long> _bytesRcvByEndpoint = new();
    private readonly ConcurrentDictionary<string, long> _bytesSentByPidEp = new();
    private readonly ConcurrentDictionary<string, long> _bytesRcvByPidEp = new();
    private readonly ConcurrentDictionary<string, long> _bytesSentByPidRemoteIp = new();
    private readonly ConcurrentDictionary<string, long> _bytesRcvByPidRemoteIp = new();

    public NetworkSortMetric SortMetric { get; set; } = NetworkSortMetric.Total;

    private long _totalCapturedSent;
    private long _totalCapturedRcv;

    // Interface statistics safety net
    private long _lastIfaceBytesSent;
    private long _lastIfaceBytesRcv;
    private bool _hasIfaceBaseline;

    // ETW Kernel session
    private TraceEventSession? _etwSession;
    private Thread? _etwThread;
    private bool _isEtwActive;

    // Npcap fallback
    private readonly List<ILiveDevice> _pcapDevices = new();
    private bool _isPcapActive;
    private readonly object _captureLock = new();

    private readonly HashSet<string> _localIpAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _pollTimer;
    private DateTime _lastPollTime = DateTime.UtcNow;

    public event Action<List<ProcessNetworkItem>, List<NetworkConnectionItem>, long, long>? OnDataUpdated;

    public bool IsRunning { get; private set; }

    public ResourceMonitorService()
    {
        _pollTimer = new Timer(OnPollTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _lastPollTime = DateTime.UtcNow;

        RefreshLocalAddresses();
        InitIfaceBaseline();

        // 1. Try starting ETW Kernel Network provider (resmon.exe parity)
        bool etwStarted = StartEtwMeter();

        // 2. If ETW cannot start (e.g. non-admin), start SharpPcap high-performance fallback
        if (!etwStarted)
        {
            StartPcapMeter();
        }

        // Start 1000ms polling timer
        _pollTimer.Change(0, 1000);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);

        StopEtwMeter();
        StopPcapMeter();
    }

    #region Local Address Enumeration & NIC Stats

    private void RefreshLocalAddresses()
    {
        try
        {
            _localIpAddresses.Clear();
            _localIpAddresses.Add("127.0.0.1");
            _localIpAddresses.Add("::1");
            _localIpAddresses.Add("0.0.0.0");
            _localIpAddresses.Add("::");

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    {
                        string ipStr = u.Address.ToString();
                        _localIpAddresses.Add(ipStr);
                        // Also add without IPv6 scope id if present
                        int scopeIdx = ipStr.IndexOf('%');
                        if (scopeIdx > 0)
                        {
                            _localIpAddresses.Add(ipStr.Substring(0, scopeIdx));
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void InitIfaceBaseline()
    {
        try
        {
            long sent = 0;
            long rcv = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var stats = ni.GetIPStatistics();
                sent += stats.BytesSent;
                rcv += stats.BytesReceived;
            }
            _lastIfaceBytesSent = sent;
            _lastIfaceBytesRcv = rcv;
            _hasIfaceBaseline = true;
        }
        catch { }
    }

    private (long sentBps, long rcvBps) ReadIfaceDelta(double elapsedSec)
    {
        if (!_hasIfaceBaseline || elapsedSec <= 0) return (0, 0);
        try
        {
            long currentSent = 0;
            long currentRcv = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var stats = ni.GetIPStatistics();
                currentSent += stats.BytesSent;
                currentRcv += stats.BytesReceived;
            }

            long deltaSent = Math.Max(0, currentSent - _lastIfaceBytesSent);
            long deltaRcv = Math.Max(0, currentRcv - _lastIfaceBytesRcv);
            _lastIfaceBytesSent = currentSent;
            _lastIfaceBytesRcv = currentRcv;

            return ((long)(deltaSent / elapsedSec), (long)(deltaRcv / elapsedSec));
        }
        catch
        {
            return (0, 0);
        }
    }

    #endregion

    #region ETW Kernel Network Provider (resmon.exe engine)

    private bool StartEtwMeter()
    {
        if (!TraceEventSession.IsElevated().GetValueOrDefault(false))
        {
            Debug.WriteLine("[ResourceMonitor] Process not elevated. ETW requires Administrator; falling back to Pcap.");
            return false;
        }

        try
        {
            string sessionName = KernelTraceEventParser.KernelSessionName;

            // Clean up any stale kernel session from previous run
            var existing = TraceEventSession.GetActiveSession(sessionName);
            if (existing != null)
            {
                try
                {
                    existing.Stop(true);
                    Thread.Sleep(60);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ResourceMonitor] Notice stopping existing kernel session: {ex.Message}");
                }
            }

            _etwSession = new TraceEventSession(sessionName)
            {
                StopOnDispose = true,
                BufferSizeMB = 64
            };

            // Enable TCP/IP kernel provider
            _etwSession.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = _etwSession.Source.Kernel;

            // TCP Send (IPv4 & IPv6): local is saddr:sport, remote is daddr:dport
            kernel.TcpIpSend += data => RecordPacket(data.ProcessID, data.size, isSend: true, data.sport, data.daddr, data.dport);
            kernel.TcpIpSendIPV6 += data => RecordPacket(data.ProcessID, data.size, isSend: true, data.sport, data.daddr, data.dport);

            // TCP Recv (IPv4 & IPv6): remote is saddr:sport, local is daddr:dport
            kernel.TcpIpRecv += data => RecordPacket(data.ProcessID, data.size, isSend: false, data.dport, data.saddr, data.sport);
            kernel.TcpIpRecvIPV6 += data => RecordPacket(data.ProcessID, data.size, isSend: false, data.dport, data.saddr, data.sport);

            // UDP Send (IPv4 & IPv6)
            kernel.UdpIpSend += data => RecordPacket(data.ProcessID, data.size, isSend: true, data.sport, data.daddr, data.dport);
            kernel.UdpIpSendIPV6 += data => RecordPacket(data.ProcessID, data.size, isSend: true, data.sport, data.daddr, data.dport);

            // UDP Recv (IPv4 & IPv6)
            kernel.UdpIpRecv += data => RecordPacket(data.ProcessID, data.size, isSend: false, data.dport, data.saddr, data.sport);
            kernel.UdpIpRecvIPV6 += data => RecordPacket(data.ProcessID, data.size, isSend: false, data.dport, data.saddr, data.sport);

            _etwThread = new Thread(() =>
            {
                try
                {
                    _isEtwActive = true;
                    Debug.WriteLine("[ResourceMonitor] ETW Kernel session listening...");
                    _etwSession.Source.Process();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ResourceMonitor] ETW thread exited: {ex.Message}");
                }
                finally
                {
                    _isEtwActive = false;
                }
            })
            {
                IsBackground = true,
                Name = "RouteWatch-Kernel-ETW",
                Priority = ThreadPriority.AboveNormal
            };

            _etwThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ResourceMonitor] Could not start ETW session: {ex.Message}");
            _isEtwActive = false;
            _etwSession = null;
            return false;
        }
    }

    private void StopEtwMeter()
    {
        if (_etwSession == null && !_isEtwActive) return;
        try
        {
            _etwSession?.Stop();
            _etwThread?.Join(400);
            _etwSession?.Dispose();
        }
        catch { }
        finally
        {
            _etwSession = null;
            _isEtwActive = false;
            _etwThread = null;
        }
    }

    public static string NormalizeIpString(string ipStr)
    {
        if (string.IsNullOrEmpty(ipStr)) return ipStr;
        if (ipStr.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
            return ipStr.Substring(7);
        return ipStr;
    }

    public static string NormalizeIp(IPAddress? ip)
    {
        if (ip == null) return "";
        if (ip.IsIPv4MappedToIPv6)
            return ip.MapToIPv4().ToString();
        string s = ip.ToString();
        if (s.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
            return s.Substring(7);
        return s;
    }

    private void RecordPacket(int processId, int size, bool isSend, int localPort, IPAddress remoteIp, int remotePort)
    {
        if (size <= 0) return;

        string remoteIpStr = NormalizeIp(remoteIp);
        string exactKey = $"{localPort}:{remoteIpStr}:{remotePort}";
        string epKey = $"{remoteIpStr}:{remotePort}";

        // If kernel reported System (PID 4) or Idle (PID 0) due to DPC / interrupt context,
        // map it to the actual socket owning process.
        int targetPid = processId;
        if (targetPid <= 4)
        {
            if (_socketToPid.TryGetValue(exactKey, out int mappedPid) && mappedPid > 4)
            {
                targetPid = mappedPid;
            }
            else if (_endpointToPid.TryGetValue(epKey, out int mappedEpPid) && mappedEpPid > 4)
            {
                targetPid = mappedEpPid;
            }
        }
        else
        {
            _socketToPid[exactKey] = targetPid;
            _endpointToPid[epKey] = targetPid;
        }

        string pidEpKey = $"{targetPid}:{remoteIpStr}:{remotePort}";
        string pidRemoteIpKey = $"{targetPid}:{remoteIpStr}";

        if (isSend)
        {
            _bytesSentByPid.AddOrUpdate(targetPid, size, (_, c) => c + size);
            _bytesSentBySocket.AddOrUpdate(exactKey, size, (_, c) => c + size);
            _bytesSentByEndpoint.AddOrUpdate(epKey, size, (_, c) => c + size);
            _bytesSentByPidEp.AddOrUpdate(pidEpKey, size, (_, c) => c + size);
            _bytesSentByPidRemoteIp.AddOrUpdate(pidRemoteIpKey, size, (_, c) => c + size);
            Interlocked.Add(ref _totalCapturedSent, size);
        }
        else
        {
            _bytesRcvByPid.AddOrUpdate(targetPid, size, (_, c) => c + size);
            _bytesRcvBySocket.AddOrUpdate(exactKey, size, (_, c) => c + size);
            _bytesRcvByEndpoint.AddOrUpdate(epKey, size, (_, c) => c + size);
            _bytesRcvByPidEp.AddOrUpdate(pidEpKey, size, (_, c) => c + size);
            _bytesRcvByPidRemoteIp.AddOrUpdate(pidRemoteIpKey, size, (_, c) => c + size);
            Interlocked.Add(ref _totalCapturedRcv, size);
        }
    }

    #endregion

    #region High-Performance SharpPcap Fallback

    private void StartPcapMeter()
    {
        lock (_captureLock)
        {
            if (_isPcapActive) return;
            try
            {
                var devices = CaptureDeviceList.Instance;
                if (devices.Count == 0) return;

                var targetDevices = devices.OfType<LibPcapLiveDevice>()
                    .Where(d =>
                        !d.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase) &&
                        !d.Description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) &&
                        d.Addresses.Any(a => a.Addr?.ipAddress != null &&
                            !a.Addr.ipAddress.ToString().StartsWith("127.") &&
                            !a.Addr.ipAddress.ToString().StartsWith("169.254.")))
                    .ToList();

                if (targetDevices.Count == 0)
                {
                    targetDevices = devices.OfType<LibPcapLiveDevice>()
                        .Where(d => !d.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                _pcapDevices.Clear();
                foreach (var dev in targetDevices)
                {
                    try
                    {
                        dev.Open(mode: DeviceModes.Promiscuous | DeviceModes.MaxResponsiveness, read_timeout: 10);
                        dev.Filter = "ip or ip6";
                        dev.OnPacketArrival += FastPcapPacketHandler;
                        dev.StartCapture();
                        _pcapDevices.Add(dev);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ResourceMonitor] Pcap could not open {dev.Description}: {ex.Message}");
                    }
                }

                if (_pcapDevices.Count > 0)
                {
                    _isPcapActive = true;
                    Debug.WriteLine($"[ResourceMonitor] SharpPcap fallback capturing on {_pcapDevices.Count} devices.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ResourceMonitor] SharpPcap start notice: {ex.Message}");
            }
        }
    }

    private void StopPcapMeter()
    {
        lock (_captureLock)
        {
            if (!_isPcapActive) return;
            foreach (var dev in _pcapDevices)
            {
                try
                {
                    dev.OnPacketArrival -= FastPcapPacketHandler;
                    dev.StopCapture();
                    dev.Close();
                }
                catch { }
            }
            _pcapDevices.Clear();
            _isPcapActive = false;
        }
    }

    private void FastPcapPacketHandler(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            ReadOnlySpan<byte> data = raw.Data;
            if (data.Length < 34) return;

            ushort etherType = (ushort)((data[12] << 8) | data[13]);
            int ipOffset = 14;

            if (etherType == 0x8100 && data.Length >= 38) // 802.1Q VLAN
            {
                etherType = (ushort)((data[16] << 8) | data[17]);
                ipOffset = 18;
            }

            int payloadLen = data.Length;
            int protocol = 0;
            IPAddress? srcIp = null;
            IPAddress? dstIp = null;
            int srcPort = 0;
            int dstPort = 0;

            if (etherType == 0x0800) // IPv4
            {
                if (data.Length < ipOffset + 20) return;
                int ihl = (data[ipOffset] & 0x0F) * 4;
                protocol = data[ipOffset + 9];
                srcIp = new IPAddress(data.Slice(ipOffset + 12, 4));
                dstIp = new IPAddress(data.Slice(ipOffset + 16, 4));
                int transportOffset = ipOffset + ihl;

                if (data.Length >= transportOffset + 4 && (protocol == 6 || protocol == 17))
                {
                    srcPort = (data[transportOffset] << 8) | data[transportOffset + 1];
                    dstPort = (data[transportOffset + 2] << 8) | data[transportOffset + 3];
                }
            }
            else if (etherType == 0x86DD) // IPv6
            {
                if (data.Length < ipOffset + 40) return;
                protocol = data[ipOffset + 6];
                srcIp = new IPAddress(data.Slice(ipOffset + 8, 16));
                dstIp = new IPAddress(data.Slice(ipOffset + 24, 16));
                int transportOffset = ipOffset + 40;

                if (data.Length >= transportOffset + 4 && (protocol == 6 || protocol == 17))
                {
                    srcPort = (data[transportOffset] << 8) | data[transportOffset + 1];
                    dstPort = (data[transportOffset + 2] << 8) | data[transportOffset + 3];
                }
            }
            else
            {
                return;
            }

            if (srcIp == null || dstIp == null || (protocol != 6 && protocol != 17)) return;

            string srcIpStr = srcIp.ToString();
            string dstIpStr = dstIp.ToString();

            bool isInbound = _localIpAddresses.Contains(dstIpStr);
            bool isOutbound = _localIpAddresses.Contains(srcIpStr);

            if (!isInbound && !isOutbound)
            {
                isInbound = (srcPort == 80 || srcPort == 443 || srcPort == 8080 || srcPort < dstPort);
            }

            if (isInbound)
            {
                string exactKey = $"{dstPort}:{srcIpStr}:{srcPort}";
                string epKey = $"{srcIpStr}:{srcPort}";
                _bytesRcvBySocket.AddOrUpdate(exactKey, payloadLen, (_, c) => c + payloadLen);
                _bytesRcvByEndpoint.AddOrUpdate(epKey, payloadLen, (_, c) => c + payloadLen);
                Interlocked.Add(ref _totalCapturedRcv, payloadLen);
            }
            else
            {
                string exactKey = $"{srcPort}:{dstIpStr}:{dstPort}";
                string epKey = $"{dstIpStr}:{dstPort}";
                _bytesSentBySocket.AddOrUpdate(exactKey, payloadLen, (_, c) => c + payloadLen);
                _bytesSentByEndpoint.AddOrUpdate(epKey, payloadLen, (_, c) => c + payloadLen);
                Interlocked.Add(ref _totalCapturedSent, payloadLen);
            }
        }
        catch { }
    }

    #endregion

    #region Polling & Data Reconciliation Loop

    private static Dictionary<TKey, long> DrainAccumulator<TKey>(ConcurrentDictionary<TKey, long> dict) where TKey : notnull
    {
        var result = new Dictionary<TKey, long>();
        foreach (var key in dict.Keys)
        {
            if (dict.TryRemove(key, out long val) && val > 0)
            {
                result[key] = val;
            }
        }
        return result;
    }

    private void OnPollTick(object? state)
    {
        if (!IsRunning) return;

        try
        {
            var now = DateTime.UtcNow;
            double elapsedSec = Math.Max(0.5, (now - _lastPollTime).TotalSeconds);
            _lastPollTime = now;

            // 1. Drain atomic accumulators
            var sentByPid = DrainAccumulator(_bytesSentByPid);
            var rcvByPid = DrainAccumulator(_bytesRcvByPid);
            var sentBySocket = DrainAccumulator(_bytesSentBySocket);
            var rcvBySocket = DrainAccumulator(_bytesRcvBySocket);
            var sentByEp = DrainAccumulator(_bytesSentByEndpoint);
            var rcvByEp = DrainAccumulator(_bytesRcvByEndpoint);
            var sentByPidEp = DrainAccumulator(_bytesSentByPidEp);
            var rcvByPidEp = DrainAccumulator(_bytesRcvByPidEp);
            var sentByPidRemoteIp = DrainAccumulator(_bytesSentByPidRemoteIp);
            var rcvByPidRemoteIp = DrainAccumulator(_bytesRcvByPidRemoteIp);

            long capturedSentBytes = Interlocked.Exchange(ref _totalCapturedSent, 0);
            long capturedRcvBytes = Interlocked.Exchange(ref _totalCapturedRcv, 0);

            // 2. Query all active TCP & UDP connections from kernel
            var rawConnections = QueryAllSockets();

            // Refresh socket mapping caches
            foreach (var conn in rawConnections)
            {
                if (conn.Pid > 0)
                {
                    string remoteAddress = NormalizeIpString(conn.RemoteAddress);
                    string exactKey = $"{conn.LocalPort}:{remoteAddress}:{conn.RemotePort}";
                    string epKey = $"{remoteAddress}:{conn.RemotePort}";
                    _socketToPid[exactKey] = conn.Pid;
                    _endpointToPid[epKey] = conn.Pid;
                }
            }

            // 3. Match connection throughput
            var connectionItems = new List<NetworkConnectionItem>(rawConnections.Count);
            var processMap = new Dictionary<int, ProcessNetworkItem>();

            // Group raw connections by process to allow fair distribution of PID-level throughput
            var connsByPid = rawConnections.GroupBy(c => c.Pid).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var conn in rawConnections)
            {
                string remoteAddress = NormalizeIpString(conn.RemoteAddress);
                string exactKey = $"{conn.LocalPort}:{remoteAddress}:{conn.RemotePort}";
                string epKey = $"{remoteAddress}:{conn.RemotePort}";
                string pidEpKey = $"{conn.Pid}:{remoteAddress}:{conn.RemotePort}";
                string pidRemoteIpKey = $"{conn.Pid}:{remoteAddress}";

                sentBySocket.TryGetValue(exactKey, out long bytesSent);
                if (bytesSent == 0 && sentByPidEp.TryGetValue(pidEpKey, out long pEpSent))
                {
                    bytesSent = pEpSent;
                }
                if (bytesSent == 0 && sentByPidRemoteIp.TryGetValue(pidRemoteIpKey, out long pRipSent))
                {
                    bytesSent = pRipSent;
                }
                if (bytesSent == 0 && sentByEp.TryGetValue(epKey, out long epSent))
                {
                    bytesSent = epSent;
                }

                rcvBySocket.TryGetValue(exactKey, out long bytesRcv);
                if (bytesRcv == 0 && rcvByPidEp.TryGetValue(pidEpKey, out long pEpRcv))
                {
                    bytesRcv = pEpRcv;
                }
                if (bytesRcv == 0 && rcvByPidRemoteIp.TryGetValue(pidRemoteIpKey, out long pRipRcv))
                {
                    bytesRcv = pRipRcv;
                }
                if (bytesRcv == 0 && rcvByEp.TryGetValue(epKey, out long epRcv))
                {
                    bytesRcv = epRcv;
                }

                long sendBps = (long)(bytesSent / elapsedSec);
                long rcvBps = (long)(bytesRcv / elapsedSec);

                // Queue DNS resolve for external IPs
                string remoteHost = conn.RemoteAddress;
                if (!IsLoopbackOrLocal(conn.RemoteAddress))
                {
                    if (_dnsCache.TryGetValue(conn.RemoteAddress, out var cachedHost))
                    {
                        remoteHost = cachedHost;
                    }
                    else
                    {
                        QueueDnsResolve(conn.RemoteAddress);
                    }
                }

                var item = new NetworkConnectionItem
                {
                    Pid = conn.Pid,
                    ImageName = conn.ProcessName,
                    LocalAddress = conn.LocalAddress,
                    LocalPort = conn.LocalPort,
                    RemoteAddress = conn.RemoteAddress,
                    RemoteHostName = remoteHost,
                    RemotePort = conn.RemotePort,
                    Protocol = conn.Protocol,
                    State = conn.State,
                    SendBytesPerSec = sendBps,
                    ReceiveBytesPerSec = rcvBps,
                    TotalBytesPerSec = sendBps + rcvBps,
                    LatencyMs = -1
                };

                connectionItems.Add(item);

                // Process aggregation
                if (!processMap.TryGetValue(conn.Pid, out var procItem))
                {
                    procItem = new ProcessNetworkItem
                    {
                        Pid = conn.Pid,
                        ImageName = conn.ProcessName,
                        SendBytesPerSec = 0,
                        ReceiveBytesPerSec = 0,
                        TotalBytesPerSec = 0,
                        ActiveConnectionsCount = 0
                    };
                    processMap[conn.Pid] = procItem;
                }

                procItem.SendBytesPerSec += sendBps;
                procItem.ReceiveBytesPerSec += rcvBps;
                procItem.TotalBytesPerSec += (sendBps + rcvBps);
                procItem.ActiveConnectionsCount++;
            }

            // 4. Reconcile PID-level throughput (ensures high-throughput streams like Speedtest.exe match Resmon)
            foreach (var kvp in processMap)
            {
                int pid = kvp.Key;
                var procItem = kvp.Value;

                long pidSentRate = sentByPid.TryGetValue(pid, out long sBytes) ? (long)(sBytes / elapsedSec) : 0;
                long pidRcvRate = rcvByPid.TryGetValue(pid, out long rBytes) ? (long)(rBytes / elapsedSec) : 0;

                // If PID throughput is higher than sum of matched sockets, assign true PID rate
                if (pidSentRate > procItem.SendBytesPerSec)
                {
                    long diff = pidSentRate - procItem.SendBytesPerSec;
                    procItem.SendBytesPerSec = pidSentRate;

                    // Distribute remaining throughput only to Established non-local connections
                    var pConns = connectionItems.Where(c => c.Pid == pid && 
                                                            !IsLoopbackOrLocal(c.RemoteAddress) && 
                                                            c.State.Equals("Established", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (pConns.Count == 0)
                    {
                        pConns = connectionItems.Where(c => c.Pid == pid && !IsLoopbackOrLocal(c.RemoteAddress)).ToList();
                    }

                    if (pConns.Count > 0)
                    {
                        var targetConn = pConns.FirstOrDefault(c => c.SendBytesPerSec > 0)
                                      ?? pConns.FirstOrDefault(c => c.RemotePort == 443 || c.RemotePort == 80)
                                      ?? pConns[0];
                        targetConn.SendBytesPerSec += diff;
                        targetConn.TotalBytesPerSec = targetConn.SendBytesPerSec + targetConn.ReceiveBytesPerSec;
                    }
                }

                if (pidRcvRate > procItem.ReceiveBytesPerSec)
                {
                    long diff = pidRcvRate - procItem.ReceiveBytesPerSec;
                    procItem.ReceiveBytesPerSec = pidRcvRate;

                    // Distribute remaining throughput only to Established non-local connections
                    var pConns = connectionItems.Where(c => c.Pid == pid && 
                                                            !IsLoopbackOrLocal(c.RemoteAddress) && 
                                                            c.State.Equals("Established", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (pConns.Count == 0)
                    {
                        pConns = connectionItems.Where(c => c.Pid == pid && !IsLoopbackOrLocal(c.RemoteAddress)).ToList();
                    }

                    if (pConns.Count > 0)
                    {
                        var targetConn = pConns.FirstOrDefault(c => c.ReceiveBytesPerSec > 0)
                                      ?? pConns.FirstOrDefault(c => c.RemotePort == 443 || c.RemotePort == 80)
                                      ?? pConns[0];
                        targetConn.ReceiveBytesPerSec += diff;
                        targetConn.TotalBytesPerSec = targetConn.SendBytesPerSec + targetConn.ReceiveBytesPerSec;
                    }
                }

                procItem.TotalBytesPerSec = procItem.SendBytesPerSec + procItem.ReceiveBytesPerSec;
            }

            // Also include any processes that had PID-level activity but no open socket remaining
            foreach (var pid in sentByPid.Keys.Concat(rcvByPid.Keys).Distinct())
            {
                if (!processMap.ContainsKey(pid))
                {
                    long sRate = sentByPid.TryGetValue(pid, out long s) ? (long)(s / elapsedSec) : 0;
                    long rRate = rcvByPid.TryGetValue(pid, out long r) ? (long)(r / elapsedSec) : 0;
                    if (sRate > 0 || rRate > 0)
                    {
                        processMap[pid] = new ProcessNetworkItem
                        {
                            Pid = pid,
                            ImageName = GetProcessName(pid),
                            SendBytesPerSec = sRate,
                            ReceiveBytesPerSec = rRate,
                            TotalBytesPerSec = sRate + rRate,
                            ActiveConnectionsCount = 0
                        };
                    }
                }
            }

            // 5. Total system bandwidth calculation
            long totalSystemSent = (long)(capturedSentBytes / elapsedSec);
            long totalSystemRcv = (long)(capturedRcvBytes / elapsedSec);

            long sumProcSent = processMap.Values.Sum(p => p.SendBytesPerSec);
            long sumProcRcv = processMap.Values.Sum(p => p.ReceiveBytesPerSec);

            totalSystemSent = Math.Max(totalSystemSent, sumProcSent);
            totalSystemRcv = Math.Max(totalSystemRcv, sumProcRcv);

            // Read hardware NIC delta
            var (nicSent, nicRcv) = ReadIfaceDelta(elapsedSec);
            totalSystemSent = Math.Max(totalSystemSent, nicSent);
            totalSystemRcv = Math.Max(totalSystemRcv, nicRcv);

            // 6. Sort for display based on SortMetric
            var sortedProcesses = SortMetric switch
            {
                NetworkSortMetric.Send => processMap.Values
                    .OrderByDescending(p => p.SendBytesPerSec)
                    .ThenByDescending(p => p.TotalBytesPerSec)
                    .ToList(),
                NetworkSortMetric.Receive => processMap.Values
                    .OrderByDescending(p => p.ReceiveBytesPerSec)
                    .ThenByDescending(p => p.TotalBytesPerSec)
                    .ToList(),
                _ => processMap.Values
                    .OrderByDescending(p => p.TotalBytesPerSec)
                    .ThenByDescending(p => p.ActiveConnectionsCount)
                    .ToList()
            };

            var sortedConnections = SortMetric switch
            {
                NetworkSortMetric.Send => connectionItems
                    .OrderByDescending(c => c.SendBytesPerSec)
                    .ThenByDescending(c => c.TotalBytesPerSec)
                    .ToList(),
                NetworkSortMetric.Receive => connectionItems
                    .OrderByDescending(c => c.ReceiveBytesPerSec)
                    .ThenByDescending(c => c.TotalBytesPerSec)
                    .ToList(),
                _ => connectionItems
                    .OrderByDescending(c => c.TotalBytesPerSec)
                    .ThenBy(c => c.RemotePort)
                    .ToList()
            };

            // Fire UI event
            OnDataUpdated?.Invoke(sortedProcesses, sortedConnections, totalSystemSent, totalSystemRcv);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ResourceMonitor] Polling tick notice: {ex.Message}");
        }
    }

    private void QueueDnsResolve(string ipStr)
    {
        if (_dnsPending.TryAdd(ipStr, 0))
        {
            Task.Run(async () =>
            {
                try
                {
                    if (IPAddress.TryParse(ipStr, out var ip))
                    {
                        var entry = await Dns.GetHostEntryAsync(ip);
                        if (!string.IsNullOrWhiteSpace(entry.HostName))
                        {
                            _dnsCache[ipStr] = entry.HostName;
                            return;
                        }
                    }
                }
                catch { }
                _dnsCache[ipStr] = ipStr;
            });
        }
    }

    private static bool IsLoopbackOrLocal(string ipStr)
    {
        return ipStr == "127.0.0.1" || ipStr == "::1" || ipStr == "0.0.0.0" || ipStr == "::" || ipStr == "*";
    }

    private string GetProcessName(int pid)
    {
        if (pid <= 0) return "System Idle";
        if (pid == 4) return "System";
        if (_processNameCache.TryGetValue(pid, out var name)) return name;

        try
        {
            var p = Process.GetProcessById(pid);
            name = p.ProcessName + ".exe";
        }
        catch
        {
            name = "Unknown";
        }

        _processNameCache[pid] = name;
        return name;
    }

    #endregion

    #region Socket Enumeration P/Invoke

    private struct RawSocketInfo
    {
        public int Pid;
        public string ProcessName;
        public string LocalAddress;
        public int LocalPort;
        public string RemoteAddress;
        public int RemotePort;
        public string State;
        public string Protocol;
    }

    private List<RawSocketInfo> QueryAllSockets()
    {
        var list = new List<RawSocketInfo>(128);

        // 1. IPv4 TCP
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size > 0)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int num = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);

                    for (int i = 0; i < num; i++)
                    {
                        int stateInt = Marshal.ReadInt32(rowPtr, 0);
                        uint laddr = (uint)Marshal.ReadInt32(rowPtr, 4);
                        int lportRaw = Marshal.ReadInt32(rowPtr, 8);
                        uint raddr = (uint)Marshal.ReadInt32(rowPtr, 12);
                        int rportRaw = Marshal.ReadInt32(rowPtr, 16);
                        int pid = Marshal.ReadInt32(rowPtr, 20);

                        int lport = ((lportRaw & 0xFF) << 8) | ((lportRaw >> 8) & 0xFF);
                        int rport = ((rportRaw & 0xFF) << 8) | ((rportRaw >> 8) & 0xFF);

                        list.Add(new RawSocketInfo
                        {
                            Pid = pid,
                            ProcessName = GetProcessName(pid),
                            LocalAddress = new IPAddress(laddr).ToString(),
                            LocalPort = lport,
                            RemoteAddress = new IPAddress(raddr).ToString(),
                            RemotePort = rport,
                            State = FormatTcpState(stateInt),
                            Protocol = "TCP"
                        });

                        rowPtr = IntPtr.Add(rowPtr, 24);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // 2. IPv6 TCP
        size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size > 0)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, true, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int num = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);
                    byte[] lBytes = new byte[16];
                    byte[] rBytes = new byte[16];

                    for (int i = 0; i < num; i++)
                    {
                        Marshal.Copy(rowPtr, lBytes, 0, 16);
                        int lportRaw = Marshal.ReadInt32(rowPtr, 20);
                        Marshal.Copy(IntPtr.Add(rowPtr, 24), rBytes, 0, 16);
                        int rportRaw = Marshal.ReadInt32(rowPtr, 44);
                        int stateInt = Marshal.ReadInt32(rowPtr, 48);
                        int pid = Marshal.ReadInt32(rowPtr, 52);

                        int lport = ((lportRaw & 0xFF) << 8) | ((lportRaw >> 8) & 0xFF);
                        int rport = ((rportRaw & 0xFF) << 8) | ((rportRaw >> 8) & 0xFF);

                        list.Add(new RawSocketInfo
                        {
                            Pid = pid,
                            ProcessName = GetProcessName(pid),
                            LocalAddress = new IPAddress(lBytes).ToString(),
                            LocalPort = lport,
                            RemoteAddress = new IPAddress(rBytes).ToString(),
                            RemotePort = rport,
                            State = FormatTcpState(stateInt),
                            Protocol = "TCP"
                        });

                        rowPtr = IntPtr.Add(rowPtr, 56);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // 3. IPv4 UDP
        size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
        if (size > 0)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buffer, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0) == 0)
                {
                    int num = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);

                    for (int i = 0; i < num; i++)
                    {
                        uint laddr = (uint)Marshal.ReadInt32(rowPtr, 0);
                        int lportRaw = Marshal.ReadInt32(rowPtr, 4);
                        int pid = Marshal.ReadInt32(rowPtr, 8);

                        int lport = ((lportRaw & 0xFF) << 8) | ((lportRaw >> 8) & 0xFF);

                        list.Add(new RawSocketInfo
                        {
                            Pid = pid,
                            ProcessName = GetProcessName(pid),
                            LocalAddress = new IPAddress(laddr).ToString(),
                            LocalPort = lport,
                            RemoteAddress = "*",
                            RemotePort = 0,
                            State = "Bound",
                            Protocol = "UDP"
                        });

                        rowPtr = IntPtr.Add(rowPtr, 12);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // 4. IPv6 UDP
        size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET6, UDP_TABLE_OWNER_PID, 0);
        if (size > 0)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buffer, ref size, true, AF_INET6, UDP_TABLE_OWNER_PID, 0) == 0)
                {
                    int num = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);
                    byte[] lBytes = new byte[16];

                    for (int i = 0; i < num; i++)
                    {
                        Marshal.Copy(rowPtr, lBytes, 0, 16);
                        int lportRaw = Marshal.ReadInt32(rowPtr, 20);
                        int pid = Marshal.ReadInt32(rowPtr, 24);

                        int lport = ((lportRaw & 0xFF) << 8) | ((lportRaw >> 8) & 0xFF);

                        list.Add(new RawSocketInfo
                        {
                            Pid = pid,
                            ProcessName = GetProcessName(pid),
                            LocalAddress = new IPAddress(lBytes).ToString(),
                            LocalPort = lport,
                            RemoteAddress = "*",
                            RemotePort = 0,
                            State = "Bound",
                            Protocol = "UDP"
                        });

                        rowPtr = IntPtr.Add(rowPtr, 28);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        return list;
    }

    private static string FormatTcpState(int stateInt) => stateInt switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynRcvd",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTCB",
        _ => $"State_{stateInt}"
    };

    #endregion

    public void Dispose()
    {
        Stop();
        _pollTimer.Dispose();
        _etwSession?.Dispose();
    }
}
