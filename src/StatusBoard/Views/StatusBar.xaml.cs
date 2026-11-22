using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StatusBoard.Views;

/// <summary>The status bar. Code-behind only drives the news ticker's scroll.</summary>
public partial class StatusBar : UserControl
{
    /// <summary>Target speed in device-independent pixels per second: readable, not distracting.</summary>
    public const double TickerSpeed = 60;
    private readonly TranslateTransform _shift = new();
    private bool _running, _paused;
    private double _offsetPx;            // how far the track has moved, in device pixels
    private double _copyWidthPx;          // one copy of the items, in device pixels
    private double _stepPx = 1;           // whole device pixels per displayed frame
    private TimeSpan _lastFrame;
    private readonly Queue<double> _intervals = new();

    public StatusBar()
    {
        InitializeComponent();
        TickerTrack.RenderTransform = _shift;
        TickerA.SizeChanged += (_, _) => RestartTicker();
        TickerViewport.SizeChanged += (_, _) => RestartTicker();
        TickerViewport.MouseEnter += (_, _) => Pause(true);
        TickerViewport.MouseLeave += (_, _) => Pause(TickerViewport.IsKeyboardFocusWithin);
        TickerViewport.IsKeyboardFocusWithinChanged += (_, e) => Pause((bool)e.NewValue || TickerViewport.IsMouseOver);
        IsVisibleChanged += (_, _) => RestartTicker(); // no per-frame work while hidden (e.g. the details panel covers it)
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => RestartTicker(); // TV mode moves the bar out of the scrolling area and back
    }

    public bool TickerScrolling => _running;
    public double TickerOffset => _shift.X;

    public void Pause(bool pause) => _paused = pause;

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>
    /// (Re)starts the scroll when the items or the strip width change. The items are drawn twice, end to end, and the track moves
    /// left by one copy's width before wrapping, so the join is invisible.
    /// <para>Smoothness: the track moves by the same whole number of device pixels on every displayed frame (driven by
    /// <see cref="CompositionTarget.Rendering"/>, so it follows the screen's own refresh). Whole pixels keep the cached text
    /// crisp instead of being resampled at a new sub-pixel offset each frame, and a constant step means even motion; if a frame
    /// is ever late, the ticker pauses for that frame rather than jumping to catch up.</para>
    /// </summary>
    private void RestartTicker()
    {
        var width = TickerA.ActualWidth;
        var scroll = IsVisible && width > TickerViewport.ActualWidth + 1;
        TickerB.Visibility = width > TickerViewport.ActualWidth + 1 ? Visibility.Visible : Visibility.Collapsed;
        var newCopyPx = Math.Round(width * Dpi);
        if (!scroll || width <= 0) { Stop(); _offsetPx = 0; _shift.X = 0; return; }
        if (Math.Abs(newCopyPx - _copyWidthPx) > 0.5) _offsetPx %= Math.Max(1, newCopyPx); // items changed: keep going from about here
        _copyWidthPx = newCopyPx;
        if (_running) return;
        _running = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs r || r.RenderingTime == _lastFrame) return; // Rendering can fire twice per frame
        if (_lastFrame != TimeSpan.Zero)
        {
            // Learn the display's frame interval (median of recent ones) and pick the whole-pixel step closest to the target speed.
            var interval = (r.RenderingTime - _lastFrame).TotalSeconds;
            if (interval is > 0.002 and < 0.1)
            {
                _intervals.Enqueue(interval);
                if (_intervals.Count > 60) _intervals.Dequeue();
                var median = _intervals.Order().ElementAt(_intervals.Count / 2);
                _stepPx = Math.Max(1, Math.Round(TickerSpeed * Dpi * median));
            }
        }
        _lastFrame = r.RenderingTime;
        if (_paused || _copyWidthPx <= 0) return;
        _offsetPx = (_offsetPx + _stepPx) % _copyWidthPx;
        _shift.X = -_offsetPx / Dpi;
    }
}
