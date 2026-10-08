using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RouteWatch.Controls;

/// <summary>
/// High-performance, hardware-accelerated native WPF vector chart for live hop latency history.
/// Eliminates third-party SkiaSharp blank-render bugs and scales smoothly with DPI.
/// </summary>
public sealed class LatencyGraphControl : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(
            nameof(Values),
            typeof(ObservableCollection<double>),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata(null, OnValuesChanged));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(
            nameof(AccentBrush),
            typeof(Brush),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty =
        DependencyProperty.Register(
            nameof(GridBrush),
            typeof(Brush),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty =
        DependencyProperty.Register(
            nameof(TextBrush),
            typeof(Brush),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(140, 160, 180)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HopTitleProperty =
        DependencyProperty.Register(
            nameof(HopTitle),
            typeof(string),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata("Selected Hop Latency", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AvgValueProperty =
        DependencyProperty.Register(
            nameof(AvgValue),
            typeof(double),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BestValueProperty =
        DependencyProperty.Register(
            nameof(BestValue),
            typeof(double),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WorstValueProperty =
        DependencyProperty.Register(
            nameof(WorstValue),
            typeof(double),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LastValueProperty =
        DependencyProperty.Register(
            nameof(LastValue),
            typeof(double),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LossPercentProperty =
        DependencyProperty.Register(
            nameof(LossPercent),
            typeof(double),
            typeof(LatencyGraphControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ObservableCollection<double>? Values
    {
        get => (ObservableCollection<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public string HopTitle
    {
        get => (string)GetValue(HopTitleProperty);
        set => SetValue(HopTitleProperty, value);
    }

    public double AvgValue
    {
        get => (double)GetValue(AvgValueProperty);
        set => SetValue(AvgValueProperty, value);
    }

    public double BestValue
    {
        get => (double)GetValue(BestValueProperty);
        set => SetValue(BestValueProperty, value);
    }

    public double WorstValue
    {
        get => (double)GetValue(WorstValueProperty);
        set => SetValue(WorstValueProperty, value);
    }

    public double LastValue
    {
        get => (double)GetValue(LastValueProperty);
        set => SetValue(LastValueProperty, value);
    }

    public double LossPercent
    {
        get => (double)GetValue(LossPercentProperty);
        set => SetValue(LossPercentProperty, value);
    }

    private NotifyCollectionChangedEventHandler? _collectionHandler;

    public LatencyGraphControl()
    {
        ClipToBounds = true;
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LatencyGraphControl ctrl)
        {
            if (e.OldValue is INotifyCollectionChanged oldCol && ctrl._collectionHandler != null)
            {
                oldCol.CollectionChanged -= ctrl._collectionHandler;
                ctrl._collectionHandler = null;
            }

            if (e.NewValue is INotifyCollectionChanged newCol)
            {
                ctrl._collectionHandler = (_, _) => ctrl.Dispatcher.InvokeAsync(ctrl.InvalidateVisual);
                newCol.CollectionChanged += ctrl._collectionHandler;
            }

            ctrl.InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 40 || h < 40) return;

        var typeFace = new Typeface(
            new FontFamily("Cascadia Code, Consolas, Segoe UI"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        var boldTypeFace = new Typeface(
            new FontFamily("Cascadia Code, Consolas, Segoe UI"),
            FontStyles.Normal,
            FontWeights.Bold,
            FontStretches.Normal);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Margins for axes and HUD
        double left = 48;
        double right = 56;
        double top = 34;
        double bottom = 24;

        double chartW = Math.Max(10, w - left - right);
        double chartH = Math.Max(10, h - top - bottom);

        // 1. Draw top HUD Header
        DrawHudHeader(dc, w, boldTypeFace, typeFace, dpi);

        // Extract valid positive samples
        var rawValues = Values?.ToList() ?? new List<double>();
        var samples = rawValues.Where(v => v >= 0).ToList();

        // If no samples, draw waiting grid
        if (samples.Count == 0)
        {
            DrawEmptyGrid(dc, left, top, chartW, chartH, typeFace, dpi);
            return;
        }

        // 2. Compute dynamic Y-axis scale
        double maxSample = Math.Max(samples.Max(), 10.0);
        if (WorstValue > maxSample && WorstValue < 5000)
            maxSample = WorstValue;

        double gridMax = NiceCeiling(maxSample * 1.15);

        // 3. Draw Grid Lines & Labels
        var gridPen = new Pen(GridBrush, 1.0);
        gridPen.Freeze();

        int gridDivisions = 4;
        for (int i = 0; i <= gridDivisions; i++)
        {
            double ratio = (double)i / gridDivisions;
            double yVal = ratio * gridMax;
            double y = (top + chartH) - (ratio * chartH);

            // Grid line
            dc.DrawLine(gridPen, new Point(left, y), new Point(left + chartW, y));

            // Y-axis label (left)
            string labelStr = yVal >= 1000 ? $"{yVal / 1000:F1}s" : $"{yVal:F0}ms";
            var labelText = new FormattedText(
                labelStr,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeFace,
                9.5,
                TextBrush,
                dpi);

            dc.DrawText(labelText, new Point(left - labelText.Width - 6, y - (labelText.Height / 2)));
        }

        // 4. Reference line for Average
        if (AvgValue > 0 && AvgValue <= gridMax)
        {
            double avgY = (top + chartH) - (AvgValue / gridMax * chartH);
            var avgPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 230, 0)), 1.0)
            {
                DashStyle = DashStyles.Dash
            };
            avgPen.Freeze();
            dc.DrawLine(avgPen, new Point(left, avgY), new Point(left + chartW, avgY));

            var avgLabel = new FormattedText(
                $"AVG {AvgValue:F1}ms",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeFace,
                9.0,
                new SolidColorBrush(Color.FromRgb(255, 230, 0)),
                dpi);
            dc.DrawText(avgLabel, new Point(left + chartW + 4, avgY - (avgLabel.Height / 2)));
        }

        // 5. Build Points
        int count = rawValues.Count;
        var points = new List<Point>(count);
        var lostXList = new List<double>();

        for (int i = 0; i < count; i++)
        {
            double x = left + (count > 1 ? (double)i / (count - 1) * chartW : chartW / 2);
            double val = rawValues[i];

            if (val < 0)
            {
                // Lost packet
                lostXList.Add(x);
                points.Add(new Point(x, top + chartH));
            }
            else
            {
                double clamped = Math.Clamp(val / gridMax, 0.0, 1.0);
                double y = (top + chartH) - (clamped * chartH);
                points.Add(new Point(x, y));
            }
        }

        // 6. Draw Gradient Area Fill
        if (points.Count >= 2)
        {
            var areaGeom = new StreamGeometry();
            using (var ctx = areaGeom.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, top + chartH), isFilled: true, isClosed: true);
                ctx.LineTo(points[0], true, false);
                for (int i = 1; i < points.Count; i++)
                {
                    ctx.LineTo(points[i], true, false);
                }
                ctx.LineTo(new Point(points[^1].X, top + chartH), true, false);
            }
            areaGeom.Freeze();

            Color accentCol = Colors.Cyan;
            if (AccentBrush is SolidColorBrush scb)
                accentCol = scb.Color;

            var areaBrush = new LinearGradientBrush(
                Color.FromArgb(70, accentCol.R, accentCol.G, accentCol.B),
                Color.FromArgb(0, accentCol.R, accentCol.G, accentCol.B),
                new Point(0, 0),
                new Point(0, 1));
            areaBrush.Freeze();

            dc.DrawGeometry(areaBrush, null, areaGeom);

            // 7. Draw Vector Line
            var lineGeom = new StreamGeometry();
            using (var ctx = lineGeom.Open())
            {
                ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
                for (int i = 1; i < points.Count; i++)
                {
                    ctx.LineTo(points[i], true, false);
                }
            }
            lineGeom.Freeze();

            var linePen = new Pen(AccentBrush, 2.0);
            linePen.Freeze();
            dc.DrawGeometry(null, linePen, lineGeom);
        }

        // 8. Draw Dropped Packet Markers
        if (lostXList.Count > 0)
        {
            var redPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 42, 109)), 1.5);
            redPen.Freeze();
            foreach (var lx in lostXList)
            {
                dc.DrawLine(redPen, new Point(lx, top + chartH - 12), new Point(lx, top + chartH));
            }
        }

        // 9. Draw Latest Sample Pulsing Ring
        if (points.Count > 0)
        {
            var lastPt = points[^1];
            Color accentCol = Colors.Cyan;
            if (AccentBrush is SolidColorBrush scb)
                accentCol = scb.Color;

            var glowBrush = new SolidColorBrush(Color.FromArgb(60, accentCol.R, accentCol.G, accentCol.B));
            glowBrush.Freeze();

            dc.DrawEllipse(glowBrush, null, lastPt, 7, 7);
            dc.DrawEllipse(AccentBrush, null, lastPt, 3.5, 3.5);
        }

        // 10. Sample count footer
        string footerStr = $"← Older ({count} samples)                    Latest Ping →";
        var footerText = new FormattedText(
            footerStr,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeFace,
            9.0,
            TextBrush,
            dpi);
        dc.DrawText(footerText, new Point(left + (chartW - footerText.Width) / 2, top + chartH + 7));
    }

    private void DrawHudHeader(DrawingContext dc, double w, Typeface boldTypeFace, Typeface regularTypeFace, double dpi)
    {
        // Title (left)
        var titleText = new FormattedText(
            HopTitle.ToUpperInvariant(),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            boldTypeFace,
            11.0,
            AccentBrush,
            dpi);
        dc.DrawText(titleText, new Point(14, 10));

        // Right metrics HUD
        string lastStr = LastValue < 0 ? "LOST" : $"{LastValue:F1}ms";
        string bestStr = BestValue >= double.MaxValue - 1 || BestValue <= 0 ? "—" : $"{BestValue:F1}ms";
        string worstStr = WorstValue <= 0 ? "—" : $"{WorstValue:F1}ms";

        string metricsStr = $"LAST: {lastStr}  ·  AVG: {AvgValue:F1}ms  ·  BEST: {bestStr}  ·  WORST: {worstStr}  ·  LOSS: {LossPercent:F1}%";
        var metricsText = new FormattedText(
            metricsStr,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            regularTypeFace,
            10.0,
            TextBrush,
            dpi);

        double rightX = Math.Max(titleText.Width + 24, w - metricsText.Width - 14);
        dc.DrawText(metricsText, new Point(rightX, 11));
    }

    private void DrawEmptyGrid(DrawingContext dc, double left, double top, double chartW, double chartH, Typeface typeFace, double dpi)
    {
        var gridPen = new Pen(GridBrush, 1.0);
        gridPen.Freeze();

        for (int i = 0; i <= 4; i++)
        {
            double y = top + (i * (chartH / 4));
            dc.DrawLine(gridPen, new Point(left, y), new Point(left + chartW, y));
        }

        string msg = "● Awaiting live telemetry ping samples for this hop...";
        var emptyText = new FormattedText(
            msg,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeFace,
            11.0,
            TextBrush,
            dpi);

        dc.DrawText(emptyText, new Point(left + (chartW - emptyText.Width) / 2, top + (chartH - emptyText.Height) / 2));
    }

    private static double NiceCeiling(double val)
    {
        if (val <= 20) return 20;
        if (val <= 50) return 50;
        if (val <= 100) return 100;
        if (val <= 200) return 200;
        if (val <= 300) return 300;
        if (val <= 500) return 500;
        if (val <= 1000) return 1000;
        return Math.Ceiling(val / 200.0) * 200.0;
    }
}
