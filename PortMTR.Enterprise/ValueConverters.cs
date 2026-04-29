using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PortMTR.Enterprise.Models;

namespace PortMTR.Enterprise.Converters;

/// <summary>Converts a Loss% double to a WPF SolidColorBrush.</summary>
[ValueConversion(typeof(double), typeof(SolidColorBrush))]
public sealed class LossToBrushConverter : IValueConverter
{
    public static readonly LossToBrushConverter Instance = new();

    private static readonly SolidColorBrush BrushOk       = Frozen(0x3F, 0xB9, 0x50);
    private static readonly SolidColorBrush BrushWarn      = Frozen(0xD2, 0x99, 0x22);
    private static readonly SolidColorBrush BrushBad       = Frozen(0xF0, 0x88, 0x3E);
    private static readonly SolidColorBrush BrushCritical  = Frozen(0xF8, 0x51, 0x49);
    private static readonly SolidColorBrush BrushNeutral   = Frozen(0x8B, 0x94, 0x9E);

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is double d)
        {
            if (d == 0)   return BrushOk;
            if (d < 10)   return BrushWarn;
            if (d < 50)   return BrushBad;
            return BrushCritical;
        }
        return BrushNeutral;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}

/// <summary>Returns row background brush based on HopFlag.</summary>
[ValueConversion(typeof(HopFlag), typeof(Brush))]
public sealed class FlagToRowBrushConverter : IValueConverter
{
    public static readonly FlagToRowBrushConverter Instance = new();

    private static readonly SolidColorBrush BrushNone     = Frozen(0x16, 0x1B, 0x22);
    private static readonly SolidColorBrush BrushWarn     = Frozen(0x1E, 0x17, 0x00);
    private static readonly SolidColorBrush BrushCritical = Frozen(0x1E, 0x07, 0x07);
    private static readonly SolidColorBrush BrushDest     = Frozen(0x07, 0x1E, 0x0A);
    private static readonly SolidColorBrush BrushMuted    = Frozen(0x10, 0x10, 0x14);

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is HopFlag flag)
        {
            return flag switch
            {
                HopFlag.SeverePacketLoss => BrushCritical,
                HopFlag.PacketLoss       => BrushWarn,
                HopFlag.HighLatency      => BrushWarn,
                HopFlag.Unreachable      => BrushMuted,
                HopFlag.Destination      => BrushDest,
                _                         => BrushNone,
            };
        }
        return BrushNone;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}

/// <summary>Maps HopFlag to a short coloured badge string.</summary>
[ValueConversion(typeof(HopFlag), typeof(string))]
public sealed class FlagToLabelConverter : IValueConverter
{
    public static readonly FlagToLabelConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is HopFlag f ? f switch
        {
            HopFlag.None             => "Healthy",
            HopFlag.HighLatency      => "⚡ High RTT",
            HopFlag.PacketLoss       => "⚠ Loss",
            HopFlag.SeverePacketLoss => "🔴 Severe Loss",
            HopFlag.Unreachable      => "✖ Unreachable",
            HopFlag.Destination      => "✔ Destination",
            HopFlag.AsymmetricRoute  => "↕ Asymmetric",
            _                         => f.ToString()
        } : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;
}

/// <summary>Combines city/country into a readable location label.</summary>
public sealed class LocationDisplayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string city = values.Length > 0 ? values[0] as string ?? string.Empty : string.Empty;
        string country = values.Length > 1 ? values[1] as string ?? string.Empty : string.Empty;

        city = city.Trim();
        country = country.Trim();

        if (string.IsNullOrEmpty(city) && string.IsNullOrEmpty(country))
            return "Unknown";
        if (string.IsNullOrEmpty(city))
            return country;
        if (string.IsNullOrEmpty(country))
            return city;

        return $"{city}, {country}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        Array.Empty<object>();
}

/// <summary>Converts -1 RTT (timeout) to "—" string.</summary>
[ValueConversion(typeof(double), typeof(string))]
public sealed class RttToStringConverter : IValueConverter
{
    public static readonly RttToStringConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is double d ? (d < 0 ? "—" : $"{d:F1}") : "—";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;
}

/// <summary>Formats Best ms (double.MaxValue = not yet received).</summary>
[ValueConversion(typeof(double), typeof(string))]
public sealed class BestRttConverter : IValueConverter
{
    public static readonly BestRttConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is double d ? (d >= double.MaxValue - 1 ? "—" : $"{d:F1}") : "—";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;
}

/// <summary>True → Visible / False → Collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;
}

/// <summary>Converts stability score (0–100) to a descriptive string.</summary>
[ValueConversion(typeof(double), typeof(string))]
public sealed class ScoreToLabelConverter : IValueConverter
{
    public static readonly ScoreToLabelConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is double d ? d switch
        {
            >= 95 => "Excellent",
            >= 80 => "Good",
            >= 60 => "Fair",
            >= 40 => "Poor",
            _      => "Critical"
        } : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => DependencyProperty.UnsetValue;
}
