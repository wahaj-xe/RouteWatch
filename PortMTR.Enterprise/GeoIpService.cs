using System.IO;
using System.Net;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;

namespace PortMTR.Enterprise.Services;

/// <summary>
/// Thin wrapper around MaxMind GeoIP2 for IP→country/city/ASN lookups.
/// Falls back gracefully when the database is not available.
///
/// Download GeoLite2-City.mmdb from https://dev.maxmind.com/geoip/geolite2-free-geolocation-data
/// and place it next to the executable.
/// </summary>
public sealed class GeoIpService : IDisposable
{
    private DatabaseReader? _reader;
    private readonly Dictionary<string, GeoResult> _cache = new(512);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsAvailable => _reader != null;

    public void Initialise(string? mmdbPath = null)
    {
        var candidates = new[]
        {
            mmdbPath,
            Path.Combine(AppContext.BaseDirectory, "GeoLite2-City.mmdb"),
            Path.Combine(Environment.CurrentDirectory, "GeoLite2-City.mmdb"),
        };

        foreach (var path in candidates.Where(p => p != null && File.Exists(p)))
        {
            try
            {
                _reader = new DatabaseReader(path!);
                return;
            }
            catch { /* try next */ }
        }
    }

    public async Task<GeoResult> LookupAsync(IPAddress ip)
    {
        string key = ip.ToString();

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var result = Lookup(ip);
            _cache[key] = result;
            return result;
        }
        finally { _lock.Release(); }
    }

    private GeoResult Lookup(IPAddress ip)
    {
        if (_reader == null) return GeoResult.Unknown;
        if (ip.IsPrivate() || IPAddress.IsLoopback(ip)) return GeoResult.Private;

        try
        {
            var city = _reader.City(ip);
            return new GeoResult(
                Country: city.Country.IsoCode ?? string.Empty,
                CountryName: city.Country.Name ?? string.Empty,
                CityName: city.City.Name ?? string.Empty,
                Latitude: city.Location.Latitude ?? 0,
                Longitude: city.Location.Longitude ?? 0,
                Asn: string.Empty
            );
        }
        catch (AddressNotFoundException) { return GeoResult.Unknown; }
        catch { return GeoResult.Unknown; }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _lock.Dispose();
    }
}

public sealed record GeoResult(
    string Country, string CountryName, string CityName,
    double Latitude, double Longitude, string Asn)
{
    public static readonly GeoResult Unknown = new("", "", "", 0, 0, "");
    public static readonly GeoResult Private = new("LAN", "Private", "", 0, 0, "");

    public string Display =>
        string.IsNullOrEmpty(CountryName) ? string.Empty :
        string.IsNullOrEmpty(CityName)    ? CountryName :
        $"{CityName}, {Country}";
}

internal static class IpAddressExtensions
{
    public static bool IsPrivate(this IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               b[0] == 127;
    }
}
