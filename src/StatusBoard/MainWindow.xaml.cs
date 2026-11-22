using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StatusBoard.Services;
using StatusBoard.ViewModels;

namespace StatusBoard;

/// <summary>Layout behaviour the XAML can't express: the docked/full-width details panel, tile layout, entrances, focus and keys.</summary>
public partial class MainWindow : Window
{
    /// <summary>Below this width the details panel takes over the content area instead of docking beside the tiles.</summary>
    private const double NarrowWidth = 760, MinDashboardWidth = 340;
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm, ThemeManager theme)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        SourceInitialized += (_, _) => theme.ApplyTitleBar(this);
        SizeChanged += (_, _) => { Drawer.Width = Math.Min(480, ActualWidth); VendorsDrawer.Width = Math.Min(580, ActualWidth); ApplyPanelLayout(); };
        Scroller.SizeChanged += (_, _) => Relayout();
        Scroller.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) Relayout(); };
        CardsHost.SizeChanged += (_, e) => { if (e.WidthChanged) Relayout(); };
        vm.Cards.CollectionChanged += (_, _) => Relayout();
        vm.PropertyChanged += OnVmPropertyChanged;
        _tvScroll.Tick += TvScrollTick;
        _tvIdle.Tick += (_, _) => HideTvControls();
        Scroller.ScrollChanged += (_, _) => UpdateSticky();
        StatusBar.SizeChanged += (_, _) => UpdateSticky();
        // TV mode: any real mouse movement or key brings the controls (and pointer) back for another 10 s.
        PreviewMouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _lastMouse.X) < 2 && Math.Abs(p.Y - _lastMouse.Y) < 2) return; // tiles moving under a still mouse
            _lastMouse = p;
            ShowTvControls();
        };
        PreviewKeyDown += (_, _) => ShowTvControls();
        HeaderGrid.SizeChanged += (_, _) => FitClock();
        HeaderControls.SizeChanged += (_, _) => FitClock();
        ProfileButton.SizeChanged += (_, _) => FitClock();
        // Ctrl + mouse wheel resizes tiles (as in browsers).
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || _vm.IsDrawerOpen || _vm.IsTvMode) return;
            (e.Delta > 0 ? _vm.ZoomInCommand : _vm.ZoomOutCommand).Execute(null);
            e.Handled = true;
        };
    }

    /// <summary>Width of the tile area in tile units: the host stretches to the viewport and its negative right margin absorbs the
    /// last gap. (The viewer's ViewportWidth can lag behind a scrollbar appearing, so it is only the fallback.)</summary>
    private void Relayout() => _vm.UpdateLayout(CardsHost.ActualWidth > 0 ? CardsHost.ActualWidth + CardsHost.Margin.Right : Scroller.ViewportWidth);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HasSelection):
                var wasOpen = DetailsPanel.Visibility == Visibility.Visible;
                ApplyPanelLayout();
                if (!wasOpen && _vm.HasSelection) SlideIn(DetailsPanel, 36);
                break;
            case nameof(MainViewModel.IsSettingsOpen):
                if (_vm.IsSettingsOpen)
                {
                    SlideIn(Drawer, 60);
                    SettingsScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                }
                // Modal: focus goes into the drawer (Tab cycles inside it) and back to the gear when it closes.
                AfterLayout(() =>
                {
                    if (_vm.IsSettingsOpen) SettingsView.FocusFirst();
                    else if (IsActive && !_vm.IsVendorsOpen) SettingsButton.Focus();
                });
                break;
            case nameof(MainViewModel.IsVendorsOpen):
                if (_vm.IsVendorsOpen)
                {
                    SlideIn(VendorsDrawer, 60);
                    VendorsScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                }
                AfterLayout(() =>
                {
                    if (_vm.IsVendorsOpen) VendorsView.FocusFirst();
                    else if (IsActive && !_vm.IsSettingsOpen) VendorsButton.Focus();
                });
                break;
            case nameof(MainViewModel.TvAutoScroll):
                SyncTvScroll();
                break;
            case nameof(MainViewModel.IsTvMode):
                ApplyTvMode();
                AfterLayout(FitClock);
                break;
            case nameof(MainViewModel.IsSetupOpen):
                if (_vm.IsSetupOpen) { SlideIn(SetupCard, 0); AfterLayout(() => SetupView.FocusFirst()); }
                break;
            case nameof(MainViewModel.Selected) when _vm.Selected is { } selected:
                // Opening the panel reflows the grid and can push the chosen tile out of view: scroll it back.
                AfterLayout(() =>
                {
                    if (ReferenceEquals(_vm.Selected, selected) && Scroller.IsVisible && CardsHost.ItemContainerGenerator.ContainerFromItem(selected) is FrameworkElement card)
                        card.BringIntoView();
                });
                break;
        }
    }

    private void AfterLayout(Action action) => Dispatcher.BeginInvoke(action, DispatcherPriority.Loaded);

    /// <summary>
    /// The date and time stay centred on the window. When they fit between the profile button and the controls they share one
    /// line; otherwise the clock takes its own line above them. Only on a window too narrow for the date does the date hide.
    /// </summary>
    private void FitClock()
    {
        if (HeaderGrid.ActualWidth <= 0) return;
        ClockDate.Visibility = Visibility.Visible;
        ClockPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (ClockPanel.DesiredSize.Width > HeaderGrid.ActualWidth) ClockDate.Visibility = Visibility.Collapsed;
        ClockPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        HeaderControls.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ProfileButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var side = Math.Max(ProfileButton.Visibility == Visibility.Visible ? ProfileButton.DesiredSize.Width : 0, HeaderControls.DesiredSize.Width) + 20;
        var inline = HeaderGrid.ActualWidth - 2 * side >= ClockPanel.DesiredSize.Width;
        var row = inline ? 0 : 1;
        if (Grid.GetRow(HeaderControls) == row) return;
        Grid.SetRow(HeaderControls, row);
        Grid.SetRow(ProfileButton, row);
        ClockPanel.Margin = inline ? new Thickness(0) : new Thickness(0, 0, 0, 10);
    }

    /// <summary>For the self-test: true when the clock shares a line with the controls.</summary>
    public bool ClockInline => Grid.GetRow(HeaderControls) == 0;

    /// <summary>Profile menu: switch, new, manage.</summary>
    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = ProfileButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var row in _vm.ProfileRows)
        {
            var item = new System.Windows.Controls.MenuItem { Header = row.Name, IsCheckable = false, IsChecked = row.IsActive, Command = row.SwitchCommand };
            System.Windows.Automation.AutomationProperties.SetName(item, row.IsActive ? $"{row.Name} (current)" : $"Switch to {row.Name}");
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "New profile…", Command = _vm.NewProfileCommand });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Manage profiles…", Command = _vm.ManageProfilesCommand });
        menu.IsOpen = true;
    }

    // ---- TV mode: borderless full screen; the status boxes and ticker stay pinned while the tiles scroll slowly down beneath
    // them, rest at the bottom, glide back up and rest at the top before the next pass. ----
    private (WindowState State, WindowStyle Style, ResizeMode Resize)? _beforeTv;
    private static readonly TimeSpan ScrollFrame = TimeSpan.FromMilliseconds(33), GlideFrame = TimeSpan.FromMilliseconds(15);
    private readonly DispatcherTimer _tvScroll = new(DispatcherPriority.Render) { Interval = ScrollFrame };
    private DateTime _tvPauseUntil, _tvLastTick, _tvReturnStart;
    private bool _tvAtBottom;
    private double _tvReturnFrom = -1; // >= 0 while gliding back to the top
    private TimeSpan _tvReturnTime;
    /// <summary>Scroll speed in TV mode, device-independent pixels per second.</summary>
    public const double TvScrollSpeed = 28;
    /// <summary>How long TV mode rests at the bottom, and when scrolling is switched back on mid-board (the self-test shortens these).</summary>
    public static TimeSpan TvPause { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>How long TV mode rests at the top, with the whole board in its normal layout, before each pass.</summary>
    public static TimeSpan TvTopPause { get; set; } = TimeSpan.FromSeconds(12);
    /// <summary>Mouse idle time before the TV-mode buttons (and the pointer) fade away.</summary>
    public static TimeSpan TvIdle { get; set; } = TimeSpan.FromSeconds(10);
    public bool TvScrolling => _tvScroll.IsEnabled;
    public bool TvReturning => _tvReturnFrom >= 0;
    public bool TvControlsHidden { get; private set; }
    private readonly DispatcherTimer _tvIdle = new();
    private Point _lastMouse = new(double.NaN, double.NaN);

    private void ApplyTvMode()
    {
        if (_vm.IsTvMode)
        {
            _beforeTv ??= (WindowState, WindowStyle, ResizeMode);
            WindowState = WindowState.Normal; // re-maximising after a style change covers the taskbar
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden; // a clean screen
            Scroller.ScrollToTop();
            SyncTvScroll();
            ShowTvControls();
        }
        else
        {
            _tvScroll.Stop();
            _tvIdle.Stop();
            ShowTvControls();
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            UpdateSticky();
            if (_beforeTv is { } b)
            {
                WindowState = WindowState.Normal;
                (WindowStyle, ResizeMode, WindowState) = (b.Style, b.Resize, b.State);
            }
            _beforeTv = null;
            AfterLayout(() => { if (IsActive) TvButton.Focus(); });
        }
    }

    private readonly TranslateTransform _countersStick = new(), _tickerStick = new();

    /// <summary>For the self-test: how far the counters and the ticker are currently held down to stay at the top.</summary>
    public (double Counters, double Ticker) StickyShift => (_countersStick.Y, _tickerStick.Y);

    /// <summary>
    /// TV mode's "sticky" header. As the board scrolls, the headline and "Needs attention" scroll away as usual, but the counters
    /// are picked up when they reach the top and held there, then the ticker is picked up beneath them; the tiles pass behind
    /// both (the status bar is drawn above the tiles). Scrolling back up releases them in reverse, so at the top the board looks
    /// exactly as it does in a window. Runs on every scroll change, in the same layout pass, so nothing lags a frame behind.
    /// </summary>
    private void UpdateSticky()
    {
        var on = _vm.IsTvMode && StatusBar.IsVisible;
        var top = Scroller.VerticalOffset;
        var counters = StatusBar.CountersBlock;
        var ticker = StatusBar.TickerStrip;
        // Positions in the scrolling content as laid out, i.e. without the current shifts.
        double LaidOutTop(FrameworkElement e, TranslateTransform t) => e.TranslatePoint(new Point(0, 0), ScrollContent).Y - t.Y;
        var countersY = on ? Math.Max(0, top - LaidOutTop(counters, _countersStick)) : 0;
        var tickerY = on && ticker.IsVisible ? Math.Max(0, top + counters.ActualHeight - LaidOutTop(ticker, _tickerStick)) : 0;
        if (counters.RenderTransform != _countersStick) counters.RenderTransform = _countersStick;
        if (ticker.RenderTransform != _tickerStick) ticker.RenderTransform = _tickerStick;
        _countersStick.Y = countersY;
        _tickerStick.Y = tickerY;
        // Opaque only while held, so the tiles disappear behind it; otherwise the card's own (rounded) background shows through.
        if (countersY > 0) counters.SetResourceReference(Border.BackgroundProperty, "Surface");
        else counters.ClearValue(Border.BackgroundProperty);
    }

    /// <summary>Runs the auto-scroll only in TV mode with it switched on; it rests first (at the top for the full top pause).</summary>
    private void SyncTvScroll()
    {
        if (_vm.IsTvMode && _vm.TvAutoScroll)
        {
            if (_tvScroll.IsEnabled) return;
            var now = DateTime.UtcNow;
            (_tvPauseUntil, _tvLastTick, _tvAtBottom, _tvReturnFrom) = (now + (Scroller.VerticalOffset < 0.5 ? TvTopPause : TvPause), now, false, -1);
            _tvScroll.Interval = ScrollFrame;
            _tvScroll.Start();
        }
        else
        {
            _tvScroll.Stop();
            _tvReturnFrom = -1;
        }
    }

    private void TvScrollTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _tvLastTick).TotalSeconds;
        _tvLastTick = now;
        if (TvReturning)
        {
            // Glide back up (ease in and out): the tiles slide out from under the pinned strip and the board settles at the top.
            var t = Math.Clamp((now - _tvReturnStart) / _tvReturnTime, 0, 1);
            var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            Scroller.ScrollToVerticalOffset(_tvReturnFrom * (1 - eased));
            if (t < 1) return;
            _tvReturnFrom = -1;
            Scroller.ScrollToTop();
            _tvScroll.Interval = ScrollFrame;
            _tvPauseUntil = now + TvTopPause;
            return;
        }
        if (now < _tvPauseUntil || Scroller.ScrollableHeight <= 0) return;
        if (_tvAtBottom)
        {
            // Rested at the bottom long enough: glide back to the top (about 1-3 s however long the board is).
            _tvAtBottom = false;
            (_tvReturnFrom, _tvReturnStart) = (Scroller.VerticalOffset, now);
            _tvReturnTime = TimeSpan.FromSeconds(Math.Clamp(0.8 + _tvReturnFrom / 1500, 1.2, 3));
            _tvScroll.Interval = GlideFrame;
            return;
        }
        var next = Math.Min(Scroller.ScrollableHeight, Scroller.VerticalOffset + TvScrollSpeed * Math.Min(dt, 0.25));
        Scroller.ScrollToVerticalOffset(next);
        if (next >= Scroller.ScrollableHeight - 0.5) (_tvAtBottom, _tvPauseUntil) = (true, now + TvPause);
    }

    /// <summary>Shows the TV-mode buttons and the pointer, and restarts the idle countdown that fades them away.</summary>
    private void ShowTvControls()
    {
        _tvIdle.Stop();
        if (TvControlsHidden || TvControls.HasAnimatedProperties)
        {
            TvControlsHidden = false;
            TvControls.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            ClearValue(CursorProperty);
        }
        if (!_vm.IsTvMode) { TvControls.BeginAnimation(OpacityProperty, null); return; }
        _tvIdle.Interval = TvIdle;
        _tvIdle.Start();
    }

    private void HideTvControls()
    {
        _tvIdle.Stop();
        if (!_vm.IsTvMode) return;
        if (TvControls.IsMouseOver) { _tvIdle.Start(); return; } // pointer resting on them: still in use
        TvControlsHidden = true;
        TvControls.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(900)) { EasingFunction = new QuadraticEase() });
        Cursor = Cursors.None; // a clean screen: the pointer goes too, until the mouse moves
    }

    /// <summary>Slide-and-fade entrance (runs even with Windows animation effects off).</summary>
    private static void SlideIn(FrameworkElement element, double fromX)
    {
        if (element.RenderTransform is not TranslateTransform t) element.RenderTransform = t = new TranslateTransform();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    /// <summary>Docked side panel on wide windows (resizable via the splitter); full-width panel on narrow ones.</summary>
    private void ApplyPanelLayout()
    {
        // Reset limits first: a MinWidth left from the docked layout would keep an empty column after the panel closes.
        (MainCol.MinWidth, PanelCol.MinWidth, PanelCol.MaxWidth) = (0, 0, double.PositiveInfinity);
        var open = _vm.HasSelection;
        var docked = open && ActualWidth >= NarrowWidth;
        DetailsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = open && !docked ? Visibility.Collapsed : Visibility.Visible;
        Splitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        MainCol.Width = open && !docked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SplitCol.Width = docked ? GridLength.Auto : new GridLength(0);
        if (!open) { PanelCol.Width = new GridLength(0); return; }

        if (docked)
        {
            var max = Math.Max(300, ActualWidth - MinDashboardWidth - 8);
            (MainCol.MinWidth, PanelCol.MinWidth, PanelCol.MaxWidth) = (MinDashboardWidth, 300, max);
            PanelCol.Width = new GridLength(Math.Min(_vm.DetailsPanelWidth, max));
        }
        else PanelCol.Width = new GridLength(1, GridUnitType.Star);
        DetailsPanel.Margin = docked ? new Thickness(0, 14, 0, 0) : new Thickness(12, 8, 12, 0);
        DetailsPanel.BorderThickness = docked ? new Thickness(1, 1, 0, 0) : new Thickness(1, 1, 1, 0);
        DetailsPanel.CornerRadius = docked ? new CornerRadius(12, 0, 0, 0) : new CornerRadius(12, 12, 0, 0);
    }

    // The splitter's last move isn't laid out yet when the drag ends: read the width after layout.
    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e) =>
        AfterLayout(() => { if (_vm.HasSelection) _vm.SaveDetailsPanelWidth(PanelCol.ActualWidth); });

    private void Scrim_Click(object sender, MouseButtonEventArgs e) => _vm.DrawerCancelCommand?.Execute(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.Setup is { IsFirstRun: false } setup)
        {
            setup.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (_vm.IsSetupOpen) { } // first run: setup has to be finished
        else if (e.Key == Key.S && _vm.IsTvMode && Keyboard.Modifiers == ModifierKeys.None)
        {
            _vm.TvAutoScroll = !_vm.TvAutoScroll; // S: scroll on/off
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.IsTvMode)
        {
            _vm.IsTvMode = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (_vm.DrawerCancelCommand ?? (_vm.HasSelection ? _vm.CloseDetailsCommand : null)) is { } close)
        {
            close.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
