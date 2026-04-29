using System.Net;
using System.Net.Sockets;
using PortMTR.Enterprise.Analysis;
using PortMTR.Enterprise.Models;
using PortMTR.Enterprise.Protocols;
using PortMTR.Enterprise.Services;

namespace PortMTR.Enterprise.Engine;

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

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
    }

    // ── Main loop ─────────────────────────────────────────────────────────
    private async Task RunLoop(CancellationToken ct)
    {
        int cycle = 0;

        while (!ct.IsCancellationRequested)
        {
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

                for (int b = 0; b < batchSize; b++)
                {
                    int ttl = base_ + b;
                    int probeId = Interlocked.Increment(ref _nextProbeId);
                    var hop = _hops[ttl - 1];

                    tasks[b] = Task.Run(async () =>
                    {
                        try
                        {
                            var timeout = GetProbeTimeout(ttl);
                            var result = await _prober.ProbeAsync(
                                Target, ttl, _flowId, probeId, Port, timeout, ct);

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

                                if (result.IsDestination)
                                {
                                    _uiDispatch(() => hop.Flag = HopFlag.Destination);
                                    Interlocked.Exchange(ref highestHit, ttl);
                                    _destinationHop = ttl;  // Track destination across cycles
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

            // Interruptible interval sleep
            try
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);
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
                    hop.Country = geo.Country;
                    hop.City    = geo.CityName;
                });
            }
        }
        catch { /* enrichment is best-effort */ }
    }

    private TimeSpan GetProbeTimeout(int ttl)
    {
        if (Protocol == ProbeProtocol.ICMP)
            return ProbeTimeout;

        if (Target.AddressFamily == AddressFamily.InterNetworkV6)
            return ProbeTimeout;

        if (!HasRespondingHopBeyond(ttl))
            return ProbeTimeout;

        return ProbeTimeout <= s_darkHopTimeout ? ProbeTimeout : s_darkHopTimeout;
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

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _prober.Dispose();
    }
}
