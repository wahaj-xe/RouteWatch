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
/// Represents an active network connection / remote endpoint with port, protocol, and throughput.
/// </summary>
public sealed partial class NetworkConnectionItem : ObservableObject
{
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _imageName = "Unknown";
    [ObservableProperty] private string _localAddress = "";
    [ObservableProperty] private int _localPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayAddress))]
    private string _remoteAddress = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayAddress))]
    private string _remoteHostName = "";

    [ObservableProperty] private int _remotePort;
    [ObservableProperty] private string _protocol = "TCP";
    [ObservableProperty] private string _state = "";

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

    public string DisplayAddress =>
        !string.IsNullOrWhiteSpace(RemoteHostName) && RemoteHostName != "???" && RemoteHostName != RemoteAddress
            ? RemoteHostName
            : RemoteAddress;

    public string SendSpeedFormatted => ProcessNetworkItem.FormatSpeed(SendBytesPerSec);
    public string ReceiveSpeedFormatted => ProcessNetworkItem.FormatSpeed(ReceiveBytesPerSec);
    public string TotalSpeedFormatted => ProcessNetworkItem.FormatSpeed(TotalBytesPerSec);

    public void NotifySpeedChanged()
    {
        OnPropertyChanged(nameof(SendSpeedFormatted));
        OnPropertyChanged(nameof(ReceiveSpeedFormatted));
        OnPropertyChanged(nameof(TotalSpeedFormatted));
    }

    public bool IsHighBandwidth => TotalBytesPerSec >= 12_500; // >= 100 Kbps
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

