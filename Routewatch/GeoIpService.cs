using System.IO;
using System.Net;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;

namespace RouteWatch.Services;

/// <summary>
/// Thin wrapper around MaxMind GeoIP2 for optional IP→country/city and ASN lookups.
/// Falls back gracefully when the database is not available.
///
/// Place GeoLite2-City.mmdb and/or GeoLite2-ASN.mmdb next to the executable.
/// </summary>
public sealed class GeoIpService : IDisposable
{
    private DatabaseReader? _cityReader;
    private DatabaseReader? _asnReader;
    private readonly Dictionary<string, GeoResult> _cache = new(512);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsAvailable => _cityReader != null || _asnReader != null;
    public string? DatabasePath { get; private set; }
    public string? AsnDatabasePath { get; private set; }
    public string StatusText => IsAvailable
        ? $"GeoIP: {(DatabasePath != null ? "City" : "")}{(DatabasePath != null && AsnDatabasePath != null ? " + " : "")}{(AsnDatabasePath != null ? "ASN" : "")} database loaded"
        : "GeoIP: City/ASN databases not found";

    public void Initialise(string? mmdbPath = null)
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var cityCandidates = new List<string?>
        {
            mmdbPath,
            Path.Combine(AppContext.BaseDirectory, "GeoLite2-City.mmdb"),
            Path.Combine(Environment.CurrentDirectory, "GeoLite2-City.mmdb"),
            Path.Combine(AppContext.BaseDirectory, "Data", "GeoLite2-City.mmdb"),
            Path.Combine(Environment.CurrentDirectory, "Data", "GeoLite2-City.mmdb"),
            Path.Combine(localData, "RouteWatch", "GeoLite2-City.mmdb"),
            Path.Combine(commonData, "RouteWatch", "GeoLite2-City.mmdb"),
            Path.Combine(localData, "PortMTR", "GeoLite2-City.mmdb"),
            Path.Combine(commonData, "PortMTR", "GeoLite2-City.mmdb"),
        };

        var asnCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "GeoLite2-ASN.mmdb"),
            Path.Combine(Environment.CurrentDirectory, "GeoLite2-ASN.mmdb"),
            Path.Combine(AppContext.BaseDirectory, "Data", "GeoLite2-ASN.mmdb"),
            Path.Combine(Environment.CurrentDirectory, "Data", "GeoLite2-ASN.mmdb"),
            Path.Combine(localData, "RouteWatch", "GeoLite2-ASN.mmdb"),
            Path.Combine(commonData, "RouteWatch", "GeoLite2-ASN.mmdb"),
            Path.Combine(localData, "PortMTR", "GeoLite2-ASN.mmdb"),
            Path.Combine(commonData, "PortMTR", "GeoLite2-ASN.mmdb"),
        };

        foreach (var path in cityCandidates.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)))
        {
            try
            {
                _cityReader = new DatabaseReader(path!);
                DatabasePath = path;
                break;
            }
            catch { /* try the next configured database path */ }
        }

        foreach (var path in asnCandidates.Where(File.Exists))
        {
            try
            {
                _asnReader = new DatabaseReader(path);
                AsnDatabasePath = path;
                break;
            }
            catch { /* try the next configured database path */ }
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
        if (!IsAvailable) return GeoResult.Unknown;
        if (ip.IsPrivate() || IPAddress.IsLoopback(ip)) return GeoResult.Private;

        string country = string.Empty;
        string countryName = string.Empty;
        string cityName = string.Empty;
        double latitude = 0;
        double longitude = 0;
        try
        {
            if (_cityReader != null)
            {
                var city = _cityReader.City(ip);
                country = city.Country.IsoCode ?? string.Empty;
                countryName = city.Country.Name ?? string.Empty;
                cityName = city.City.Name ?? string.Empty;
                latitude = city.Location.Latitude ?? 0;
                longitude = city.Location.Longitude ?? 0;
            }
        }
        catch (AddressNotFoundException) { }
        catch { }

        string asn = string.Empty;
        try
        {
            if (_asnReader != null && _asnReader.Asn(ip) is { } asnResponse)
            {
                string number = asnResponse.AutonomousSystemNumber is long value ? $"AS{value}" : string.Empty;
                string organization = asnResponse.AutonomousSystemOrganization ?? string.Empty;
                asn = string.Join(" · ", new[] { number, organization }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }
        catch (AddressNotFoundException) { }
        catch { }

        return new GeoResult(country, countryName, cityName, latitude, longitude, asn);
    }

    public void Dispose()
    {
        _cityReader?.Dispose();
        _asnReader?.Dispose();
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
        if (b.Length == 4)
        {
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   b[0] == 127 ||
                   (b[0] == 169 && b[1] == 254);
        }

        if (b.Length == 16)
        {
            return ip.IsIPv6LinkLocal ||
                   ip.IsIPv6SiteLocal ||
                   ip.IsIPv6Multicast ||
                   (b[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}
