using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using StatusBoard.Core;

namespace StatusBoard.Views;

/// <summary>
/// A horizontal <see cref="WrapPanel"/> that centres each row instead of starting it at the left, so a short last row sits in the
/// middle. Wrapping is unchanged (the base class measures); only the placement differs. Give items equal left and right margins,
/// or a trailing margin pulls every row off-centre.
/// </summary>
public sealed class CenteredWrapPanel : WrapPanel
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var row = new List<UIElement>();
        double rowWidth = 0, rowHeight = 0, y = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child is null) continue;
            var size = child.DesiredSize;
            if (row.Count > 0 && rowWidth + size.Width > finalSize.Width + 1e-6) PlaceRow();
            row.Add(child);
            rowWidth += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        PlaceRow();
        return finalSize;

        void PlaceRow()
        {
            var x = Math.Max(0, (finalSize.Width - rowWidth) / 2);
            foreach (var child in row)
            {
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, rowHeight));
                x += child.DesiredSize.Width;
            }
            y += rowHeight;
            row.Clear();
            rowWidth = rowHeight = 0;
        }
    }
}

/// <summary>
/// A status shown as colour + icon + text (never colour alone). Its look comes from styles in Styles.xaml (pill, text, dot, wash…);
/// this class supplies the glyph and label for <see cref="Status"/>.
/// </summary>
public class StatusIndicator : Control
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(ServiceStatus), typeof(StatusIndicator),
        new FrameworkPropertyMetadata(ServiceStatus.Unknown, (d, _) => ((StatusIndicator)d).Sync()));
    /// <summary>Optional text instead of the status name (e.g. "Checking…").</summary>
    public static readonly DependencyProperty OverrideTextProperty = DependencyProperty.Register(nameof(OverrideText), typeof(string), typeof(StatusIndicator),
        new FrameworkPropertyMetadata(null, (d, _) => ((StatusIndicator)d).Sync()));
    private static readonly DependencyPropertyKey GlyphKey = DependencyProperty.RegisterReadOnly(nameof(Glyph), typeof(string), typeof(StatusIndicator), new(GlyphFor(ServiceStatus.Unknown)));
    private static readonly DependencyPropertyKey LabelKey = DependencyProperty.RegisterReadOnly(nameof(Label), typeof(string), typeof(StatusIndicator), new("Unknown"));
    public static readonly DependencyProperty GlyphProperty = GlyphKey.DependencyProperty, LabelProperty = LabelKey.DependencyProperty;

    static StatusIndicator() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusIndicator), new FrameworkPropertyMetadata(typeof(StatusIndicator)));

    public StatusIndicator() { Focusable = false; Sync(); }

    public ServiceStatus Status { get => (ServiceStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string? OverrideText { get => (string?)GetValue(OverrideTextProperty); set => SetValue(OverrideTextProperty, value); }
    public string Glyph => (string)GetValue(GlyphProperty);
    public string Label => (string)GetValue(LabelProperty);

    private void Sync()
    {
        var label = OverrideText ?? Status.ToDisplay();
        SetValue(GlyphKey, GlyphFor(Status));
        SetValue(LabelKey, label);
        AutomationProperties.SetName(this, "Status: " + label);
    }

    /// <summary>Segoe Fluent Icons: check, wrench, warning, error, error badge, unknown.</summary>
    public static string GlyphFor(ServiceStatus s) => s switch
    {
        ServiceStatus.Operational => "", ServiceStatus.Maintenance => "", ServiceStatus.Degraded => "",
        ServiceStatus.PartialOutage => "", ServiceStatus.MajorOutage => "", _ => "",
    };
}

/// <summary>true → Visible (or, with <see cref="Invert"/>, Collapsed).</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; init; }
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is Visibility.Visible ^ Invert;
}

/// <summary>Negates a bool (two-way), e.g. for the second of two radio buttons bound to one flag.</summary>
public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

/// <summary>null or "" → Collapsed, otherwise Visible.</summary>
public sealed class NullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) => value is null or "" ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// A colour → its left-to-right fade (problem-card wash). A gradient fill, not an OpacityMask: masks force an off-screen render
/// pass on every frame something animates on top (the card's pulsing dot).
/// </summary>
public sealed class WashBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not SolidColorBrush b) return Brushes.Transparent;
        Color A(int alpha) => Color.FromArgb((byte)(b.Color.A * alpha / 255), b.Color.R, b.Color.G, b.Color.B);
        var brush = new LinearGradientBrush(new GradientStopCollection { new(A(255), 0), new(A(153), 0.45), new(A(0), 1) }, new Point(0, 0), new Point(1, 0.4));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>A status-bar counter: big number in a colour (grey at zero unless <see cref="FadeAtZero"/> is off) over a label.</summary>
