using System.Net;

namespace RouteWatch.Services;

/// <summary>
/// Async reverse-DNS resolver with a simple LRU-style cache.
/// PTR lookups are done on ThreadPool threads so they never block the engine.
/// </summary>
public sealed class DnsService
{
    private readonly Dictionary<string, string> _cache = new(256);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; }
    }

    /// <summary>
    /// Resolve an IP to a hostname. Returns the IP string if DNS is disabled or lookup fails.
    /// </summary>
    public async Task<string> ResolveAsync(IPAddress ip)
    {
        if (!_enabled) return ip.ToString();

        string key = ip.ToString();

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out string? cached)) return cached;
        }
        finally { _lock.Release(); }

        // Do the lookup outside the lock
        string hostname;
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip).ConfigureAwait(false);
            hostname = entry.HostName;
        }
        catch { hostname = key; }

        await _lock.WaitAsync();
        try { _cache.TryAdd(key, hostname); }
        finally { _lock.Release(); }

        return hostname;
    }

    public void ClearCache()
    {
        _lock.Wait();
        try { _cache.Clear(); }
        finally { _lock.Release(); }
    }
}
