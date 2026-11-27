using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StatusBoard.Core;
using StatusBoard.Services;

namespace StatusBoard.ViewModels;

/// <summary>A vendor's status or incident stage changed ("Major outage · Investigating" → "Major outage · Monitoring").</summary>
public sealed record StatusChange(string VendorName, ServiceStatus From, ServiceStatus To, IncidentStage FromStage = IncidentStage.None, IncidentStage ToStage = IncidentStage.None)
{
    public string FromText => Describe(From, FromStage);
    public string ToText => Describe(To, ToStage);
    public static string Describe(ServiceStatus s, IncidentStage stage) =>
        s == ServiceStatus.Operational || stage.ToDisplay() is not { } st ? s.ToDisplay() : $"{s.ToDisplay()} · {st}";
}

/// <summary>The dashboard: refresh loop, cards, summary, counters, "needs attention" chips, news ticker and tile size.</summary>
public sealed class MainViewModel : ObservableObject
{
    public const double Gap = 14, MinCardWidth = 280;
    /// <summary>Tiles are drawn 1.2× their layout size at "100%" (reads well on large screens).</summary>
    public const double BaseTileScale = 1.2;
    public const int ChipLimit = 10;

    private ConfigStore _store;
    private readonly StatusService _service;
    private readonly IHttpFetcher _http;
    private readonly LogoService _logos;
    private readonly DispatcherTimer _timer = new(), _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? _refreshCts;
    private AppConfig _config;
    private bool _isRefreshing, _showAllAffected;
    private DateTimeOffset? _lastRefresh, _nextRefresh;
    private SettingsViewModel? _settings;
    private VendorsViewModel? _vendors;
    private CardViewModel? _selected;
    private string? _banner;
    private double _cardWidth = 320;
    /// <summary>The last 24 hours per vendor, for the strip on each tile (history.json next to the app).</summary>
    private readonly StatusHistory _history;
    private readonly string _historyPath = System.IO.Path.Combine(AppContext.BaseDirectory, StatusHistory.FileName);
    /// <summary>Vendor results that arrived during a refresh, waiting to be shown (filled from background threads).</summary>
    private readonly ConcurrentQueue<(int Generation, VendorStatus Result)> _arrived = new();
    /// <summary>Bumped by each refresh and profile switch: a result from an older refresh is dropped, never shown on a newer board.</summary>
    private int _refreshGeneration;
    /// <summary>False for self-test and snapshot runs, so scripted results never reach the real history.json.</summary>
    private readonly bool _keepHistory;
    /// <summary>How often arriving results are shown during a refresh: at most about four board updates a second.</summary>
    public static readonly TimeSpan ApplyBatchInterval = TimeSpan.FromMilliseconds(250);
    private readonly DispatcherTimer _batchTimer = new() { Interval = ApplyBatchInterval };
    /// <summary>Status changes seen during one refresh, announced once at its end however many batches it took.</summary>
    private readonly ChangeCollector<StatusChange> _changes = new();
    /// <summary>Card positions before status sorting (category, then statusboard.json order).</summary>
    private Dictionary<string, int> _baseOrder = [];

    public MainViewModel(ConfigStore store, AppConfig config, StatusService service, IHttpFetcher http, ProfileStore? profiles = null, bool keepHistory = true)
    {
        (_store, _config, _service, _http, _logos, _banner, _profiles) = (store, config, service, http, new LogoService(http), store.LoadWarning, profiles);
        _keepHistory = keepHistory;
        _history = keepHistory ? StatusHistory.Load(_historyPath, DateTimeOffset.Now) : new StatusHistory();
        _batchTimer.Tick += (_, _) => ApplyArrived();

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsRefreshing);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenVendorsCommand = new RelayCommand(OpenVendors);
        DismissBannerCommand = new RelayCommand(() => Banner = null);
        SelectCardCommand = new RelayCommand(p => Selected = ReferenceEquals(p, Selected) ? null : p as CardViewModel);
        ShowVendorCommand = new RelayCommand(p => { if (p is CardViewModel c) Selected = c; });
        CloseDetailsCommand = new RelayCommand(() => Selected = null);
        ZoomInCommand = new RelayCommand(() => CardScale += 0.1, () => CardScale < ConfigStore.MaxCardScale - 0.001);
        ZoomOutCommand = new RelayCommand(() => CardScale -= 0.1, () => CardScale > ConfigStore.MinCardScale + 0.001);
        ZoomResetCommand = new RelayCommand(() => CardScale = 1.0);
        ToggleAffectedCommand = new RelayCommand(() => { _showAllAffected = !_showAllAffected; SyncAffectedShown(); });