public class Counter : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(Counter));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(Counter));
    public static readonly DependencyProperty FadeAtZeroProperty = DependencyProperty.Register(nameof(FadeAtZero), typeof(bool), typeof(Counter), new(true));

    static Counter() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Counter), new FrameworkPropertyMetadata(typeof(Counter)));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool FadeAtZero { get => (bool)GetValue(FadeAtZeroProperty); set => SetValue(FadeAtZeroProperty, value); }
}


/// <summary>
/// The 24-hour strip along the bottom of a tile: one rounded block per half hour, coloured by the worst status seen in it, faint
/// where nothing was recorded. Drawn directly (one element per tile, not 48), and redrawn only when the blocks or theme change.
/// </summary>
public sealed class HistoryStrip : FrameworkElement
{
    public static readonly DependencyProperty BucketsProperty = Prop(nameof(Buckets), typeof(IReadOnlyList<ServiceStatus?>));
    public static readonly DependencyProperty EmptyBrushProperty = Prop(nameof(EmptyBrush));
    public static readonly DependencyProperty OperationalBrushProperty = Prop(nameof(OperationalBrush));
    public static readonly DependencyProperty MaintenanceBrushProperty = Prop(nameof(MaintenanceBrush));
    public static readonly DependencyProperty DegradedBrushProperty = Prop(nameof(DegradedBrush));
    public static readonly DependencyProperty PartialBrushProperty = Prop(nameof(PartialBrush));
    public static readonly DependencyProperty MajorBrushProperty = Prop(nameof(MajorBrush));
    public static readonly DependencyProperty UnknownBrushProperty = Prop(nameof(UnknownBrush));

    private static DependencyProperty Prop(string name, Type? type = null) => DependencyProperty.Register(name, type ?? typeof(Brush), typeof(HistoryStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ServiceStatus?>? Buckets { get => (IReadOnlyList<ServiceStatus?>?)GetValue(BucketsProperty); set => SetValue(BucketsProperty, value); }
    public Brush? EmptyBrush { get => (Brush?)GetValue(EmptyBrushProperty); set => SetValue(EmptyBrushProperty, value); }
    public Brush? OperationalBrush { get => (Brush?)GetValue(OperationalBrushProperty); set => SetValue(OperationalBrushProperty, value); }
    public Brush? MaintenanceBrush { get => (Brush?)GetValue(MaintenanceBrushProperty); set => SetValue(MaintenanceBrushProperty, value); }
    public Brush? DegradedBrush { get => (Brush?)GetValue(DegradedBrushProperty); set => SetValue(DegradedBrushProperty, value); }
    public Brush? PartialBrush { get => (Brush?)GetValue(PartialBrushProperty); set => SetValue(PartialBrushProperty, value); }
    public Brush? MajorBrush { get => (Brush?)GetValue(MajorBrushProperty); set => SetValue(MajorBrushProperty, value); }
    public Brush? UnknownBrush { get => (Brush?)GetValue(UnknownBrushProperty); set => SetValue(UnknownBrushProperty, value); }

    /// <summary>Space between blocks, in layout units.</summary>
    public const double Gap = 2;

    protected override Size MeasureOverride(Size available) => new(0, 6);

    protected override void OnRender(DrawingContext dc)
    {
        var buckets = Buckets;
        var n = buckets?.Count is > 0 ? buckets.Count : StatusHistory.Buckets;
        var w = (ActualWidth - Gap * (n - 1)) / n;
        if (w <= 0 || ActualHeight <= 0) return;
        var radius = Math.Min(1.5, w / 2);
        for (var i = 0; i < n; i++)
        {
            var status = buckets is null ? null : buckets[i];
            var brush = status switch
            {
                null => EmptyBrush,
                ServiceStatus.Operational => OperationalBrush,
                ServiceStatus.Maintenance => MaintenanceBrush,
                ServiceStatus.Degraded => DegradedBrush,
                ServiceStatus.PartialOutage => PartialBrush,
                ServiceStatus.MajorOutage => MajorBrush,
                _ => UnknownBrush,
            };
            if (brush is null) continue;
            dc.DrawRoundedRectangle(brush, null, new Rect(i * (w + Gap), 0, w, ActualHeight), radius, radius);
        }
    }
}
