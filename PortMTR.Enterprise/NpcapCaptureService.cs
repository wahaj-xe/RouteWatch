using SharpPcap;
using SharpPcap.LibPcap;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PortMTR.Enterprise.Capture;

/// <summary>
/// Manages the Npcap capture device lifecycle.
/// Opens the best available device in promiscuous mode and applies a capture filter.
/// Capture itself starts lazily when a prober attaches OnPacketArrival handlers.
/// </summary>
public sealed class NpcapCaptureService : IDisposable
{
    private ILiveDevice? _device;
    public string? ActiveDeviceLabel { get; private set; }

    /// <summary>The active capture device (null if Npcap not available).</summary>
    public ILiveDevice? Device => _device;

    /// <summary>True if Npcap is installed and a device was opened successfully.</summary>
    public bool IsAvailable => _device != null;

    /// <summary>
    /// Initialise: pick the best matching device, open it, and set the requested filter.
    /// Silently degrades to null if Npcap is not installed.
    /// </summary>
    public void Initialise(string? preferredDeviceName = null, string? captureFilter = null, IPAddress? targetAddress = null)
    {
        try
        {
            Dispose();

            var devices = CaptureDeviceList.Instance;
            if (devices.Count == 0) return;

            IPAddress? outboundLocal = targetAddress is null ? null : DetermineOutboundLocalAddress(targetAddress);
            ILiveDevice? preferred = null;
            if (preferredDeviceName != null)
            {
                string? deviceId = ExtractDeviceId(preferredDeviceName);
                preferred = devices.FirstOrDefault(d =>
                    string.Equals(d.Name, deviceId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Name, preferredDeviceName, StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains(preferredDeviceName, StringComparison.OrdinalIgnoreCase) ||
                    d is LibPcapLiveDevice ld && (
                        string.Equals(ld.Interface.FriendlyName, preferredDeviceName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals($"{ld.Interface.FriendlyName} ({d.Name})", preferredDeviceName, StringComparison.OrdinalIgnoreCase)));
            }

            ILiveDevice? routed = null;
            if (outboundLocal != null)
                routed = devices.FirstOrDefault(d => DeviceHasAddress(d, outboundLocal));

            ILiveDevice? dev = routed ?? preferred;
            if (dev == null && preferred != null && outboundLocal == null)
                dev = preferred;

            // Fallback strategy: pick best non-loopback, non-virtual adapter
            if (dev == null)
            {
                dev = FindBestActiveAdapter(devices);
            }

            if (dev == null)
                dev = devices.FirstOrDefault(d =>
                    d is LibPcapLiveDevice ld &&
                    !ld.Interface.FriendlyName.Contains("Loopback", StringComparison.OrdinalIgnoreCase));

            dev ??= devices[0];

            dev.Open(mode: DeviceModes.Promiscuous | DeviceModes.MaxResponsiveness, read_timeout: 1);

            // Capture inbound ICMP/ICMPv6 only - reduces load significantly.
            dev.Filter = captureFilter ?? "(icmp and (icmp[icmptype] = icmp-timxceed or icmp[icmptype] = icmp-unreach)) or icmp6";

            _device = dev;
            ActiveDeviceLabel = BuildDeviceLabel(dev);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NpcapCaptureService] Failed to open device: {ex.Message}");
        }
    }

    private static ILiveDevice? FindBestActiveAdapter(IList<ILiveDevice> devices)
    {
        // Try to find an active non-virtual adapter using NetworkInterface
        try
        {
            var activeAdapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             !ni.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) &&
                             !ni.Description.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) &&
                             !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                // Prioritize: Ethernet first, then WiFi, then others
                .OrderBy(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 :
                              ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 2)
                .ToList();

            foreach (var adapter in activeAdapters)
            {
                // Try to match by adapter name or description
                var match = devices.FirstOrDefault(d =>
                    d is LibPcapLiveDevice ld && (
                        ld.Interface.FriendlyName.IndexOf(adapter.Name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ld.Interface.FriendlyName.IndexOf(adapter.Description, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        adapter.Name.IndexOf(ld.Interface.FriendlyName, StringComparison.OrdinalIgnoreCase) >= 0));

                if (match != null)
                    return match;
            }
        }
        catch { /* Fall through to next strategy */ }

        // Fallback: prefer non-virtual adapters (Ethernet or WiFi)
        // First try Ethernet
        var ethernet = devices.FirstOrDefault(d =>
                   d is LibPcapLiveDevice ld &&
                   ld.Interface.FriendlyName.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) &&
                   !ld.Interface.FriendlyName.Contains("Loopback", StringComparison.OrdinalIgnoreCase) &&
                   !ld.Interface.FriendlyName.Contains("vEthernet", StringComparison.OrdinalIgnoreCase));
        
        if (ethernet != null)
            return ethernet;

        // Then try WiFi/Wireless
        var wireless = devices.FirstOrDefault(d =>
                   d is LibPcapLiveDevice ld &&
                   (ld.Interface.FriendlyName.Contains("WiFi", StringComparison.OrdinalIgnoreCase) ||
                    ld.Interface.FriendlyName.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                    ld.Interface.Description.Contains("WiFi", StringComparison.OrdinalIgnoreCase) ||
                    ld.Interface.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase)) &&
                   !ld.Interface.FriendlyName.Contains("Loopback", StringComparison.OrdinalIgnoreCase) &&
                   !ld.Interface.FriendlyName.Contains("vEthernet", StringComparison.OrdinalIgnoreCase));
        
        if (wireless != null)
            return wireless;

        // Final fallback: any non-virtual adapter
        return devices.FirstOrDefault(d =>
                   d is LibPcapLiveDevice ld &&
                   !ld.Interface.FriendlyName.Contains("Loopback", StringComparison.OrdinalIgnoreCase) &&
                   !ld.Interface.FriendlyName.Contains("vEthernet", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns a human-readable list of available capture devices.</summary>
    public static IReadOnlyList<string> ListDevices()
    {
        try
        {
            return CaptureDeviceList.Instance
                .OfType<LibPcapLiveDevice>()
                .Select(d => $"{d.Interface.FriendlyName} ({d.Name})")
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ExtractDeviceId(string selectedDevice)
    {
        var match = Regex.Match(selectedDevice, @"\((\\Device\\NPF_[^)]+)\)$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string BuildDeviceLabel(ILiveDevice device)
    {
        if (device is LibPcapLiveDevice ld && !string.IsNullOrWhiteSpace(ld.Interface.FriendlyName))
            return $"{ld.Interface.FriendlyName} ({device.Name})";

        return device.Name;
    }

    private static IPAddress? DetermineOutboundLocalAddress(IPAddress targetAddress)
    {
        try
        {
            using var sock = new Socket(targetAddress.AddressFamily, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            sock.Connect(new IPEndPoint(targetAddress, 33434));
            return (sock.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return null;
        }
    }

    private static bool DeviceHasAddress(ILiveDevice device, IPAddress address)
    {
        if (device is not LibPcapLiveDevice ld)
            return false;

        return ld.Addresses.Any(a => IPAddress.TryParse(a.Addr?.ToString(), out var ip) && ip.Equals(address));
    }

    public void Dispose()
    {
        if (_device == null) return;
        try
        {
            _device.StopCapture();
            _device.Close();
            _device.Dispose();
        }
        catch { /* ignore dispose errors */ }
        _device = null;
        ActiveDeviceLabel = null;
    }
}
