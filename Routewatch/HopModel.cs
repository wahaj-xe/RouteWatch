using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using RouteWatch.Models;

namespace RouteWatch.Models;

/// <summary>
/// Thread-safe per-hop statistics model.
/// Property mutations must be dispatched to the UI thread when WPF is bound.
/// </summary>
public sealed partial class HopModel : ObservableObject
{
    private readonly object _lock = new();
    private int _rttSampleCount;
    private double _meanRtt;
    private double _sumSquaredRttDeviations;
    private double _rfcJitter;

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

    public double PathLoss => Flag == HopFlag.RateLimiting ? 0 : Loss;

    // ── Geo / analysis ────────────────────────────────────────────────────
    [ObservableProperty] private string  _country = string.Empty;
    [ObservableProperty] private string  _city    = string.Empty;
    [ObservableProperty] private string  _asn     = "ASN: —";
    [ObservableProperty] private HopFlag _flag    = HopFlag.None;

    partial void OnLossChanged(double value) => OnPropertyChanged(nameof(PathLoss));
    partial void OnFlagChanged(HopFlag value) => OnPropertyChanged(nameof(PathLoss));

    /// <summary>Rolling latency history for live chart (max 120 samples).</summary>
    public ObservableCollection<double> LatencyHistory { get; } = new();

    // ── Update API ────────────────────────────────────────────────────────
    public void RecordReply(string ip, double rttMs, Action<Action> uiDispatch)
    {
        double roundedRtt = Math.Round(rttMs, 2);
        lock (_lock)
        {
            Sent++;
            Received++;

            double prevLast = Last;
            Last = roundedRtt;

            if (roundedRtt < Best) Best = roundedRtt;
            if (roundedRtt > Worst) Worst = roundedRtt;

            _rttSampleCount++;
            double delta = roundedRtt - _meanRtt;
            _meanRtt += delta / _rttSampleCount;
            _sumSquaredRttDeviations += delta * (roundedRtt - _meanRtt);
            Avg = Math.Round(_meanRtt, 2);

            // RFC 3550 interarrival jitter algorithm (as used by mtr and VOIP metrics)
            if (prevLast >= 0)
            {
                double d = Math.Abs(roundedRtt - prevLast);
                _rfcJitter += (d - _rfcJitter) / 16.0;
                Jitter = Math.Round(_rfcJitter, 2);
            }
            else
            {
                _rfcJitter = 0;
                Jitter = 0;
            }

            // Sample standard deviation (Bessel's correction N - 1)
            if (_rttSampleCount > 1)
                StdDev = Math.Round(Math.Sqrt(_sumSquaredRttDeviations / (_rttSampleCount - 1)), 2);
            else
                StdDev = 0;

            Loss = Math.Round((double)(Sent - Received) / Sent * 100.0, 1);

            // Update chart history on UI thread
            double snapshot = roundedRtt;
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
            Loss = Math.Round((double)(Sent - Received) / Sent * 100.0, 1);
        }
    }

    public void Reset(Action<Action> uiDispatch)
    {
        lock (_lock)
        {
            _rttSampleCount = 0;
            _meanRtt = 0;
            _sumSquaredRttDeviations = 0;
            _rfcJitter = 0;
            Sent = Received = 0;
            Last = -1; Avg = Best = Worst = Jitter = StdDev = Loss = 0;
            Best = double.MaxValue;
            Flag = HopFlag.None;
        }
        uiDispatch(() =>
        {
            HostName = "???";
            IpAddress = "???";
            Asn = "ASN: —";
            LatencyHistory.Clear();
        });
    }
}
