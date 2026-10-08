using System.Net;
using System.Net.Sockets;
using RouteWatch.Analysis;
using RouteWatch.Models;
using RouteWatch.Protocols;
using RouteWatch.Services;

namespace RouteWatch.Engine;

/// <summary>
/// Core MTR engine: runs continuous TTL sweeps, dispatches probes in parallel,
/// updates HopModel stats, triggers DNS + GeoIP enrichment, and fires
/// progress events consumed by the ViewModel.
/// </summary>
public sealed class MTREngine : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────
    public IPAddress  Target      { get; }
    public int        MaxHops     { get; set; } = 30;
    public ProbeProtocol Protocol { get; }
    public int        Port        { get; }
    public TimeSpan   Interval    { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan   ProbeTimeout{ get; set; } = TimeSpan.FromSeconds(3);
    public int        PacketSize  { get; set; } = 52;
    public int        ParallelHops{ get; set; } = 4;   // hops probed concurrently

    // ── State ─────────────────────────────────────────────────────────────
    public  IReadOnlyList<HopModel> Hops => _hops;
    public  int    Cycles { get; private set; }
    public  double StabilityScore { get; private set; } = 100;

    // ── Events ────────────────────────────────────────────────────────────
    public event Action<HopModel>? OnHopUpdated;
    public event Action<int>?      OnCycleComplete;   // passes cycle number
    public event Action<int>?      OnDestinationReached;
    public event Action<string>?   OnError;

    // ── Internals ─────────────────────────────────────────────────────────
    private readonly List<HopModel>   _hops    = new();
    private readonly IProber          _prober;
    private readonly DnsService       _dns;
    private readonly GeoIpService     _geo;
    private readonly Action<Action>   _uiDispatch;
    private CancellationTokenSource?  _cts;
    private Task?                      _runTask;
    private int                       _destinationHop = 0;  // Track which hop is the destination
    private int                       _nextProbeId;
    private int                       _flowId;
    private static readonly TimeSpan   s_darkHopTimeout = TimeSpan.FromMilliseconds(300);

    public MTREngine(
        IPAddress      target,
        ProbeProtocol  protocol,
        int            port,
        IProber        prober,
        DnsService     dns,
        GeoIpService   geo,
        Action<Action> uiDispatch)
    {
        Target      = target;
        Protocol    = protocol;
        Port        = port;
        _prober     = prober;
        _dns        = dns;
        _geo        = geo;
        _uiDispatch = uiDispatch;

        // Pre-populate hop models
        for (int i = 1; i <= 64; i++)
            _hops.Add(new HopModel(i));
    }

    // ── Public control ────────────────────────────────────────────────────
    public void Start()
    {
        _destinationHop = 0;  // Reset for new trace
        _nextProbeId = 0;
        _flowId = Random.Shared.Next(1, int.MaxValue);
        _cts = new CancellationTokenSource();
        _runTask = RunLoop(_cts.Token);
    }

    public int DestinationHop => _destinationHop;

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    // ── Main loop ─────────────────────────────────────────────────────────
    private async Task RunLoop(CancellationToken ct)
    {
        int cycle = 0;

        while (!ct.IsCancellationRequested)
        {
            var cycleSw = System.Diagnostics.Stopwatch.StartNew();
            int highestHit = 0;

            // Determine probe range: stop exactly at destination, or probe all if not found yet
            int probeUpTo = _destinationHop > 0 ? _destinationHop : MaxHops;
            // ICMP can probe in parallel; TCP/UDP need sequential probing for proper TTL-based routing
            int parallelism = Protocol == ProbeProtocol.ICMP ? ParallelHops : 1;

            // Probe hops in batches
            for (int base_ = 1; base_ <= probeUpTo && !ct.IsCancellationRequested; base_ += parallelism)
            {
                int batchSize = Math.Min(parallelism, probeUpTo - base_ + 1);
                var tasks = new Task[batchSize];

                // Gentle inter-hop pacing delay (25ms) prevents router control-plane rate limiting
                if (parallelism == 1 && base_ > 1)
                {
                    try { await Task.Delay(25, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }

                for (int b = 0; b < batchSize; b++)
                {
                    if (b > 0)
                    {
                        // Inter-probe pacing delay prevents microbursts from overwhelming
                        // local Wi-Fi/gateway stacks and triggering router ICMP rate-limiting
                        try { await Task.Delay(35, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }

                    int ttl = base_ + b;
                    int probeId = Interlocked.Increment(ref _nextProbeId);
                    var hop = _hops[ttl - 1];

                    tasks[b] = Task.Run(async () =>
                    {
                        try
                        {
                            var timeout = GetProbeTimeout(ttl);
                            var result = await _prober.ProbeAsync(
                                Target, ttl, _flowId, probeId, Port, timeout, ct).ConfigureAwait(false);

                            if (result.IsTimeout)
                            {
                                hop.RecordTimeout(_uiDispatch);
                            }
                            else
                            {
                                string ip = result.ResponderIp?.ToString() ?? "???";
                                hop.RecordReply(ip, result.RttMs, _uiDispatch);

                                // Async enrichment (fire and forget — don't stall the probe loop)
                                _ = EnrichHopAsync(hop, result.ResponderIp!, ct);

                                bool isTarget = result.IsDestination &&
                                                result.ResponderIp != null &&
                                                result.ResponderIp.Equals(Target);

                                // A remote target cannot be reached at TTL 1
                                if (isTarget && ttl == 1 && !IsLocalOrLoopback(Target))
                                {
                                    isTarget = false;
                                }

                                if (isTarget)
                                {
                                    _uiDispatch(() => hop.Flag = HopFlag.Destination);
                                    Interlocked.Exchange(ref highestHit, ttl);
                                    int previousDestination = Interlocked.CompareExchange(ref _destinationHop, ttl, 0);
                                    if (previousDestination == 0 || ttl < previousDestination)
                                    {
                                        if (previousDestination != 0)
                                            Interlocked.Exchange(ref _destinationHop, ttl);
                                        OnDestinationReached?.Invoke(ttl);
                                    }
                                }
                            }

                            OnHopUpdated?.Invoke(hop);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            OnError?.Invoke($"Probe error at TTL {ttl}: {ex.Message}");
                        }
                    }, ct);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // If destination reached in this batch, skip remaining hops
                if (highestHit > 0) break;
            }

            // Post-sweep analysis
            int analysisUpTo = _destinationHop > 0 ? _destinationHop : MaxHops;
            HopAnalyzer.AnalyseAll(_hops.Take(analysisUpTo).ToList());
            StabilityScore = HopAnalyzer.ComputeStabilityScore(
                _hops.Where(h => h.Sent > 0).ToList());

            cycle++;
            Cycles = cycle;
            OnCycleComplete?.Invoke(cycle);

            cycleSw.Stop();
            // Ensure minimum inter-cycle pause floor (at least 750ms) to maintain a steady cadence
            // and prevent back-to-back sweeps from triggering router/gateway ICMP rate-limiting
            int minFloorMs = (int)Math.Max(500, Interval.TotalMilliseconds * 0.75);
            int remainingSleep = Math.Max(minFloorMs, (int)(Interval.TotalMilliseconds - cycleSw.ElapsedMilliseconds));
            try
            {
                await Task.Delay(remainingSleep, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task EnrichHopAsync(HopModel hop, IPAddress ip, CancellationToken ct)
    {
        try
        {
            // DNS
            if (_dns.Enabled && hop.HostName == "???")
            {
                string name = await _dns.ResolveAsync(ip).ConfigureAwait(false);
                _uiDispatch(() => hop.HostName = name);
            }

            // GeoIP
            if (_geo.IsAvailable && string.IsNullOrEmpty(hop.Country))
            {
                var geo = await _geo.LookupAsync(ip).ConfigureAwait(false);
                _uiDispatch(() =>
                {
                    hop.Country = string.IsNullOrWhiteSpace(geo.CountryName) ? geo.Country : geo.CountryName;
                    hop.City    = geo.CityName;
                    hop.Asn     = string.IsNullOrWhiteSpace(geo.Asn) ? "ASN: —" : geo.Asn;
                });
            }
        }
        catch { /* enrichment is best-effort */ }
    }

    private TimeSpan GetProbeTimeout(int ttl)
    {
        // Never shorten timeout for IPv6 or invalid indices
        if (Target.AddressFamily == AddressFamily.InterNetworkV6 || ttl <= 0 || ttl > _hops.Count)
            return ProbeTimeout;

        var hop = _hops[ttl - 1];

        // Responding routers must ALWAYS receive the full user-configured timeout!
        if (hop.Received > 0)
            return ProbeTimeout;

        // Only after multiple attempts with ZERO replies and with a verified responding hop beyond it,
        // treat as a confirmed dark/silent hop to keep sweep cycles responsive.
        if (hop.Sent >= 2 && HasRespondingHopBeyond(ttl))
        {
            return ProbeTimeout <= s_darkHopTimeout ? ProbeTimeout : s_darkHopTimeout;
        }

        return ProbeTimeout;
    }

    private bool HasRespondingHopBeyond(int ttl)
    {
        int upperBound = _destinationHop > 0 ? _destinationHop : MaxHops;
        for (int i = ttl; i < upperBound && i < _hops.Count; i++)
        {
            if (_hops[i].Received > 0)
                return true;
        }
        return false;
    }

    private static bool IsLocalOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _prober.Dispose();
    }
}
