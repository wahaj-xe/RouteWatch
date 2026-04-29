using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using PortMTR.Enterprise.Models;

namespace PortMTR.Enterprise.Models;

/// <summary>
/// Thread-safe per-hop statistics model.
/// Property mutations must be dispatched to the UI thread when WPF is bound.
/// </summary>
public sealed partial class HopModel : ObservableObject
{
    private readonly object _lock = new();
    private readonly List<double> _rawRtts = new(256);

    public HopModel(int ttl) => Ttl = ttl;

    // ── Identity ──────────────────────────────────────────────────────────
    public int Ttl { get; }

    [ObservableProperty] private string _hostName = "???";
    [ObservableProperty] private string _ipAddress = "???";

    // ── Counters ──────────────────────────────────────────────────────────
    [ObservableProperty] private int  _sent;
    [ObservableProperty] private int  _received;

    // ── RTT metrics ───────────────────────────────────────────────────────
    [ObservableProperty] private double _last   = -1;
    [ObservableProperty] private double _avg;
    [ObservableProperty] private double _best   = double.MaxValue;
    [ObservableProperty] private double _worst;
    [ObservableProperty] private double _jitter;
    [ObservableProperty] private double _stdDev;
    [ObservableProperty] private double _loss;

    // ── Geo / analysis ────────────────────────────────────────────────────
    [ObservableProperty] private string  _country = string.Empty;
    [ObservableProperty] private string  _city    = string.Empty;
    [ObservableProperty] private string  _asn     = string.Empty;
    [ObservableProperty] private HopFlag _flag    = HopFlag.None;

    /// <summary>Rolling latency history for live chart (max 120 samples).</summary>
    public ObservableCollection<double> LatencyHistory { get; } = new();

    // ── Update API ────────────────────────────────────────────────────────
    public void RecordReply(string ip, double rttMs, Action<Action> uiDispatch)
    {
        lock (_lock)
        {
            Sent++;
            Received++;
            Last = rttMs;

            if (rttMs < Best) Best = rttMs;
            if (rttMs > Worst) Worst = rttMs;

            _rawRtts.Add(rttMs);
            Avg    = _rawRtts.Average();
            Jitter = Worst - Best;

            if (_rawRtts.Count > 1)
            {
                double mean = Avg;
                StdDev = Math.Sqrt(_rawRtts.Average(v => Math.Pow(v - mean, 2)));
            }

            Loss = (double)(Sent - Received) / Sent * 100.0;

            // Update chart history on UI thread
            double snapshot = rttMs;
            uiDispatch(() =>
            {
                if (LatencyHistory.Count >= 120)
                    LatencyHistory.RemoveAt(0);
                LatencyHistory.Add(snapshot);
            });
        }

        if (IpAddress == "???" || IpAddress != ip)
        {
            uiDispatch(() => IpAddress = ip);
        }
    }

    public void RecordTimeout(Action<Action> uiDispatch)
    {
        lock (_lock)
        {
            Sent++;
            Last = -1;
            Loss = (double)(Sent - Received) / Sent * 100.0;
        }
    }

    public void Reset(Action<Action> uiDispatch)
    {
        lock (_lock)
        {
            _rawRtts.Clear();
            Sent = Received = 0;
            Last = -1; Avg = Best = Worst = Jitter = StdDev = Loss = 0;
            Best = double.MaxValue;
            Flag = HopFlag.None;
        }
        uiDispatch(() => { HostName = "???"; IpAddress = "???"; LatencyHistory.Clear(); });
    }
}
