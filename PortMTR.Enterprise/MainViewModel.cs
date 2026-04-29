using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using PortMTR.Enterprise.Analysis;
using PortMTR.Enterprise.Capture;
using PortMTR.Enterprise.Engine;
using PortMTR.Enterprise.Models;
using PortMTR.Enterprise.Protocols;
using PortMTR.Enterprise.Reporting;
using PortMTR.Enterprise.Services;

namespace PortMTR.Enterprise.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const double IcmpDefaultIntervalSeconds = 1.0;
    private const double IcmpDefaultTimeoutSeconds = 3.0;
    private const int IcmpDefaultParallelHops = 4;
    private const double PortTraceDefaultIntervalSeconds = 0.75;
    private const double PortTraceDefaultTimeoutSeconds = 1.0;
    private const int PortTraceDefaultParallelHops = 1;

    // ── Services (injected / owned) ───────────────────────────────────────
    private readonly NpcapCaptureService _capture;
    private readonly DnsService          _dns;
    private readonly GeoIpService        _geo;
    private MTREngine?                   _engine;
    private bool                         _isStarting;

    // ── Config properties ─────────────────────────────────────────────────
    [ObservableProperty] private string _targetHost  = "google.com";
    [ObservableProperty] private string _protocol    = "ICMP";
    [ObservableProperty] private int    _port        = 80;
    [ObservableProperty] private int    _maxHops     = 30;
    [ObservableProperty] private double _interval    = IcmpDefaultIntervalSeconds;
    [ObservableProperty] private double _probeTimeout= IcmpDefaultTimeoutSeconds;
    [ObservableProperty] private int    _packetSize  = 52;
    [ObservableProperty] private int    _parallelHops= IcmpDefaultParallelHops;
    [ObservableProperty] private bool   _dnsEnabled  = true;

    [ObservableProperty] private bool   _portEnabled = false;
    [ObservableProperty] private bool   _useIpv6     = false;
    public string[] Protocols => new[] { "ICMP", "TCP", "UDP" };

    // ── State ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private string _statusText   = "Ready";
    [ObservableProperty] private string _resolvedIp   = "";
    [ObservableProperty] private int    _cycleCount;
    [ObservableProperty] private double _stabilityScore = 100;
    [ObservableProperty] private string _stabilityLabel  = "Excellent";
    [ObservableProperty] private string _elapsedTime = "";
    private DateTime _startTime;

    // ── Hop collection (UI thread) ────────────────────────────────────────
    public ObservableCollection<HopModel> Hops { get; } = new();

    // ── Selected hop & live chart ─────────────────────────────────────────
    [ObservableProperty] private HopModel? _selectedHop;
    [ObservableProperty] private ISeries[] _chartSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[]    _chartXAxes  = new[] { new Axis { IsVisible = false } };
    [ObservableProperty] private Axis[]    _chartYAxes  = new[]
    {
        new Axis
        {
            TextSize    = 10,
            LabelsPaint = new SolidColorPaint(SKColors.Gray),
            SeparatorsPaint = new SolidColorPaint(new SKColor(33, 38, 45))
        }
    };

    // ── Npcap device list ─────────────────────────────────────────────────
    public IReadOnlyList<string> CaptureDevices { get; }
    [ObservableProperty] private string? _selectedDevice;

    // ── Diagnosis text ────────────────────────────────────────────────────
    [ObservableProperty] private string _diagnosisText = "";

    public MainViewModel()
    {
        _capture = new NpcapCaptureService();
        _dns     = new DnsService();
        _geo     = new GeoIpService();

        CaptureDevices = NpcapCaptureService.ListDevices();
        _selectedDevice = CaptureDevices.FirstOrDefault();

        _geo.Initialise();
        UpdatePortEnabled(Protocol);
        ApplyRecommendedDefaults(Protocol);
    }

    // ── Protocol changed ──────────────────────────────────────────────────
    partial void OnProtocolChanged(string value)
    {
        UpdatePortEnabled(value);
        if (value == "TCP" && Port == 33434) Port = 80;
        if (value == "UDP" && Port == 80)    Port = 33434;
        ApplyRecommendedDefaults(value);
    }

    partial void OnDnsEnabledChanged(bool value) => _dns.Enabled = value;

    partial void OnSelectedHopChanged(HopModel? value) => UpdateChart(value);

    private void UpdatePortEnabled(string proto) => PortEnabled = proto != "ICMP";

    private void ApplyRecommendedDefaults(string proto)
    {
        if (string.Equals(proto, "ICMP", StringComparison.OrdinalIgnoreCase))
        {
            Interval = IcmpDefaultIntervalSeconds;
            ProbeTimeout = IcmpDefaultTimeoutSeconds;
            ParallelHops = IcmpDefaultParallelHops;
            return;
        }

        // TCP/UDP work best with a shorter cycle and a tighter timeout.
        // The engine already serializes these protocols, so surface that
        // recommendation in the UI rather than leaving a misleading value.
        Interval = PortTraceDefaultIntervalSeconds;
        ProbeTimeout = PortTraceDefaultTimeoutSeconds;
        ParallelHops = PortTraceDefaultParallelHops;
    }

    private void UpdateChart(HopModel? hop)
    {
        if (hop == null) { ChartSeries = Array.Empty<ISeries>(); return; }

        // Wrap the ObservableCollection as a LiveCharts observable values source
        var values = hop.LatencyHistory
                        .Select(v => new ObservableValue(v))
                        .ToList();

        ChartSeries = new ISeries[]
        {
            new LineSeries<ObservableValue>
            {
                Values        = new ObservableCollection<ObservableValue>(values),
                Fill          = new LinearGradientPaint(
                                    new SKColor(88, 166, 255, 80),
                                    new SKColor(88, 166, 255, 0),
                                    new SKPoint(0, 0), new SKPoint(0, 1)),
                Stroke        = new SolidColorPaint(new SKColor(88, 166, 255)) { StrokeThickness = 2 },
                GeometrySize  = 4,
                GeometryFill  = new SolidColorPaint(new SKColor(88, 166, 255)),
                GeometryStroke= null,
                Name          = $"Hop {hop.Ttl} — {hop.IpAddress}"
            }
        };

        // Live update: hook collection changed to push new values
        hop.LatencyHistory.CollectionChanged += (_, e) =>
        {
            if (SelectedHop != hop) return;
            if (e.NewItems == null) return;
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (ChartSeries.FirstOrDefault() is LineSeries<ObservableValue> ls &&
                    ls.Values is ObservableCollection<ObservableValue> col)
                {
                    foreach (var item in e.NewItems)
                        col.Add(new ObservableValue((double)item));
                    if (col.Count > 120)
                        col.RemoveAt(0);
                }
            });
        };
    }

    // ── Commands ──────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_isStarting || IsRunning)
            return;

        _isStarting = true;
        StartCommand.NotifyCanExecuteChanged();

        try
        {
            await DisposeEngineAsync();

            // Resolve host with IPv4 as the default stable path; IPv6 is opt-in.
            IPAddress? ip;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(TargetHost);
                AddressFamily preferredFamily = UseIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
                ip = addresses.FirstOrDefault(a => a.AddressFamily == preferredFamily);
            }
            catch { StatusText = $"Cannot resolve '{TargetHost}'"; return; }

            if (ip == null)
            {
                StatusText = UseIpv6 ? "No IPv6 address found" : "No IPv4 address found";
                return;
            }

            ResolvedIp = ip.ToString();

            // Build prober
            var proto = Enum.Parse<ProbeProtocol>(Protocol);
            IProber prober = proto switch
            {
                ProbeProtocol.TCP => BuildTcpProber(ip),
                ProbeProtocol.UDP => BuildUdpProber(ip),
                _ => new IcmpProber()
            };

            // Build engine
            Action<Action> dispatch = a => Application.Current?.Dispatcher.InvokeAsync(a);

            var engine = new MTREngine(ip, proto, Port, prober, _dns, _geo, dispatch)
            {
                MaxHops = MaxHops,
                Interval = TimeSpan.FromSeconds(Interval),
                ProbeTimeout = TimeSpan.FromSeconds(ProbeTimeout),
                PacketSize = PacketSize,
                ParallelHops = ParallelHops
            };
            _engine = engine;

            // Wire events
            engine.OnHopUpdated += hop => Application.Current?.Dispatcher.InvokeAsync(() => SyncHop(hop));
            engine.OnCycleComplete += n => Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CycleCount = n;
                StabilityScore = engine.StabilityScore;
                StabilityLabel = ScoreLabel(StabilityScore);
                ElapsedTime = $"{(DateTime.Now - _startTime):hh\\:mm\\:ss}";
                DiagnosisText = string.Join("\n", HopAnalyzer.Diagnose(Hops.ToList()));
            });
            engine.OnError += msg => Application.Current?.Dispatcher.InvokeAsync(
                () => StatusText = $"Error: {msg}");

            // Clear & pre-populate hop rows
            Hops.Clear();
            for (int i = 1; i <= MaxHops; i++)
                Hops.Add(engine.Hops[i - 1]);

            _startTime = DateTime.Now;
            IsRunning = true;
            StatusText = $"Probing {TargetHost} ({ResolvedIp}) via {Protocol}" +
                         (proto != ProbeProtocol.ICMP ? $":{Port}" : "") + " …";
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();

            engine.Start();
        }
        finally
        {
            _isStarting = false;
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_engine == null) return;
        StatusText = "Stopping …";
        await DisposeEngineAsync();
        IsRunning  = false;
        StatusText = "Stopped";
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Clear()
    {
        Hops.Clear();
        CycleCount  = 0;
        DiagnosisText = "";
        StatusText  = "Cleared";
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter          = "HTML Report|*.html",
            FileName        = $"PortMTR_{TargetHost}_{DateTime.Now:yyyyMMdd_HHmmss}.html",
            DefaultExt      = ".html"
        };
        if (dlg.ShowDialog() != true) return;

        string html = ReportEngine.ToHtml(
            TargetHost, ResolvedIp,
            Enum.Parse<ProbeProtocol>(Protocol), Port,
            Hops.ToList(), StabilityScore);

        await File.WriteAllTextAsync(dlg.FileName, html);
        StatusText = $"HTML report saved: {dlg.FileName}";
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter     = "JSON|*.json",
            FileName   = $"PortMTR_{TargetHost}_{DateTime.Now:yyyyMMdd_HHmmss}.json",
            DefaultExt = ".json"
        };
        if (dlg.ShowDialog() != true) return;

        string json = ReportEngine.ToJson(
            TargetHost, ResolvedIp,
            Enum.Parse<ProbeProtocol>(Protocol), Port,
            Hops.ToList(), StabilityScore);

        await File.WriteAllTextAsync(dlg.FileName, json);
        StatusText = $"JSON report saved: {dlg.FileName}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private bool CanStart() => !IsRunning && !_isStarting;
    private bool CanStop()  => IsRunning;

    private async Task DisposeEngineAsync()
    {
        if (_engine == null)
            return;

        await _engine.StopAsync();
        _engine.Dispose();
        _engine = null;
    }

    private void SyncHop(HopModel hop)
    {
        // HopModel is already in Hops list; ObservableProperty updates propagate automatically.
        // Select first responding hop if none selected.
        if (SelectedHop == null && hop.Received > 0)
            SelectedHop = hop;
    }

    private IProber BuildUdpProber(IPAddress target)
    {
        string captureFilter = target.AddressFamily == AddressFamily.InterNetworkV6
            ? "icmp6"
            : "(icmp and (icmp[icmptype] = icmp-timxceed or icmp[icmptype] = icmp-unreach))";

        _capture.Initialise(SelectedDevice, captureFilter, target);
        if (!string.IsNullOrWhiteSpace(_capture.ActiveDeviceLabel))
            SelectedDevice = _capture.ActiveDeviceLabel;
        if (!_capture.IsAvailable)
        {
            StatusText = "Npcap not available — UDP probes may be unreliable. Falling back to raw ICMP receiver.";
            return new IcmpProber(); // graceful fallback
        }
        return new UdpProber(_capture.Device!);
    }

    private IProber BuildTcpProber(IPAddress target)
    {
        string captureFilter = target.AddressFamily == AddressFamily.InterNetworkV6
            ? "icmp6 or tcp"
            : "(icmp[icmptype] = 11 or icmp[icmptype] = 3) or (tcp and src host " + target + ")";

        _capture.Initialise(SelectedDevice, captureFilter, target);
        if (!string.IsNullOrWhiteSpace(_capture.ActiveDeviceLabel))
            SelectedDevice = _capture.ActiveDeviceLabel;
        if (_capture.IsAvailable)
            return new TcpProber(_capture.Device!);

        StatusText = "⚠ Npcap not available — TCP traceroute requires Npcap for reliable hop detection. Install Npcap from npcap.com";
        return new TcpProber();
    }

    private static string ScoreLabel(double s) =>
        s >= 95 ? "Excellent" : s >= 80 ? "Good" : s >= 60 ? "Fair" : s >= 40 ? "Poor" : "Critical";
}
