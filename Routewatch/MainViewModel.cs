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
using RouteWatch.Analysis;
using RouteWatch.Capture;
using RouteWatch.Engine;
using RouteWatch.Models;
using RouteWatch.Protocols;
using RouteWatch.Reporting;
using RouteWatch.Services;

namespace RouteWatch.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // ── Gold Standard / Best Spot Protocol Defaults ───────────────────────
    private const double IcmpDefaultIntervalSeconds = 1.0;
    private const double IcmpDefaultTimeoutSeconds  = 2.0;
    private const int    IcmpDefaultParallelHops    = 1;  // Sequential with pacing = 0% loss parity with WinMTR
    private const int    IcmpDefaultPacketSize      = 64; // WinMTR default ping payload size

    private const double TcpDefaultIntervalSeconds  = 1.0;
    private const double TcpDefaultTimeoutSeconds   = 1.5;
    private const int    TcpDefaultParallelHops     = 1;
    private const int    TcpDefaultPacketSize       = 52;

    private const double UdpDefaultIntervalSeconds  = 1.0;
    private const double UdpDefaultTimeoutSeconds   = 2.0;
    private const int    UdpDefaultParallelHops     = 1;
    private const int    UdpDefaultPacketSize       = 52;

    // ── Services (injected / owned) ───────────────────────────────────────
    private readonly NpcapCaptureService    _capture;
    private readonly DnsService             _dns;
    private readonly GeoIpService           _geo;
    private readonly ResourceMonitorService _resMon;
    private MTREngine?                      _engine;
    private bool                            _isStarting;

    // ── Config properties ─────────────────────────────────────────────────
    [ObservableProperty] private string _targetHost   = "google.com";
    [ObservableProperty] private string _protocol     = "ICMP";
    [ObservableProperty] private int    _port         = 80;
    [ObservableProperty] private int    _maxHops      = 30;
    [ObservableProperty] private double _interval     = IcmpDefaultIntervalSeconds;
    [ObservableProperty] private double _probeTimeout = IcmpDefaultTimeoutSeconds;
    [ObservableProperty] private int    _packetSize   = IcmpDefaultPacketSize;
    [ObservableProperty] private int    _parallelHops = IcmpDefaultParallelHops;
    [ObservableProperty] private bool   _dnsEnabled   = true;

    // ── Options / Settings Dialog (WinMTR Parity) ──────────────────────────
    [ObservableProperty] private bool _isOptionsModalOpen;

    [RelayCommand]
    private void OpenOptionsModal() => IsOptionsModalOpen = true;

    [RelayCommand]
    private void CloseOptionsModal() => IsOptionsModalOpen = false;

    [RelayCommand]
    private void ResetOptionsDefaults()
    {
        ApplyRecommendedDefaults(Protocol);
        DnsEnabled = true;
        UseIpv6 = null; // Auto prefer IPv6
        MaxHops = 30;
    }

    [ObservableProperty] private bool   _portEnabled = false;
    [ObservableProperty] private bool?  _useIpv6     = null; // null = dashed (Auto / Prefer IPv6), true = tick (Force IPv6), false = unticked (Force IPv4)
    public string[] Protocols => new[] { "ICMP", "TCP", "UDP" };

    public string Ipv6ToolTip => UseIpv6 switch
    {
        true  => "IPv6 Only [✓]: Force all traceroute traffic over IPv6 (Destination must have AAAA record)",
        false => "IPv4 Only [ ]: Force all traceroute traffic over IPv4 (A record only)",
        _     => "IPv6 Auto [-]: Prefer IPv6 if destination has IPv6, otherwise fallback to IPv4 (Default)"
    };

    public string Ipv6Label => UseIpv6 switch
    {
        true  => "IPv6 (Only)",
        false => "IPv6 (Off)",
        _     => "IPv6"
    };

    partial void OnUseIpv6Changed(bool? value)
    {
        OnPropertyChanged(nameof(Ipv6ToolTip));
        OnPropertyChanged(nameof(Ipv6Label));
        if (IsRunning)
        {
            RestartCommand.Execute(null);
        }
    }

    // ── Themes & Skins ────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isNightMode  = true;
    [ObservableProperty] private string _selectedSkin = "Enterprise";
    public string[] Skins => new[] { "Enterprise", "Cyberpunk", "Counter-Strike" };
    public string NightModeLabel => IsNightMode ? "🌙 Night" : "☀️ Day";

    partial void OnIsNightModeChanged(bool value)
    {
        OnPropertyChanged(nameof(NightModeLabel));
        UpdateTheme();
    }

    partial void OnSelectedSkinChanged(string value) => UpdateTheme();

    [RelayCommand]
    private void ToggleDayNight()
    {
        IsNightMode = !IsNightMode;
    }

    private void UpdateTheme()
    {
        var mode = IsNightMode ? AppThemeMode.Night : AppThemeMode.Day;
        var skin = SelectedSkin switch
        {
            "Counter-Strike" => AppThemeSkin.CounterStrike,
            "Enterprise"     => AppThemeSkin.Enterprise,
            _                => AppThemeSkin.Cyberpunk
        };
        ThemeService.ApplyTheme(mode, skin);

        ChartYAxes = new[]
        {
            new Axis
            {
                TextSize        = 10,
                LabelsPaint     = new SolidColorPaint(ThemeService.CurrentTextSkColor),
                SeparatorsPaint = new SolidColorPaint(ThemeService.CurrentGridSkColor)
            }
        };

        if (SelectedHop != null)
        {
            UpdateChart(SelectedHop);
        }
    }

    // ── State ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private string _statusText   = "Ready";
    [ObservableProperty] private string _resolvedIp   = "";
    [ObservableProperty] private int    _cycleCount;
    [ObservableProperty] private double _stabilityScore = 100;
    [ObservableProperty] private string _stabilityLabel  = "Excellent";
    [ObservableProperty] private string _elapsedTime = "";
    [ObservableProperty] private string _geoIpStatus = "GeoIP: loading";
    private DateTime _startTime;

    // ── Executive KPI Summary ──────────────────────────────────────────
    [ObservableProperty] private string _targetStatusText = "IDLE READY";
    [ObservableProperty] private string _destRttText       = "—";
    [ObservableProperty] private string _destLossText      = "0.0%";
    [ObservableProperty] private string _destBestWorstText = "Best: — · Worst: —";
    [ObservableProperty] private string _routeJitterText   = "0.0 ms";
    [ObservableProperty] private string _bottleneckText    = "No active trace";
    [ObservableProperty] private string _healthBadgeText   = "Ready";

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

    // ── Right Inspector Tab Switching ─────────────────────────────────────
    [ObservableProperty] private int _selectedInspectorTab = 0; // 0: History, 1: Waterfall, 2: Hop Intel, 3: Diagnostics

    [RelayCommand]
    private void SelectInspectorTab(string tabIndexStr)
    {
        if (int.TryParse(tabIndexStr, out int idx))
            SelectedInspectorTab = idx;
    }

    // ── Primary View Navigation (0 = Traceroute MTR, 1 = Resource Monitor) ─
    [ObservableProperty] private int _currentViewMode = 0;

    [RelayCommand] private void SwitchToMtrView() => CurrentViewMode = 0;
    [RelayCommand] private void SwitchToResourceMonitorView() => CurrentViewMode = 1;

    // ── Resource Monitor Collections & State ─────────────────────────────
    public ObservableCollection<ProcessNetworkItem> ActiveProcesses { get; } = new();
    public ObservableCollection<NetworkConnectionItem> FilteredConnections { get; } = new();
    private List<ProcessNetworkItem> _allProcesses = new();
    private List<NetworkConnectionItem> _allConnections = new();

    [ObservableProperty] private ProcessNetworkItem? _selectedProcess;
    [ObservableProperty] private string _processSearchText = "";
    [ObservableProperty] private string _connectionSearchText = "";
    [ObservableProperty] private bool _filterBySelectedProcess = true;
    [ObservableProperty] private bool _hideLoopback = true;
    private long _lastSendBps;
    private long _lastRcvBps;

    [ObservableProperty] private bool _useBitsRate = true; // true = Mb and Kb (default), false = MB/KB
    public string SpeedUnitLabel => UseBitsRate ? "Mb / Kb" : "MB / KB";

    [RelayCommand]
    private void ToggleSpeedUnit()
    {
        UseBitsRate = !UseBitsRate;
    }

    partial void OnUseBitsRateChanged(bool value)
    {
        ProcessNetworkItem.UseBitsRate = value;
        OnPropertyChanged(nameof(SpeedUnitLabel));

        TotalSendThroughputText = ProcessNetworkItem.FormatSpeed(_lastSendBps);
        TotalRcvThroughputText = ProcessNetworkItem.FormatSpeed(_lastRcvBps);
        TotalNetworkThroughputText = ProcessNetworkItem.FormatSpeed(_lastSendBps + _lastRcvBps);

        foreach (var p in ActiveProcesses)
            p.NotifySpeedChanged();
        foreach (var c in FilteredConnections)
            c.NotifySpeedChanged();
    }

    [ObservableProperty] private string _totalNetworkThroughputText = "0 Kbps";
    [ObservableProperty] private string _totalSendThroughputText = "0 Kbps";
    [ObservableProperty] private string _totalRcvThroughputText = "0 Kbps";
    [ObservableProperty] private string _resMonStatusText = "Active Live Network Telemetry";

    // Sorting in Resource Monitor
    [ObservableProperty] private NetworkSortMetric _sortMetric = NetworkSortMetric.Total;
    [ObservableProperty] private string _sortButtonLabel = "Sort: Total ▼";

    [RelayCommand]
    private void CycleSortMode()
    {
        SortMetric = SortMetric switch
        {
            NetworkSortMetric.Total => NetworkSortMetric.Receive,
            NetworkSortMetric.Receive => NetworkSortMetric.Send,
            NetworkSortMetric.Send => NetworkSortMetric.Total,
            _ => NetworkSortMetric.Total
        };
        _resMon.SortMetric = SortMetric;
        UpdateSortButtonLabel();
        ApplyProcessFilter();
        ApplyConnectionFilter();
    }

    [RelayCommand]
    private void SetSortMetric(string metric)
    {
        if (Enum.TryParse<NetworkSortMetric>(metric, true, out var m))
        {
            SortMetric = m;
            _resMon.SortMetric = SortMetric;
            UpdateSortButtonLabel();
            ApplyProcessFilter();
            ApplyConnectionFilter();
        }
    }

    private void UpdateSortButtonLabel()
    {
        SortButtonLabel = SortMetric switch
        {
            NetworkSortMetric.Receive => "Sort: RX (Download) ▼",
            NetworkSortMetric.Send => "Sort: TX (Upload) ▼",
            _ => "Sort: Total ▼"
        };
    }

    // Target Error Popup & 1-Liner Suggestion Modal
    [ObservableProperty] private bool _isErrorModalOpen;
    [ObservableProperty] private string _errorModalTitle = "Target Resolution Error";
    [ObservableProperty] private string _errorModalMessage = "";
    [ObservableProperty] private string _errorModalSolution = "";
    [ObservableProperty] private string? _errorModalSuggestedTarget;

    [RelayCommand]
    private void CloseErrorModal()
    {
        IsErrorModalOpen = false;
    }

    [RelayCommand]
    private async Task ApplySuggestedTargetAndStartAsync()
    {
        if (!string.IsNullOrEmpty(ErrorModalSuggestedTarget))
        {
            TargetHost = ErrorModalSuggestedTarget;
            IsErrorModalOpen = false;
            await StartAsync();
        }
    }

    private void ShowTargetResolutionError(string target, Exception? ex)
    {
        var (msg, sol, fix) = SuggestTargetSolution(target, ex, UseIpv6);
        StatusText = msg;
        ErrorModalTitle = "Target Resolution Error";
        ErrorModalMessage = msg;
        ErrorModalSolution = sol;
        ErrorModalSuggestedTarget = fix;
        IsErrorModalOpen = true;
    }

    partial void OnSelectedProcessChanged(ProcessNetworkItem? value) => ApplyConnectionFilter();
    partial void OnProcessSearchTextChanged(string value) => ApplyProcessFilter();
    partial void OnConnectionSearchTextChanged(string value) => ApplyConnectionFilter();
    partial void OnFilterBySelectedProcessChanged(bool value) => ApplyConnectionFilter();
    partial void OnHideLoopbackChanged(bool value) => ApplyConnectionFilter();

    [RelayCommand]
    private async Task TraceConnectionAsync(NetworkConnectionItem? conn)
    {
        if (conn == null || string.IsNullOrWhiteSpace(conn.RemoteAddress) || conn.RemoteAddress == "*")
        {
            StatusText = "Select an active connection with a valid remote address.";
            return;
        }

        TargetHost = conn.RemoteAddress;
        Port = conn.RemotePort > 0 ? conn.RemotePort : 80;
        Protocol = string.Equals(conn.Protocol, "UDP", StringComparison.OrdinalIgnoreCase) ? "UDP" : "TCP";

        // Switch to MTR view and start trace immediately!
        CurrentViewMode = 0;
        StatusText = $"Tracing endpoint {conn.ImageName} -> {TargetHost}:{Port} ({Protocol}) …";
        await RestartAsync();
    }

    [RelayCommand]
    private void SetTargetFromConnection(NetworkConnectionItem? conn)
    {
        if (conn == null || string.IsNullOrWhiteSpace(conn.RemoteAddress) || conn.RemoteAddress == "*")
            return;

        TargetHost = conn.RemoteAddress;
        Port = conn.RemotePort > 0 ? conn.RemotePort : 80;
        Protocol = string.Equals(conn.Protocol, "UDP", StringComparison.OrdinalIgnoreCase) ? "UDP" : "TCP";
        CurrentViewMode = 0;
        StatusText = $"Target set to {conn.ImageName} ({TargetHost}:{Port} via {Protocol}). Ready to trace.";
    }

    [RelayCommand]
    private void ClearSelectedProcess()
    {
        SelectedProcess = null;
        ApplyConnectionFilter();
    }

    // ── Diagnosis text ────────────────────────────────────────────────────
    [ObservableProperty] private string _diagnosisText = "";

    public MainViewModel()
    {
        _capture = new NpcapCaptureService();
        _dns     = new DnsService();
        _geo     = new GeoIpService();
        _resMon  = new ResourceMonitorService();

        CaptureDevices = NpcapCaptureService.ListDevices();
        _selectedDevice = CaptureDevices.FirstOrDefault();

        _geo.Initialise();
        GeoIpStatus = _geo.StatusText;
        UpdatePortEnabled(Protocol);
        ApplyRecommendedDefaults(Protocol);
        UpdateTheme();
        UpdateKpiMetrics();

        _resMon.OnDataUpdated += (procs, conns, sendBps, rcvBps) =>
            Application.Current?.Dispatcher.InvokeAsync(() => UpdateResourceMonitorData(procs, conns, sendBps, rcvBps));
        _resMon.Start();
    }

    private void UpdateResourceMonitorData(List<ProcessNetworkItem> procs, List<NetworkConnectionItem> conns, long sendBps, long rcvBps)
    {
        _lastSendBps = sendBps;
        _lastRcvBps = rcvBps;

        TotalSendThroughputText = ProcessNetworkItem.FormatSpeed(sendBps);
        TotalRcvThroughputText = ProcessNetworkItem.FormatSpeed(rcvBps);
        TotalNetworkThroughputText = ProcessNetworkItem.FormatSpeed(sendBps + rcvBps);

        _allProcesses = procs;
        _allConnections = conns;

        ApplyProcessFilter();
        ApplyConnectionFilter();
    }

    private void ApplyProcessFilter()
    {
        var query = _allProcesses.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(ProcessSearchText))
        {
            query = query.Where(p =>
                p.ImageName.Contains(ProcessSearchText, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(ProcessSearchText));
        }

        query = SortMetric switch
        {
            NetworkSortMetric.Send => query.OrderByDescending(p => p.SendBytesPerSec).ThenByDescending(p => p.TotalBytesPerSec),
            NetworkSortMetric.Receive => query.OrderByDescending(p => p.ReceiveBytesPerSec).ThenByDescending(p => p.TotalBytesPerSec),
            _ => query.OrderByDescending(p => p.TotalBytesPerSec).ThenByDescending(p => p.ActiveConnectionsCount)
        };

        var newItems = query.ToList();
        int prevSelectedPid = SelectedProcess?.Pid ?? -1;

        var existingMap = new Dictionary<int, ProcessNetworkItem>();
        foreach (var p in ActiveProcesses)
        {
            existingMap[p.Pid] = p;
        }

        var targetPids = new HashSet<int>(newItems.Select(p => p.Pid));

        for (int i = ActiveProcesses.Count - 1; i >= 0; i--)
        {
            if (!targetPids.Contains(ActiveProcesses[i].Pid))
                ActiveProcesses.RemoveAt(i);
        }

        for (int i = 0; i < newItems.Count; i++)
        {
            var item = newItems[i];
            if (existingMap.TryGetValue(item.Pid, out var existing))
            {
                existing.ImageName = item.ImageName;
                existing.SendBytesPerSec = item.SendBytesPerSec;
                existing.ReceiveBytesPerSec = item.ReceiveBytesPerSec;
                existing.TotalBytesPerSec = item.TotalBytesPerSec;
                existing.ActiveConnectionsCount = item.ActiveConnectionsCount;

                int oldIndex = ActiveProcesses.IndexOf(existing);
                if (oldIndex != i && oldIndex >= 0 && i < ActiveProcesses.Count)
                {
                    ActiveProcesses.Move(oldIndex, i);
                }
            }
            else
            {
                if (i <= ActiveProcesses.Count)
                    ActiveProcesses.Insert(i, item);
                else
                    ActiveProcesses.Add(item);
            }
        }

        if (prevSelectedPid != -1 && (SelectedProcess == null || SelectedProcess.Pid != prevSelectedPid))
        {
            SelectedProcess = ActiveProcesses.FirstOrDefault(p => p.Pid == prevSelectedPid);
        }
    }

    private void ApplyConnectionFilter()
    {
        var query = _allConnections.AsEnumerable();

        if (HideLoopback)
        {
            query = query.Where(c =>
                !c.RemoteAddress.StartsWith("127.") &&
                !c.RemoteAddress.StartsWith("::1") &&
                !c.RemoteAddress.StartsWith("0.0.0.0") &&
                !c.RemoteAddress.StartsWith("::") &&
                c.RemoteAddress != "*");
        }

        if (FilterBySelectedProcess && SelectedProcess != null)
        {
            query = query.Where(c => c.Pid == SelectedProcess.Pid);
        }

        if (!string.IsNullOrWhiteSpace(ConnectionSearchText))
        {
            query = query.Where(c =>
                c.RemoteAddress.Contains(ConnectionSearchText, StringComparison.OrdinalIgnoreCase) ||
                c.RemoteHostName.Contains(ConnectionSearchText, StringComparison.OrdinalIgnoreCase) ||
                c.RemotePort.ToString().Contains(ConnectionSearchText) ||
                c.ImageName.Contains(ConnectionSearchText, StringComparison.OrdinalIgnoreCase));
        }

        query = SortMetric switch
        {
            NetworkSortMetric.Send => query.OrderByDescending(c => c.SendBytesPerSec).ThenByDescending(c => c.TotalBytesPerSec),
            NetworkSortMetric.Receive => query.OrderByDescending(c => c.ReceiveBytesPerSec).ThenByDescending(c => c.TotalBytesPerSec),
            _ => query.OrderByDescending(c => c.TotalBytesPerSec).ThenBy(c => c.RemotePort)
        };

        var list = query.Take(250).ToList();

        string ConnKey(NetworkConnectionItem c) => $"{c.Pid}:{c.Protocol}:{c.LocalPort}:{c.RemoteAddress}:{c.RemotePort}";
        var existingMap = new Dictionary<string, NetworkConnectionItem>();
        foreach (var c in FilteredConnections)
        {
            existingMap[ConnKey(c)] = c;
        }

        var targetKeys = new HashSet<string>(list.Select(ConnKey));

        for (int i = FilteredConnections.Count - 1; i >= 0; i--)
        {
            if (!targetKeys.Contains(ConnKey(FilteredConnections[i])))
                FilteredConnections.RemoveAt(i);
        }

        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            var key = ConnKey(item);
            if (existingMap.TryGetValue(key, out var existing))
            {
                existing.ImageName = item.ImageName;
                existing.RemoteHostName = item.RemoteHostName;
                existing.SendBytesPerSec = item.SendBytesPerSec;
                existing.ReceiveBytesPerSec = item.ReceiveBytesPerSec;
                existing.TotalBytesPerSec = item.TotalBytesPerSec;
                existing.State = item.State;

                int oldIndex = FilteredConnections.IndexOf(existing);
                if (oldIndex != i && oldIndex >= 0 && i < FilteredConnections.Count)
                {
                    FilteredConnections.Move(oldIndex, i);
                }
            }
            else
            {
                if (i <= FilteredConnections.Count)
                    FilteredConnections.Insert(i, item);
                else
                    FilteredConnections.Add(item);
            }
        }
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
            PacketSize = IcmpDefaultPacketSize;
            return;
        }

        if (string.Equals(proto, "TCP", StringComparison.OrdinalIgnoreCase))
        {
            Interval = TcpDefaultIntervalSeconds;
            ProbeTimeout = TcpDefaultTimeoutSeconds;
            ParallelHops = TcpDefaultParallelHops;
            PacketSize = TcpDefaultPacketSize;
            return;
        }

        Interval = UdpDefaultIntervalSeconds;
        ProbeTimeout = UdpDefaultTimeoutSeconds;
        ParallelHops = UdpDefaultParallelHops;
        PacketSize = UdpDefaultPacketSize;
    }

    private void UpdateChart(HopModel? hop)
    {
        // Native LatencyGraphControl binds directly to SelectedHop.LatencyHistory.
    }

    private void UpdateKpiMetrics()
    {
        if (!IsRunning && Hops.Count == 0)
        {
            TargetStatusText  = "IDLE READY";
            DestRttText       = "—";
            DestLossText      = "0.0%";
            DestBestWorstText = "Best: — · Worst: —";
            RouteJitterText   = "0.0 ms";
            BottleneckText    = "No active trace";
            HealthBadgeText   = "Ready";
            return;
        }

        TargetStatusText = IsRunning ? "● PROBING" : "⏹ STOPPED";

        int destHop = _engine?.DestinationHop ?? 0;
        var dest = destHop > 0 ? Hops.FirstOrDefault(h => h.Ttl == destHop) : Hops.LastOrDefault(h => h.Received > 0);

        if (dest != null && dest.Received > 0)
        {
            DestRttText  = $"{dest.Last:F1} ms";
            DestLossText = $"{dest.PathLoss:F1}%";
            string bestStr = dest.Best >= double.MaxValue - 1 ? "—" : $"{dest.Best:F1}ms";
            string worstStr = dest.Worst <= 0 ? "—" : $"{dest.Worst:F1}ms";
            DestBestWorstText = $"Best: {bestStr} · Worst: {worstStr}";
            RouteJitterText   = $"{dest.Jitter:F1} ms";
        }

        var activeHops = Hops.Where(h => h.Sent > 0).ToList();
        var worstLossHop = activeHops.OrderByDescending(h => h.PathLoss).FirstOrDefault();
        if (worstLossHop != null && worstLossHop.PathLoss > 3.0)
        {
            BottleneckText = $"Hop {worstLossHop.Ttl} ({worstLossHop.IpAddress}) · {worstLossHop.PathLoss:F1}% path loss";
        }
        else
        {
            var worstRttHop = activeHops.OrderByDescending(h => h.Avg).FirstOrDefault();
            if (worstRttHop != null && worstRttHop.Avg > 150)
            {
                BottleneckText = $"Hop {worstRttHop.Ttl} ({worstRttHop.IpAddress}) · {worstRttHop.Avg:F1}ms latency";
            }
            else
            {
                BottleneckText = "✓ Clean route (0.0% loss)";
            }
        }

        HealthBadgeText = StabilityLabel;
    }

    // ── Commands ──────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_isStarting || IsRunning)
            return;

        if (!ValidateInputs())
            return;

        _isStarting = true;
        StartCommand.NotifyCanExecuteChanged();

        try
        {
            await DisposeEngineAsync();

            // Resolve host based on tri-state IPv6 toggle:
            // null  = Auto / Prefer IPv6 (if destination has IPv6, use it by default; else fallback to IPv4)
            // true  = Force IPv6 only (AAAA record only)
            // false = Force IPv4 only (A record only)
            IPAddress? ip = null;
            try
            {
                if (IPAddress.TryParse(TargetHost, out var literalIp))
                {
                    ip = literalIp;
                    if (UseIpv6 == true && literalIp.AddressFamily != AddressFamily.InterNetworkV6)
                    {
                        ShowTargetResolutionError(TargetHost, new InvalidOperationException("Target is an IPv4 literal address, but IPv6 Only mode [✓] is active."));
                        return;
                    }
                    if (UseIpv6 == false && literalIp.AddressFamily != AddressFamily.InterNetwork)
                    {
                        ShowTargetResolutionError(TargetHost, new InvalidOperationException("Target is an IPv6 literal address, but IPv4 Only mode [ ] is active."));
                        return;
                    }
                }
                else
                {
                    var addresses = await Dns.GetHostAddressesAsync(TargetHost);

                    if (UseIpv6 == true)
                    {
                        // Forced IPv6 only
                        ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
                        if (ip == null)
                        {
                            ShowTargetResolutionError(TargetHost, new InvalidOperationException($"No IPv6 address found for '{TargetHost}' (IPv6 Only mode)."));
                            return;
                        }
                    }
                    else if (UseIpv6 == false)
                    {
                        // Forced IPv4 only
                        ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                        if (ip == null)
                        {
                            ShowTargetResolutionError(TargetHost, new InvalidOperationException($"No IPv4 address found for '{TargetHost}' (IPv4 Only mode)."));
                            return;
                        }
                    }
                    else
                    {
                        // Indeterminate (null) = Auto / Prefer IPv6:
                        // If destination has an IPv6 address, use it by default! Otherwise fallback to IPv4!
                        var ipv6 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
                        var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                        ip = ipv6 ?? ipv4;
                        if (ip == null)
                        {
                            ShowTargetResolutionError(TargetHost, new SocketException((int)SocketError.HostNotFound));
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShowTargetResolutionError(TargetHost, ex);
                return;
            }

            ResolvedIp = ip.ToString();

            // Build prober
            var proto = Enum.Parse<ProbeProtocol>(Protocol);
            IProber prober = proto switch
            {
                ProbeProtocol.TCP => BuildTcpProber(ip),
                ProbeProtocol.UDP => BuildUdpProber(ip),
                _ => new IcmpProber(PacketSize)
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
                TrimDisplayedHops(engine.DestinationHop);
                DiagnosisText = string.Join("\n", HopAnalyzer.Diagnose(Hops.ToList()));
                UpdateKpiMetrics();
            });
            engine.OnDestinationReached += ttl => Application.Current?.Dispatcher.InvokeAsync(
                () => TrimDisplayedHops(ttl));
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
        UpdateKpiMetrics();
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (IsRunning)
        {
            await StopAsync();
        }
        Clear();
        await StartAsync();
    }

    [RelayCommand]
    private void Clear()
    {
        Hops.Clear();
        CycleCount  = 0;
        DiagnosisText = "";
        StatusText  = "Cleared";
        UpdateKpiMetrics();
    }

    [RelayCommand]
    private void CopyResolvedIp()
    {
        if (string.IsNullOrWhiteSpace(ResolvedIp))
        {
            StatusText = "No resolved IP available.";
            return;
        }

        try
        {
            Clipboard.SetDataObject(ResolvedIp, true);
            StatusText = $"✓ Copied IP {ResolvedIp} to clipboard!";
        }
        catch (Exception ex)
        {
            StatusText = $"Clipboard error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyReportText()
    {
        if (Hops.Count == 0)
        {
            StatusText = "No hops to copy. Run a trace first.";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"RouteWatch — Network Diagnostic Report");
        sb.AppendLine($"Target: {TargetHost} ({ResolvedIp}) | Protocol: {Protocol} | Cycles: {CycleCount}");
        sb.AppendLine($"Stability: {StabilityScore:F0}/100 ({StabilityLabel}) | Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 98));
        sb.AppendLine(string.Format("{0,3} | {1,-16} | {2,-28} | {3,6} | {4,6} | {5,4} | {6,4} | {7,7} | {8,7} | {9,7}",
            "Hop", "IP Address", "Hostname", "Path%", "Reply%", "Snt", "Rcv", "Last", "Avg", "Wrst"));
        sb.AppendLine(new string('-', 98));

        foreach (var h in Hops)
        {
            string last = h.Last < 0 ? "—" : $"{h.Last:F1}";
            string worst = h.Worst <= 0 ? "—" : $"{h.Worst:F1}";
            string avg = h.Avg <= 0 ? "—" : $"{h.Avg:F1}";
            string host = string.IsNullOrWhiteSpace(h.HostName) ? "???" : (h.HostName.Length > 28 ? h.HostName[..25] + "..." : h.HostName);

            sb.AppendLine(string.Format("{0,3} | {1,-16} | {2,-28} | {3,5:F1}% | {4,5:F1}% | {5,4} | {6,4} | {7,7} | {8,7} | {9,7}",
                h.Ttl, h.IpAddress, host, h.PathLoss, h.Loss, h.Sent, h.Received, last, avg, worst));
        }

        try
        {
            Clipboard.SetDataObject(sb.ToString(), true);
            StatusText = "✓ Complete MTR report copied to clipboard!";
        }
        catch (Exception ex)
        {
            StatusText = $"Clipboard error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopySelectedHopIp()
    {
        if (SelectedHop == null || SelectedHop.IpAddress == "???")
        {
            StatusText = "No valid hop IP selected.";
            return;
        }
        try
        {
            Clipboard.SetDataObject(SelectedHop.IpAddress, true);
            StatusText = $"✓ Copied IP {SelectedHop.IpAddress} to clipboard!";
        }
        catch (Exception ex)
        {
            StatusText = $"Clipboard error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopySelectedHopHost()
    {
        if (SelectedHop == null || string.IsNullOrWhiteSpace(SelectedHop.HostName) || SelectedHop.HostName == "???")
        {
            StatusText = "No resolved hostname for this hop.";
            return;
        }
        try
        {
            Clipboard.SetDataObject(SelectedHop.HostName, true);
            StatusText = $"✓ Copied hostname {SelectedHop.HostName} to clipboard!";
        }
        catch (Exception ex)
        {
            StatusText = $"Clipboard error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyDiagnosis()
    {
        if (string.IsNullOrWhiteSpace(DiagnosisText))
        {
            StatusText = "No diagnostic summary to copy.";
            return;
        }
        try
        {
            Clipboard.SetDataObject(DiagnosisText, true);
            StatusText = "✓ Path diagnosis copied to clipboard!";
        }
        catch (Exception ex)
        {
            StatusText = $"Clipboard error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter          = "HTML Report|*.html",
            FileName        = $"RouteWatch_{SafeFileNamePart(TargetHost)}_{DateTime.Now:yyyyMMdd_HHmmss}.html",
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
            FileName   = $"RouteWatch_{SafeFileNamePart(TargetHost)}_{DateTime.Now:yyyyMMdd_HHmmss}.json",
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

    private bool ValidateInputs()
    {
        TargetHost = TargetHost.Trim();
        Protocol = Protocol.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(TargetHost) || TargetHost.Length > 253)
        {
            ShowTargetResolutionError(TargetHost, new ArgumentException("Target host or IP address is empty or exceeds 253 characters."));
            return false;
        }

        if (!Protocols.Contains(Protocol, StringComparer.OrdinalIgnoreCase))
        {
            StatusText = "Protocol must be ICMP, TCP, or UDP.";
            return false;
        }

        if (PortEnabled && (Port < 1 || Port > 65535))
        {
            StatusText = "Port must be between 1 and 65535.";
            return false;
        }

        if (MaxHops < 1 || MaxHops > 64)
        {
            StatusText = "Max hops must be between 1 and 64.";
            return false;
        }

        if (Interval < 0.1 || Interval > 60)
        {
            StatusText = "Interval must be between 0.1 and 60 seconds.";
            return false;
        }

        if (ProbeTimeout < 0.1 || ProbeTimeout > 30)
        {
            StatusText = "Timeout must be between 0.1 and 30 seconds.";
            return false;
        }

        if (PacketSize < 0 || PacketSize > 1400)
        {
            StatusText = "Packet size must be between 0 and 1400 bytes.";
            return false;
        }

        if (ParallelHops < 1 || ParallelHops > 16)
        {
            StatusText = "Parallel probes must be between 1 and 16.";
            return false;
        }

        return true;
    }

    private static string SafeFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (safe.Length == 0)
            return "target";
        return safe.Length <= 80 ? safe : safe[..80];
    }

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
        if (SelectedHop == null)
        {
            if (_engine?.DestinationHop > 0 && hop.Ttl == _engine.DestinationHop)
                SelectedHop = hop;
            else if (hop.Received > 0)
                SelectedHop = hop;
        }
        else if (SelectedHop.Received == 0 && hop.Received > 0)
        {
            SelectedHop = hop;
        }

        UpdateKpiMetrics();
    }

    private void TrimDisplayedHops(int destinationHop)
    {
        if (destinationHop <= 0)
            return;

        // Never trim intermediate hops if the destination has not actually matched the target host IP
        if (_engine?.Target != null)
        {
            var destHop = Hops.FirstOrDefault(h => h.Ttl == destinationHop);
            if (destHop == null || destHop.IpAddress == "???" ||
                !destHop.IpAddress.Equals(_engine.Target.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Guard against premature collapse to Hop 1 for remote internet targets
            if (destinationHop == 1 && !IsLocalOrLoopbackAddress(_engine.Target))
            {
                return;
            }
        }

        while (Hops.Count > destinationHop)
            Hops.RemoveAt(Hops.Count - 1);
    }

    private static bool IsLocalOrLoopbackAddress(IPAddress ip)
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

    public static (string Message, string Solution, string? SuggestedTarget) SuggestTargetSolution(string? rawTarget, Exception? ex, bool? useIpv6)
    {
        string target = (rawTarget ?? "").Trim();
        string baseMsg = ex?.Message ?? "Unable to resolve target host or address.";

        if (string.IsNullOrWhiteSpace(target))
        {
            return ("Host or IP address is empty.", "Enter a valid hostname (e.g. google.com) or IP address (e.g. 1.1.1.1).", null);
        }

        // 1. Check if user pasted a URL with protocol or path (e.g. https://yahoo.com/path)
        if (target.Contains("://") || target.Contains('/'))
        {
            try
            {
                var uri = new Uri(target.Contains("://") ? target : "http://" + target);
                string cleanHost = uri.DnsSafeHost;
                if (!string.IsNullOrWhiteSpace(cleanHost) && cleanHost != target)
                {
                    return ($"Input contains URL protocol or path: '{target}'",
                            $"Use only the domain name or IP without 'http://' or URL paths: '{cleanHost}'",
                            cleanHost);
                }
            }
            catch { }
        }

        // 2. Check for common domain / TLD typos (e.g., yahoo.ccom, google.cmo)
        var tldTypos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".ccom", ".com" },
            { ".coom", ".com" },
            { ".cmo",  ".com" },
            { ".cm",   ".com" },
            { ".con",  ".com" },
            { ".comm", ".com" },
            { ".ogr",  ".org" },
            { ".orgg", ".org" },
            { ".neet", ".net" },
            { ".nett", ".net" },
            { ".edd",  ".edu" },
            { ".eduu", ".edu" },
            { ".gvo",  ".gov" }
        };

        foreach (var (badTld, goodTld) in tldTypos)
        {
            if (target.EndsWith(badTld, StringComparison.OrdinalIgnoreCase))
            {
                string suggested = target.Substring(0, target.Length - badTld.Length) + goodTld;
                return ($"Host '{target}' could not be resolved (detected typo '{badTld}').",
                        $"Did you mean '{suggested}'? Click 'Use Suggestion & Start' to fix and run.",
                        suggested);
            }
        }

        // 3. IPv6 / IPv4 mode mismatch checks
        if (useIpv6 == true && baseMsg.Contains("IPv6 Only"))
        {
            return ($"Cannot connect to '{target}': Destination does not have an IPv6 (AAAA) record.",
                    "Switch IPv6 toggle to Auto [-] or uncheck it to allow standard IPv4 routing.",
                    null);
        }
        if (useIpv6 == false && baseMsg.Contains("IPv4 Only"))
        {
            return ($"Cannot connect to '{target}': Destination is IPv6 only, but IPv4 Only mode is active.",
                    "Set IPv6 toggle to Auto [-] or check it [✓] to enable IPv6 connectivity.",
                    null);
        }

        // 4. Check for invalid IP-like patterns (e.g., octet > 255 or incomplete IP)
        var parts = target.Split('.');
        if (parts.Length == 4 && parts.All(p => int.TryParse(p, out _)))
        {
            var nums = parts.Select(int.Parse).ToList();
            if (nums.Any(n => n < 0 || n > 255))
            {
                return ($"Invalid IPv4 address '{target}': Octets must be between 0 and 255.",
                        "Correct the out-of-range number (0-255) or specify a valid domain name.",
                        null);
            }
        }
        else if (parts.Length > 1 && parts.Length < 4 && parts.All(p => int.TryParse(p, out _)))
        {
            return ($"Incomplete IPv4 address '{target}'.",
                    "Provide a complete 4-octet address (e.g. 192.168.1.1) or a domain name.",
                    null);
        }

        // 5. General DNS resolution failure
        return ($"Could not resolve host '{target}': {baseMsg}",
                "Check domain spelling, verify your network DNS configuration, or try tracing an IP directly.",
                null);
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
