using System.Globalization;
using System.Windows.Data;

namespace PortMTR.Enterprise.Converters;

/// <summary>
/// Converts (Loss%, CellWidth) → pixel width for the loss background bar.
/// Used as a MultiBinding converter inside the DataGrid Loss% column.
/// </summary>
public sealed class LossToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c)
    {
        if (values.Length >= 2 &&
            values[0] is double loss &&
            values[1] is double cellWidth)
        {
            return Math.Min(loss / 100.0 * cellWidth, cellWidth);
        }
        return 0.0;
    }
    public object[] ConvertBack(object v, Type[] types, object p, CultureInfo c)
        => throw new NotImplementedException();
}
