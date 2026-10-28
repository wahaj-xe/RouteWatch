using CommunityToolkit.Mvvm.ComponentModel;

namespace RouteWatch.Models;

/// <summary>
/// Represents a running process with network activity (Send/Receive throughput).
/// </summary>
public sealed partial class ProcessNetworkItem : ObservableObject
{
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _imageName = "Unknown";
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendSpeedFormatted))]
    private long _sendBytesPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceiveSpeedFormatted))]
    private long _receiveBytesPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalSpeedFormatted))]
    private long _totalBytesPerSec;

    [ObservableProperty] private int _activeConnectionsCount;
    [ObservableProperty] private bool _isSelected;

    public string SendSpeedFormatted => FormatSpeed(SendBytesPerSec);
    public string ReceiveSpeedFormatted => FormatSpeed(ReceiveBytesPerSec);
    public string TotalSpeedFormatted => FormatSpeed(TotalBytesPerSec);

    public void NotifySpeedChanged()
    {
        OnPropertyChanged(nameof(SendSpeedFormatted));
        OnPropertyChanged(nameof(ReceiveSpeedFormatted));
        OnPropertyChanged(nameof(TotalSpeedFormatted));
    }

    /// <summary>
    /// When true (default), speeds are formatted in standard network bit rates (Mb and Kb: Mbps / Kbps).
    /// When false, speeds are formatted in byte rates (MB/s / KB/s).
    /// </summary>
    public static bool UseBitsRate { get; set; } = true;

    public static string FormatSpeed(long bytesPerSec)
    {
        if (bytesPerSec <= 0) return UseBitsRate ? "0 Kbps" : "0 B/s";

        if (UseBitsRate)
        {
            double bitsPerSec = bytesPerSec * 8.0;

            if (bitsPerSec < 1000.0)
                return $"{bitsPerSec:F0} bps";

            if (bitsPerSec < 1_000_000.0)
                return $"{bitsPerSec / 1000.0:F1} Kbps";

            if (bitsPerSec < 1_000_000_000.0)
                return $"{bitsPerSec / 1_000_000.0:F2} Mbps";

            return $"{bitsPerSec / 1_000_000_000.0:F2} Gbps";
        }
        else
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:N0} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:F1} KB/s";
            return $"{bytesPerSec / (1024.0 * 1024.0):F2} MB/s";
        }
    }

    public static string FormatBytesSpeed(long bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec:N0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:F1} KB/s";
        return $"{bytesPerSec / (1024.0 * 1024.0):F2} MB/s";
    }
}

/// <summary>
/// Represents an active network connection, remote endpoint, or listening port (TCP / UDP).
/// </summary>
public sealed partial class NetworkConnectionItem : ObservableObject
{
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _imageName = "Unknown";
    [ObservableProperty] private string _localAddress = "";
    [ObservableProperty] private int _localPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayAddress))]
    [NotifyPropertyChangedFor(nameof(DisplayRemoteIp))]
    [NotifyPropertyChangedFor(nameof(DisplayHostOrRole))]
    private string _remoteAddress = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayAddress))]
    [NotifyPropertyChangedFor(nameof(DisplayHostOrRole))]
    private string _remoteHostName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPort))]
    private int _remotePort;

    [ObservableProperty] private string _protocol = "TCP";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListening))]
    [NotifyPropertyChangedFor(nameof(DisplayAddress))]
    [NotifyPropertyChangedFor(nameof(DisplayPort))]
    [NotifyPropertyChangedFor(nameof(DisplayHostOrRole))]
    private string _state = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendSpeedFormatted))]
    private long _sendBytesPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceiveSpeedFormatted))]
    private long _receiveBytesPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalSpeedFormatted))]
    [NotifyPropertyChangedFor(nameof(IsHighBandwidth))]
    private long _totalBytesPerSec;

    [ObservableProperty] private double _latencyMs = -1;

    public bool IsListening =>
        string.Equals(State, "Listen", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(State, "Listening", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(State, "Bound", StringComparison.OrdinalIgnoreCase) ||
        RemotePort == 0 ||
        RemoteAddress == "*";

    public int DisplayPort => RemotePort > 0 ? RemotePort : LocalPort;

    public string DisplayAddress
    {
        get
        {
            if (IsListening || string.IsNullOrWhiteSpace(RemoteAddress) || RemoteAddress == "*")
            {
                if (!string.IsNullOrWhiteSpace(LocalAddress) && LocalAddress != "0.0.0.0" && LocalAddress != "::")
                    return LocalAddress;
                return "* (All Interfaces)";
            }

            if (!string.IsNullOrWhiteSpace(RemoteHostName) && RemoteHostName != "???" && RemoteHostName != RemoteAddress)
                return RemoteHostName;

            return RemoteAddress;
        }
    }

    public string DisplayRemoteIp =>
        !string.IsNullOrWhiteSpace(RemoteAddress) && RemoteAddress != "*"
            ? RemoteAddress
            : (!string.IsNullOrWhiteSpace(LocalAddress) ? LocalAddress : "0.0.0.0");

    public string DisplayHostOrRole
    {
        get
        {
            if (IsListening)
            {
                return Protocol == "UDP" ? "UDP Listening Port" : "TCP Listening Port";
            }
            if (!string.IsNullOrWhiteSpace(RemoteHostName) && RemoteHostName != "???")
                return RemoteHostName;
            return RemoteAddress;
        }
    }

    public string SendSpeedFormatted => ProcessNetworkItem.FormatSpeed(SendBytesPerSec);
    public string ReceiveSpeedFormatted => ProcessNetworkItem.FormatSpeed(ReceiveBytesPerSec);
    public string TotalSpeedFormatted => ProcessNetworkItem.FormatSpeed(TotalBytesPerSec);

    public void NotifySpeedChanged()
    {
        OnPropertyChanged(nameof(SendSpeedFormatted));
        OnPropertyChanged(nameof(ReceiveSpeedFormatted));
        OnPropertyChanged(nameof(TotalSpeedFormatted));
        OnPropertyChanged(nameof(DisplayAddress));
        OnPropertyChanged(nameof(DisplayPort));
        OnPropertyChanged(nameof(DisplayRemoteIp));
        OnPropertyChanged(nameof(DisplayHostOrRole));
        OnPropertyChanged(nameof(IsListening));
    }

    public bool IsHighBandwidth => TotalBytesPerSec >= 12_500; // >= 100 Kbps
}

/// <summary>
/// Mode used to filter sockets in Resource Monitor (All, Active connections only, or Listening ports only).
/// </summary>
public enum SocketFilterMode
{
    All,
    Active,
    Listening
}

/// <summary>
/// Metric used to sort network processes and connections top-to-bottom.
/// </summary>
public enum NetworkSortMetric
{
    Total,
    Receive,
    Send
}