        _timer.Tick += (_, _) => _ = RefreshAsync();
        _checkTimer.Tick += (_, _) => _ = RunChecksAsync();
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(LastRefreshText));
        _clock.Start();
        _minuteTimer.Tick += (_, _) => OnMinuteTick();
        _minuteTimer.Start();
        RebuildCards();
        RestartTimer();
        RefreshProfileRows();
    }

    public AppConfig Config => _config;
    public ObservableCollection<CardViewModel> Cards { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    /// <summary>The Vendors pane (header "Vendors N").</summary>
    public ICommand OpenVendorsCommand { get; }
    public ICommand DismissBannerCommand { get; }
    /// <summary>Card click: toggles the details panel.</summary>
    public ICommand SelectCardCommand { get; }
    /// <summary>Chip/ticker click: opens details (never toggles closed).</summary>
    public ICommand ShowVendorCommand { get; }
    public ICommand CloseDetailsCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomResetCommand { get; }
    public ICommand ToggleAffectedCommand { get; }

    /// <summary>After each refresh: vendors whose status changed between two known states.</summary>
    public event EventHandler<IReadOnlyList<StatusChange>>? StatusesChanged;
    /// <summary>Settings saved (theme and tray need re-applying).</summary>
    public event EventHandler? ConfigApplied;
    public event EventHandler? SummaryChanged;

    // ---- State ----
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { if (Set(ref _isRefreshing, value)) { OnPropertyChanged(nameof(RefreshButtonText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string RefreshButtonText => IsRefreshing ? "Refreshing…" : "Refresh now";

    public SettingsViewModel? Settings { get => _settings; private set { if (Set(ref _settings, value)) Notify(nameof(IsSettingsOpen), nameof(IsDrawerOpen), nameof(DrawerCancelCommand)); } }
    public bool IsSettingsOpen => _settings is not null;

    public VendorsViewModel? Vendors { get => _vendors; private set { if (Set(ref _vendors, value)) Notify(nameof(IsVendorsOpen), nameof(IsDrawerOpen), nameof(DrawerCancelCommand)); } }
    public bool IsVendorsOpen => _vendors is not null;
    /// <summary>Either drawer (they are modal and never open together).</summary>
    public bool IsDrawerOpen => IsSettingsOpen || IsVendorsOpen;
    public ICommand? DrawerCancelCommand => (ICommand?)_settings?.CancelCommand ?? _vendors?.CancelCommand;

    public CardViewModel? Selected
    {
        get => _selected;
        set
        {
            var old = _selected;
            if (!Set(ref _selected, value)) return;
            if (old is not null) old.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selected is not null;
    public double DetailsPanelWidth => _config.DetailsPanelWidth;

    /// <summary>Remembers the panel width after the splitter is dragged.</summary>
    public void SaveDetailsPanelWidth(double width)
    {
        if (Math.Abs(width - _config.DetailsPanelWidth) < 1) return;
        _config.DetailsPanelWidth = Math.Round(width);
        _store.TrySave(_config);
    }

    /// <summary>Non-fatal problems such as an unreadable config or a failed save.</summary>
    public string? Banner { get => _banner; private set { if (Set(ref _banner, value)) OnPropertyChanged(nameof(HasBanner)); } }
    public bool HasBanner => !string.IsNullOrEmpty(_banner);
    public bool HasNoVendors => Cards.Count == 0;

    // ---- Tile layout and size ----
    public double CardWidth { get => _cardWidth; private set => Set(ref _cardWidth, value); }

    /// <summary>Called by the view with the card area's width (in tile units).</summary>
    public void UpdateLayout(double availableWidth)
    {
        if (availableWidth <= 0) return;
        var cols = Math.Clamp((int)((availableWidth + Gap) / (MinCardWidth + Gap)), 1, Math.Max(1, Cards.Count));
        CardWidth = Math.Floor((availableWidth + Gap) / cols - Gap - 0.5); // half a pixel of slack so rounding never wraps early
    }

    /// <summary>Tile size multiplier, remembered in statusboard.json.</summary>
    public double CardScale
    {
        get => _config.CardScale;
        set
        {
            var v = Math.Clamp(Math.Round(value, 2), ConfigStore.MinCardScale, ConfigStore.MaxCardScale);
            if (Math.Abs(v - _config.CardScale) < 0.001) return;
            _config.CardScale = v;
            _store.TrySave(_config);
            Notify(nameof(CardScale), nameof(CardScaleText), nameof(TileScale));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string CardScaleText => $"{Math.Round(CardScale * 100)}%";
    public double TileScale => CardScale * BaseTileScale * (IsTvMode ? TvBoost : 1);

    // ---- Summary ----
    public ServiceStatus OverallStatus { get; private set; } = ServiceStatus.Unknown;
    public string SummaryHeadline { get; private set; } = "Checking vendor status…";
    public string SummaryDetail { get; private set; } = "";
    public int WatchingCount => Cards.Count;
    public int OperationalCount { get; private set; }
    public int ProblemCount { get; private set; }
    public int MaintenanceCount { get; private set; }
    public int MajorCount { get; private set; }
    public int PartialCount { get; private set; }
    public int DegradedCount { get; private set; }
    /// <summary>Vendors whose sources all failed on the last refresh.</summary>
    public int UnreachableCount { get; private set; }

    public string LastRefreshText
    {
        get
        {
            var ago = DateTimeOffset.Now - _lastRefresh;
            var first = IsRefreshing ? "Refreshing…" : _lastRefresh is not { } last ? "Not checked yet"
                : "Updated " + (ago!.Value.TotalMinutes < 1 ? "just now" : ago.Value.TotalMinutes < 60 ? $"{(int)ago.Value.TotalMinutes} min ago" : last.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture));
            var second = _config.RefreshIntervalMinutes <= 0 ? "auto-refresh off"
                : IsRefreshing || _nextRefresh is not { } next ? null
                : (next - DateTimeOffset.Now).TotalSeconds < 60 ? "next in <1 min" : $"next in {(int)Math.Ceiling((next - DateTimeOffset.Now).TotalMinutes)} min";
            return second is null ? first : $"{first} · {second}";
        }
    }

    /// <summary>Services needing attention, worst first: problems, then maintenance, then unreachable.</summary>
    public ObservableCollection<CardViewModel> Affected { get; } = [];
    /// <summary>The chips shown: the worst <see cref="ChipLimit"/> unless expanded.</summary>
    public ObservableCollection<CardViewModel> AffectedShown { get; } = [];
    public bool HasAffected => Affected.Count > 0;
    public bool HasMoreAffected => Affected.Count > ChipLimit;
    public string MoreAffectedText => _showAllAffected ? "Show fewer" : $"+{Affected.Count - ChipLimit} more";

    private void UpdateSummary()
    {
        var checkedCards = Cards.Where(c => c.Data is not null).ToList();
        var problems = checkedCards.Where(c => c.Status.IsProblem()).OrderByDescending(c => (int)c.Status).ToList();
        var maint = checkedCards.Where(c => c.Status == ServiceStatus.Maintenance).ToList();
        var unreachable = checkedCards.Where(c => c.Status == ServiceStatus.Unknown).ToList();

        (OverallStatus, SummaryHeadline, SummaryDetail) =
            Cards.Count == 0 ? (ServiceStatus.Unknown, "No vendors enabled", "Open Settings to choose which vendors to watch.")
            : checkedCards.Count == 0 ? (ServiceStatus.Unknown, "Checking vendor status…", "")
            // First load still arriving and nothing wrong yet: don't call it "all operational" (or "offline") on a handful of answers.
            : IsRefreshing && checkedCards.Count < Cards.Count && problems.Count == 0
                ? (ServiceStatus.Unknown, "Checking vendor status…", $"{checkedCards.Count} of {Cards.Count} checked")
            : problems.Count > 0 ? (problems[0].Status, problems.Count == 1 ? $"{problems[0].Name} is reporting issues" : $"{problems.Count} services reporting issues", "")
            : unreachable.Count == checkedCards.Count ? (ServiceStatus.Unknown, "Couldn't reach any status source",
                "Check your internet connection." + (_config.RefreshIntervalMinutes > 0 ? " Will retry automatically." : ""))
            : maint.Count > 0 ? (ServiceStatus.Maintenance, "Maintenance in progress", "")
            : (ServiceStatus.Operational, "All systems operational", "");
        if (unreachable.Count > 0 && unreachable.Count < checkedCards.Count)
        {
            var note = unreachable.Count == 1 ? $"{unreachable[0].Name} couldn't be checked" : $"{unreachable.Count} services couldn't be checked";
            SummaryDetail = SummaryDetail.Length == 0 ? note : SummaryDetail + "  ·  " + note;
        }

        OperationalCount = checkedCards.Count(c => c.Status == ServiceStatus.Operational);
        (ProblemCount, MaintenanceCount) = (problems.Count, maint.Count);
        MajorCount = problems.Count(c => c.Status == ServiceStatus.MajorOutage);
        PartialCount = problems.Count(c => c.Status == ServiceStatus.PartialOutage);
        DegradedCount = problems.Count(c => c.Status == ServiceStatus.Degraded);
        UnreachableCount = unreachable.Count;
        Sync(Affected, problems.Concat(maint).Concat(unreachable.Count < checkedCards.Count ? unreachable : []).ToList());
        SyncAffectedShown();

        Notify(nameof(HasAffected), nameof(WatchingCount), nameof(OperationalCount), nameof(ProblemCount), nameof(MaintenanceCount),
            nameof(MajorCount), nameof(PartialCount), nameof(DegradedCount), nameof(UnreachableCount), nameof(OverallStatus), nameof(SummaryHeadline),
            nameof(SummaryDetail), nameof(LastRefreshText));
        SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncAffectedShown()
    {
        Sync(AffectedShown, (_showAllAffected ? Affected : Affected.Take(ChipLimit)).ToList());
        Notify(nameof(HasMoreAffected), nameof(MoreAffectedText));
    }

    /// <summary>Replaces a collection's contents only if they differ (avoids rebuilding chips on every refresh).</summary>
    private static void Sync<T>(ObservableCollection<T> target, List<T> items)
    {
        if (items.SequenceEqual(target)) return;
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    // ---- News ticker ----
    /// <summary>Replaced (not edited) whenever anything in it changes.</summary>
    public IReadOnlyList<TickerItemViewModel> TickerItems { get; private set; } = [];
    public bool ShowTicker => _config.NewsTicker && TickerItems.Count > 0;

    private void UpdateTicker()
    {
        var entries = NewsTicker.Build(Cards.Select(c => c.Data).OfType<VendorStatus>());
        if (!entries.SequenceEqual(TickerItems.Select(t => t.Entry)))
        {
            TickerItems = entries.Select(e => new TickerItemViewModel(e, Cards.FirstOrDefault(c => c.Id == e.VendorId))).ToList();
            OnPropertyChanged(nameof(TickerItems));
        }
        OnPropertyChanged(nameof(ShowTicker));
    }

    // ---- Refresh ----
    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        OnPropertyChanged(nameof(LastRefreshText));
        _timer.Stop();
        _refreshCts = new CancellationTokenSource();
        try
        {
            foreach (var c in Cards) c.MarkChecking();
            // Each vendor is shown as soon as it answers (in small batches), instead of all at once after the slowest.
            _batchTimer.Start();
            var generation = ++_refreshGeneration;
            await _service.RefreshAsync(_config, _refreshCts.Token, r => _arrived.Enqueue((generation, r)));
            _batchTimer.Stop();
            ApplyArrived();
            _lastRefresh = DateTimeOffset.Now;
            OnPropertyChanged(nameof(LastRefreshText));
            AnnounceChanges();
            SaveHistory();
        }
        catch (OperationCanceledException)
        {
            _arrived.Clear(); // a cancelled refresh (profile switch) shows nothing more and announces nothing
            _changes.Drain();
        }
        finally
        {
            _batchTimer.Stop();
            IsRefreshing = false;
            UpdateSummary(); // the final verdict, now that the refresh is over
            _refreshCts?.Dispose();
            _refreshCts = null;
            RestartTimer();
        }
    }

    /// <summary>Applies results to the cards, re-sorts, and updates summary, counters and ticker. The self-test feeds it scripted results.</summary>
    public void ApplyResults(IReadOnlyList<VendorStatus> results) => ApplyResults(results, countsAsRefresh: true);

    /// <param name="countsAsRefresh">False for the minute-by-minute website/ping checks, which mustn't move "Updated …" for every vendor.</param>
    public void ApplyResults(IReadOnlyList<VendorStatus> results, bool countsAsRefresh)
    {
        ApplyBatch(results);
        if (countsAsRefresh) _lastRefresh = DateTimeOffset.Now;
        if (!IsRefreshing) AnnounceChanges(); // during a refresh, its own end announces everything once
    }

    /// <summary>Shows whatever has arrived since the last batch.</summary>
    private void ApplyArrived()
    {
        var batch = new List<VendorStatus>();
        while (_arrived.TryDequeue(out var item)) if (item.Generation == _refreshGeneration) batch.Add(item.Result);
        if (batch.Count > 0) ApplyBatch(batch);
    }

    /// <summary>Updates the cards for some vendors, then the order, summary, counters and ticker. Changes are collected, not announced.</summary>
    private void ApplyBatch(IReadOnlyList<VendorStatus> results)
    {
        var now = DateTimeOffset.Now;
        foreach (var r in results)
        {
            if (Cards.FirstOrDefault(c => c.Id == r.VendorId) is not { } card) continue; // settings changed mid-refresh
            if (StatusChanges.IsStale(card.Data?.CheckedAt, r.CheckedAt)) continue; // a slow check overtaken by a newer refresh
            var old = card.Data?.Status;
            var oldStage = card.Data?.Stage ?? IncidentStage.None;
            card.Update(r);
            // Only transitions between known states: an unreachable feed is not a vendor incident. A notification also covers
            // an incident moving on at the same status (Investigating → Monitoring); the tile glows only when the status changed.
            if (StatusChanges.ShouldNotify(old, oldStage, r.Status, r.Stage)) _changes.Add(new StatusChange(r.VendorName, old!.Value, r.Status, oldStage, r.Stage));
            if (StatusChanges.ShouldGlow(old, r.Status)) card.Glow();
            _history.Record(r.VendorId, r.Status, r.CheckedAt, StatusHistory.GapFor(_config.RefreshIntervalMinutes));
            card.SetHistory(_history.BucketsFor(r.VendorId, now));
        }
        SortProblemsFirst();
        UpdateSummary();
        UpdateTicker();
    }

    private void AnnounceChanges()
    {
        var changes = _changes.Drain();
        if (changes.Count > 0) StatusesChanged?.Invoke(this, changes);
    }

    private void SaveHistory()
    {
        _history.Prune(DateTimeOffset.Now);
        if (_keepHistory) _history.TrySave(_historyPath);
    }

    /// <summary>Every tile's strip moves along as the half hours pass, and durations keep counting.</summary>
    private void RefreshTimeBasedText()
    {
        var now = DateTimeOffset.Now;
        foreach (var c in Cards)
        {
            c.TickMinute();
            c.SetHistory(_history.BucketsFor(c.Id, now));
        }
    }

    private void RestartTimer()
    {
        _timer.Stop();
        _nextRefresh = null;
        if (_config.RefreshIntervalMinutes > 0)
        {
            _timer.Interval = TimeSpan.FromMinutes(_config.RefreshIntervalMinutes);
            _nextRefresh = DateTimeOffset.Now + _timer.Interval;
            _timer.Start();
        }
        OnPropertyChanged(nameof(LastRefreshText));
    }

    public void Shutdown()
    {
        SaveHistory();
        _batchTimer.Stop();
        _timer.Stop();
        _clock.Stop();
        _minuteTimer.Stop();
        _checkTimer.Stop();
        _refreshCts?.Cancel();
    }

    // ---- Groups (optional board sections) ----
    public const string OtherVendorsTitle = "Other vendors";
    /// <summary>Position of each group in the user's order; ungrouped vendors come last.</summary>
    private Dictionary<string, int> _groupOrder = new(StringComparer.OrdinalIgnoreCase);
    public bool HasGroups => _config.Groups.Count > 0;
    /// <summary>The board's items: grouped into headed sections when the user has groups.</summary>
    public System.ComponentModel.ICollectionView CardsView { get; private set; } = null!;

    private int GroupRank(CardViewModel c) => c.GroupId is { } g && _groupOrder.TryGetValue(g, out var i) ? i : int.MaxValue;

    private void RebuildView()
    {
        var view = new System.Windows.Data.ListCollectionView(Cards);
        if (HasGroups) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(CardViewModel.Section)) { CustomSort = new BoardSection.Comparer() });
        CardsView = view;
        Notify(nameof(CardsView), nameof(HasGroups));
    }

    // ---- Tiles or list ----
    public bool IsListView
    {
        get => _config.BoardView == "List";
        set
        {
            if (value == IsListView) return;
            _config.BoardView = value ? "List" : "Tiles";
            _store.TrySave(_config);
            OnPropertyChanged();
        }
    }
    public ICommand ShowTilesCommand => new RelayCommand(() => IsListView = false);
    public ICommand ShowListCommand => new RelayCommand(() => IsListView = true);

    // ---- TV / wall-board mode ----
    private bool _tv;
    /// <summary>Full screen, read-only, large tiles, slowly auto-scrolling (F11 or --tv; Esc leaves).</summary>
    public bool IsTvMode { get => _tv; set { if (Set(ref _tv, value)) { if (value) { Selected = null; Settings = null; Vendors = null; } OnPropertyChanged(nameof(TileScale)); } } }
    public ICommand ToggleTvCommand => new RelayCommand(() => IsTvMode = !IsTvMode);
    // ---- Header clock: today's date and the time, updated on the minute. The timer wakes just after each minute turns (and at
    // least every 15 s, so it catches up quickly after sleep or a clock change) instead of every second. ----
    private readonly DispatcherTimer _minuteTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _shownMinute = -1;
    public string ClockText => DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
    /// <summary>"Sunday 22 November 2026" in the user's language.</summary>
    public string DateText => DateTime.Now.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
    public string ClockSpoken => $"{DateText}, {ClockText}";

    private void OnMinuteTick()
    {
        var now = DateTime.Now;
        var minute = now.Hour * 60 + now.Minute;
        var toNext = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(30);
        _minuteTimer.Interval = toNext < TimeSpan.FromSeconds(15) ? toNext : TimeSpan.FromSeconds(15);
        UpdateYearProgress(now);
        if (minute == _shownMinute) return;
        _shownMinute = minute;
        Notify(nameof(ClockText), nameof(DateText), nameof(ClockSpoken));
        RefreshTimeBasedText();
    }

    // ---- Year progress: every few minutes the clock steps aside for 15 s to show how far through the year we are ----
    private bool _showYear;
    /// <summary>True while the header shows the year-progress bar instead of the clock (Settings: on/off and how often).</summary>
    public bool ShowYearProgress { get => _showYear; private set => Set(ref _showYear, value); }
    public double YearFraction => YearProgress.Fraction(DateTime.Now);
    /// <summary>"90.6%".</summary>
    public string YearPercentText => (YearFraction * 100).ToString("0.0", CultureInfo.CurrentCulture) + "%";
    /// <summary>"of 2026 · 34 days left".</summary>
    public string YearDetailText
    {
        get
        {
            var now = DateTime.Now;
            var left = (int)Math.Ceiling((new DateTime(now.Year + 1, 1, 1) - now).TotalDays) - 1;
            return $"of {now.Year} · " + (left <= 0 ? "last day" : left == 1 ? "1 day left" : $"{left} days left");
        }
    }
    public string YearSpoken => $"{YearPercentText} {YearDetailText}";
    /// <summary>The bar's filled and empty parts, as star widths.</summary>
    public System.Windows.GridLength YearDoneLength => new(YearFraction, System.Windows.GridUnitType.Star);
    public System.Windows.GridLength YearLeftLength => new(1 - YearFraction, System.Windows.GridUnitType.Star);

    private void UpdateYearProgress(DateTime now)
    {
        var show = _config.YearProgressCycle && YearProgress.IsShowing(now, _config.YearProgressIntervalMinutes);
        if (show && !_showYear) Notify(nameof(YearFraction), nameof(YearPercentText), nameof(YearDetailText), nameof(YearSpoken), nameof(YearDoneLength), nameof(YearLeftLength));
        ShowYearProgress = show;
    }
    /// <summary>Extra size in TV mode, on top of the user's tile size.</summary>
    public const double TvBoost = 1.25;

    /// <summary>TV mode scrolls by itself; off keeps the board still (you can still scroll with the wheel). Saved per profile.</summary>
    public bool TvAutoScroll
    {
        get => _config.TvAutoScroll;
        set
        {
            if (value == _config.TvAutoScroll) return;
            _config.TvAutoScroll = value;
            _store.TrySave(_config);
            Notify(nameof(TvAutoScroll), nameof(TvScrollText));
        }
    }
    public string TvScrollText => TvAutoScroll ? "Auto-scroll on" : "Auto-scroll off";
    public ICommand ToggleTvScrollCommand => new RelayCommand(() => TvAutoScroll = !TvAutoScroll);

    // ---- Website / ping checks: every minute, apart from the vendor refresh ----
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _checking;

    private void RestartCheckTimer()
    {
        _checkTimer.Stop();
        if (_config.Vendors.Any(v => v.Enabled && StatusService.IsCheck(v))) _checkTimer.Start();
    }

    public async Task RunChecksAsync()
    {
        if (_checking || IsRefreshing) return;
        _checking = true;
        try
        {
            var checks = new AppConfig { Vendors = _config.Vendors.Where(v => v.Enabled && StatusService.IsCheck(v)).ToList() };
            if (checks.Vendors.Count > 0) ApplyResults(await _service.RefreshAsync(checks, CancellationToken.None), countsAsRefresh: false);
        }
        catch (OperationCanceledException) { }
        finally { _checking = false; }
    }

    // ---- Ordering ----
    /// <summary>Worst first: outages, degraded, maintenance, unreachable, then healthy (and not yet checked).</summary>
    public static int SortRank(CardViewModel c) => c.Data is null ? 5 : c.Status switch
    {
        ServiceStatus.MajorOutage => 0, ServiceStatus.PartialOutage => 1, ServiceStatus.Degraded => 2,
        ServiceStatus.Maintenance => 3, ServiceStatus.Unknown => 4, _ => 5,
    };

    /// <summary>Problems on top. Moves only cards that change place, so selection and scroll survive a refresh.</summary>
    private void SortProblemsFirst()
    {
        // Your groups first (in your order), each worst-first; then everything else, worst-first.
        var desired = Cards.OrderBy(GroupRank).ThenBy(SortRank).ThenBy(c => _baseOrder.GetValueOrDefault(c.Id, int.MaxValue)).ToList();
        for (var i = 0; i < desired.Count; i++)
            if (Cards.IndexOf(desired[i]) is var at && at != i) Cards.Move(at, i);
    }

    // ---- Config ----
    private void RebuildCards()
    {
        // Related services together (by category), then statusboard.json order.
        var enabled = _config.Vendors.Select((v, i) => (v, i)).Where(x => x.v.Enabled)
            .OrderBy(x => VendorCategories.Order(x.v.Category)).ThenBy(x => x.i).Select(x => x.v).ToList();
        var existing = Cards.ToDictionary(c => c.Id);
        var selectedId = Selected?.Id;
        Cards.Clear();
        _groupOrder = _config.Groups.Select((g, i) => (g.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.OrdinalIgnoreCase);
        var groupNames = _config.Groups.ToDictionary(g => g.Id, g => g.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var v in enabled)
        {
            if (existing.TryGetValue(v.Id, out var card)) card.Rename(v.Name, v.StatusPageUrl, v.Category);
            else card = new CardViewModel(v.Id, v.Name, v.StatusPageUrl, v.Category);
            var gid = v.GroupId is { } g && groupNames.ContainsKey(g) ? g : null;
            card.SetSection(gid, gid is null ? OtherVendorsTitle : groupNames[gid], gid is null ? int.MaxValue : _groupOrder[gid], StatusService.IsCheck(v));
            Cards.Add(card);
        }
        RebuildView();
        var now = DateTimeOffset.Now;
        foreach (var c in Cards) c.SetHistory(_history.BucketsFor(c.Id, now)); // strips filled straight away, not on the next result
        Selected = Cards.FirstOrDefault(c => c.Id == selectedId);
        _baseOrder = Cards.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        SortProblemsFirst();
        OnPropertyChanged(nameof(HasNoVendors));
        UpdateSummary();
        UpdateTicker();
        RestartCheckTimer();
        _ = LoadLogosAsync();
    }

    public async Task LoadLogosAsync()
    {
        _logos.Prune(_config.Vendors.Select(v => v.Id));
        if (!IsSettingsOpen) LogoImporter.PruneUnused(_config.Vendors.Select(v => v.Logo)); // an open Settings may hold a just-uploaded logo
        await Task.WhenAll(_config.Vendors.Where(v => v.Enabled).Select(async v =>
        {
            var image = await _logos.GetAsync(v, CancellationToken.None);
            // A logo that already failed this run comes back null: never wipe one that's shown.
            if (image is not null && Cards.FirstOrDefault(c => c.Id == v.Id) is { } card) card.Logo = image;
        }));
    }

    /// <summary>Logo for any vendor (watched or not), from the cache or the vendor's site: previews in Settings and the Vendors pane.</summary>
    private Task<ImageSource?> LogoFor(VendorConfig v) =>
        Cards.FirstOrDefault(c => c.Id == v.Id) is { Logo: { } shown } && _config.Vendors.FirstOrDefault(x => x.Id == v.Id)?.Logo == v.Logo
            ? Task.FromResult<ImageSource?>(shown)
            : _logos.GetAsync(v, CancellationToken.None);

    private void OpenSettings()
    {
        Vendors = null;
        Settings = new SettingsViewModel(Clone(_config), _store.Path, LogoFor, SaveSettings, () => Settings = null);
    }

    private void OpenVendors()
    {
        Settings = null;
        Vendors = new VendorsViewModel(Clone(_config), _http, LogoFor, SaveSettings, () => Vendors = null,
            v => _service.RefreshVendorAsync(v, CancellationToken.None, applyFilters: false, sourcesFilter: _ => true));
    }

    private bool SaveSettings(AppConfig updated)
    {
        try { _store.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner = $"Couldn't save settings to {_store.Path}: {ex.Message}. Is the folder read-only?";
            return false;
        }
        _config = updated;
        Settings = null;
        Vendors = null;
        Banner = null;
        RebuildCards();
        RestartTimer();
        ConfigApplied?.Invoke(this, EventArgs.Empty);
        _ = RefreshAsync();
        return true;
    }

    private static AppConfig Clone(AppConfig c) => ConfigStore.Deserialize(ConfigStore.Serialize(c));

    // ---- Profiles ----
    private readonly ProfileStore? _profiles;
    private SetupViewModel? _setup;
    public ProfileStore? ProfileStore => _profiles;
    public string ProfileName => _profiles?.Active?.Name ?? ProfileStore.DefaultName;
    public ObservableCollection<ProfileRowViewModel> ProfileRows { get; } = [];
    /// <summary>Raised to show a theme without saving it (the setup wizard's theme step); null = back to the saved theme.</summary>
    public event EventHandler<string?>? ThemePreview;

    /// <summary>First-time setup, or "New profile": name → vendors → theme → done.</summary>
    public SetupViewModel? Setup { get => _setup; private set { if (Set(ref _setup, value)) OnPropertyChanged(nameof(IsSetupOpen)); } }
    public bool IsSetupOpen => _setup is not null;
    public ICommand NewProfileCommand => new RelayCommand(() => StartSetup(firstRun: false), () => _profiles is not null);
    public ICommand ManageProfilesCommand => new RelayCommand(OpenSettings);

    public void StartSetup(bool firstRun)
    {
        if (_profiles is null) return;
        (Settings, Vendors, Selected, IsTvMode) = (null, null, null, false);
        Setup = new SetupViewModel(firstRun, _config.Theme, LogoFor, theme => ThemePreview?.Invoke(this, theme), FinishSetup,
            () => { Setup = null; ThemePreview?.Invoke(this, null); });
    }

    private bool FinishSetup(string name, AppConfig config)
    {
        try
        {
            var entry = _profiles!.Create(name, config);
            Setup = null;
            SwitchTo(entry);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner = $"Couldn't create the profile next to the app ({ex.Message}). Is the folder read-only?";
            return false;
        }
    }

    public void SwitchProfile(ProfileEntry p)
    {
        if (_profiles is null || !_profiles.Profiles.Contains(p)) return;
        _profiles.SetActive(p);
        SwitchTo(p);
    }

    /// <summary>Loads a profile's settings and rebuilds the board around them (theme, tray and refresh included).</summary>
    private void SwitchTo(ProfileEntry p)
    {
        var store = _profiles!.StoreFor(p);
        var config = store.Load();
        (Settings, Vendors, Selected) = (null, null, null);
        _refreshCts?.Cancel();
        (_store, _config, Banner) = (store, config, store.LoadWarning);
        _refreshGeneration++; // anything the old profile's refresh still delivers is dropped
        _arrived.Clear();
        foreach (var c in Cards) c.Logo = null;
        Cards.Clear(); // a fresh board: no state carried over from the other profile
        RebuildCards();
        RestartTimer();
        Notify(nameof(CardScale), nameof(CardScaleText), nameof(TileScale), nameof(IsListView), nameof(ProfileName), nameof(DetailsPanelWidth), nameof(TvAutoScroll), nameof(TvScrollText));
        RefreshProfileRows();
        ConfigApplied?.Invoke(this, EventArgs.Empty);
        _ = RefreshAfterSwitchAsync();
    }

    private async Task RefreshAfterSwitchAsync()
    {
        while (IsRefreshing) await Task.Delay(50); // the cancelled refresh finishing
        await RefreshAsync();
    }

    public void RenameProfile(ProfileEntry p, string name)
    {
        _profiles?.Rename(p, name);
        Notify(nameof(ProfileName));
    }

    public void DeleteProfile(ProfileEntry p)
    {
        if (_profiles is null) return;
        var wasActive = _profiles.Active == p;
        if (!_profiles.Delete(p)) return;
        if (wasActive && _profiles.Active is { } next) SwitchTo(next);
        RefreshProfileRows();
    }

    public void RefreshProfileRows()
    {
        ProfileRows.Clear();
        if (_profiles is null) return;
        foreach (var p in _profiles.Profiles)
            ProfileRows.Add(new ProfileRowViewModel(p, p == _profiles.Active, _profiles.Profiles.Count > 1, this));
        Notify(nameof(ProfileName));
    }
}

/// <summary>A profile in Settings: rename in place, switch to it, or delete it.</summary>
public sealed class ProfileRowViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _name;

    public ProfileRowViewModel(ProfileEntry entry, bool active, bool canDelete, MainViewModel main)
    {
        (Entry, IsActive, CanDelete, _main, _name) = (entry, active, canDelete, main, entry.Name);
        SwitchCommand = new RelayCommand(() => _main.SwitchProfile(Entry), () => !IsActive);
        DeleteCommand = new RelayCommand(() => _main.DeleteProfile(Entry), () => CanDelete);
    }

    public ProfileEntry Entry { get; }
    public bool IsActive { get; }
    public bool CanDelete { get; }
    /// <summary>Renames when the box loses focus; shows the name actually kept (trimmed, made unique).</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            _main.RenameProfile(Entry, value);
            if (_name != Entry.Name) { _name = Entry.Name; OnPropertyChanged(); }
        }
    }
    public string FileName => Entry.File;
    public ICommand SwitchCommand { get; }
    public ICommand DeleteCommand { get; }
}
