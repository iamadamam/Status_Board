using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StatusBoard.Core;

namespace StatusBoard.ViewModels;

/// <summary>One vendor tile and its details panel. Kept across refreshes so selection survives.</summary>
public sealed class CardViewModel(string id, string name, string? statusPageUrl, string? category) : ObservableObject
{
    private const int MaxComponentsShown = 60;
    private VendorStatus? _data;
    private bool _isSelected, _isChecking = true;
    private ImageSource? _logo;

    public string Id { get; } = id;
    public string Name { get; private set; } = name;
    public string? StatusPageUrl { get; private set; } = statusPageUrl;
    public string Category { get; private set; } = VendorCategories.Normalise(category);
    public VendorStatus? Data => _data;

    public ImageSource? Logo { get => _logo; set { if (Set(ref _logo, value)) OnPropertyChanged(nameof(HasLogo)); } }
    public bool HasLogo => _logo is not null;

    /// <summary>Badge text without a logo: initials of up to three words ("Amazon Web Services" → "AWS", "Microsoft 365" → "M365",
    /// "FortiCloud" → "FC").</summary>
    public string Monogram => MonogramFor(Name);

    public static string MonogramFor(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length > 1) return string.Concat(words.Take(3).Select(w => w.All(char.IsDigit) ? w : char.ToUpperInvariant(w[0]).ToString()));
        var caps = new string(words[0].Where(char.IsUpper).Take(3).ToArray());
        return caps.Length >= 2 ? caps : words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
    }

    // ---- Which of the vendor's services are affected (tile line, so a FortiCloud "Major outage" says which product) ----
    private const int MaxServicesOnTile = 3;

    /// <summary>Services with a problem, worst first: per-product sources for multi-source vendors (FortiCloud, Atlassian, M365…),
    /// otherwise the affected components, otherwise the components named by incidents.</summary>
    public IReadOnlyList<ComponentStatus> AffectedServices { get; private set; } = [];
    public bool HasAffectedServices => IsProblem && AffectedServices.Count > 0;
    public ServiceStatus AffectedServicesStatus => AffectedServices.Count > 0 ? AffectedServices[0].Status : Status;
    /// <summary>"FortiSIEM Cloud, FortiZTP +2 more".</summary>
    public string? AffectedServicesText => AffectedServices.Count == 0 ? null
        : string.Join(", ", AffectedServices.Take(MaxServicesOnTile).Select(s => s.Name))
          + (AffectedServices.Count > MaxServicesOnTile ? $" +{AffectedServices.Count - MaxServicesOnTile} more" : "");
    /// <summary>Tooltip: every affected service with its status.</summary>
    public string? AffectedServicesTooltip => AffectedServices.Count == 0 ? null
        : "Affected:\n" + string.Join("\n", AffectedServices.Select(s => $"• {s.Name} – {s.Status.ToDisplay()}"));

    public static IReadOnlyList<ComponentStatus> FindAffectedServices(VendorStatus data)
    {
        var ok = data.Sources.Where(s => s.Succeeded).ToList();
        IEnumerable<ComponentStatus> found;
        if (data.Sources.Count > 1)
            found = ok.Where(s => s.Status.IsProblem()).Select(s => new ComponentStatus(s.SourceLabel, s.Status));
        else
        {
            var comps = ok.SelectMany(s => s.Components).Where(c => c.Status.IsProblem()).ToList();
            found = comps.Count > 0 ? comps
                : ok.SelectMany(s => s.Incidents).Where(i => i.Status.IsProblem())
                    .SelectMany(i => i.AffectedComponents.Select(n => new ComponentStatus(n, i.Status)));
        }
        return found.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ComponentStatus(g.Key, g.Select(c => c.Status).Worst()))
            .OrderByDescending(c => (int)c.Status).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    // ---- How long the current problem has lasted ("for 2h 14m"), beside the status on the tile ----
    public string? DurationText => IncidentDuration.Text(_data, DateTimeOffset.Now);
    /// <summary>Called every minute so the duration keeps counting between refreshes.</summary>
    public void TickMinute() => OnPropertyChanged(nameof(DurationText));

    // ---- The last 24 hours: one entry per half hour, oldest first (null = no data) ----
    private IReadOnlyList<ServiceStatus?> _history = new ServiceStatus?[StatusHistory.Buckets];
    public IReadOnlyList<ServiceStatus?> History => _history;
    public string HistoryTooltip => "Last 24 hours, in half-hour blocks (coloured by the worst status seen)";

    public void SetHistory(IReadOnlyList<ServiceStatus?> buckets)
    {
        if (buckets.SequenceEqual(_history)) return; // redraw only when a block changed
        _history = buckets;
        OnPropertyChanged(nameof(History));
    }

    // ---- A short glow when the status changes, so it's noticed on a wall screen ----
    public static readonly TimeSpan GlowLength = TimeSpan.FromSeconds(2.6);
    private bool _isGlowing;
    private DispatcherTimer? _glowTimer;
    public bool IsGlowing { get => _isGlowing; private set => Set(ref _isGlowing, value); }

    /// <summary>Starts (or restarts) the glow; it switches itself off after <see cref="GlowLength"/>.</summary>
    public void Glow()
    {
        IsGlowing = false; // a change during a glow starts it again
        IsGlowing = true;
        _glowTimer ??= CreateGlowTimer();
        _glowTimer.Stop();
        _glowTimer.Start();
    }

    private DispatcherTimer CreateGlowTimer()
    {
        var t = new DispatcherTimer { Interval = GlowLength };
        t.Tick += (_, _) => { t.Stop(); IsGlowing = false; };
        return t;
    }
    public bool IsChecking { get => _isChecking; private set { if (Set(ref _isChecking, value)) OnPropertyChanged(nameof(StatusOverrideText)); } }
    public ServiceStatus Status => _data?.Status ?? ServiceStatus.Unknown;
    public bool IsProblem => Status.IsProblem();

    // ---- Incident stage (Investigating, Monitoring, In progress…) shown beside the status ----
    /// <summary>Only while there's something to stage: a problem or maintenance.</summary>
    public IncidentStage Stage => _data is { } d && (d.Status.IsProblem() || d.Status == ServiceStatus.Maintenance) ? d.Stage : IncidentStage.None;
    public string? StageText => Stage.ToDisplay();
    public bool StageRecovering => Stage.IsRecovering();
    /// <summary>Plain-English meaning, for the details panel and tooltips.</summary>
    public string? StageDescription => DescribeStage(Stage);

    public static string? DescribeStage(IncidentStage s) => s switch
    {
        IncidentStage.Investigating => "The vendor is investigating; the cause isn't known yet.",
        IncidentStage.Identified => "The cause has been found and a fix is being worked on.",
        IncidentStage.Update => "The vendor has posted an update; the issue is still open.",
        IncidentStage.Monitoring => "A fix has been applied and the vendor is monitoring the results.",
        IncidentStage.Notice => "An advisory or known issue rather than an outage.",
        IncidentStage.Scheduled => "Planned work that hasn't started yet.",
        IncidentStage.InProgress => "Planned work is happening now.",
        IncidentStage.Verifying => "Planned work is finished and being verified.",
        _ => null,
    };

    /// <summary>Instead of "Unknown": before the first result, or when nothing could be reached.</summary>
    public string? StatusOverrideText => _data is null ? "Checking…" : _data.Status == ServiceStatus.Unknown && _data.FailedSourceCount > 0 ? "Couldn't reach" : null;

    public string Headline
    {
        get
        {
            if (_data is null) return "Fetching status…";
            if (_data.Sources.Count == 0) return "No sources enabled";
            if (_data.FailedSourceCount == _data.Sources.Count) return "No source could be reached";
            var (incidents, comps) = (_data.Incidents.Count(), _data.AffectedComponents.Count());
            var text = incidents switch { 0 => "No active incidents", 1 => "1 active incident", _ => $"{incidents} active incidents" };
            return comps == 0 ? text : $"{text} · {(comps == 1 ? "1 component affected" : $"{comps} components affected")}";
        }
    }

    /// <summary>One-line tile subtitle: what's wrong, or a quiet "no incidents".</summary>
    public string ShortHeadline
    {
        get
        {
            if (_data is null) return "Fetching status…";
            if (_data.Sources.Count == 0) return "No sources enabled";
            if (_data.FailedSourceCount == _data.Sources.Count) return "Status page unreachable";
            var incidents = _data.Incidents.ToList();
            if (incidents.Count > 0) return incidents.Count == 1 ? incidents[0].Title : $"{incidents.Count} active incidents";
            if (Note is { } note) return note; // website/ping checks: "Up · 182 ms"
            return _data.AffectedComponents.Count() switch { 0 => "No active incidents", 1 => "1 component affected", var n => $"{n} components affected" };
        }
    }

    /// <summary>A reading from a website/ping check.</summary>
    public string? Note => _data?.Sources.Count == 1 ? _data.Sources[0].Note : null;

    // ---- Board section (the user's groups) ----
    public string? GroupId { get; private set; }
    /// <summary>The group's name, or "Other vendors". Used to group the board into sections.</summary>
    public string SectionTitle { get; private set; } = MainViewModel.OtherVendorsTitle;
    public int SectionOrder { get; private set; } = int.MaxValue;
    /// <summary>A website/ping check rather than a vendor's status page.</summary>
    public bool IsCheck { get; private set; }

    /// <summary>What the board groups by: one section per group (two groups with the same name stay separate), in the user's order.</summary>
    public BoardSection Section { get; private set; } = BoardSection.Other;

    public void SetSection(string? groupId, string title, int order, bool isCheck)
    {
        (GroupId, SectionTitle, SectionOrder, IsCheck) = (groupId, title, order, isCheck);
        Section = new BoardSection(groupId ?? "", title, order);
        Notify(nameof(GroupId), nameof(SectionTitle), nameof(SectionOrder), nameof(IsCheck), nameof(Section));
    }

    // ---- Components tab (details panel) ----
    private bool _showComponents;
    private string _componentSearch = "";
    public bool ShowComponents { get => _showComponents; set { if (Set(ref _showComponents, value)) OnPropertyChanged(nameof(ShowOverview)); } }
    public bool ShowOverview => !_showComponents;
    public ICommand ShowComponentsCommand => new RelayCommand(() => ShowComponents = true);
    public ICommand ShowOverviewCommand => new RelayCommand(() => ShowComponents = false);
    public string ComponentSearch { get => _componentSearch; set { if (Set(ref _componentSearch, value)) Notify(nameof(ComponentsShown), nameof(MoreAllComponentsText)); } }
    /// <summary>Every component the vendor reports (after your component filter), problems first.</summary>
    public IReadOnlyList<ComponentStatus> AllComponents { get; private set; } = [];
    public bool HasAllComponents => AllComponents.Count > 0;
    public string ComponentsTabText => AllComponents.Count == 0 ? "Components" : $"Components ({AllComponents.Count})";
    public IReadOnlyList<ComponentStatus> ComponentsShown => MatchingComponents().Take(MaxComponentsShown * 3).ToList();
    public string? MoreAllComponentsText => MatchingComponents().Count() is var n && n > MaxComponentsShown * 3 ? $"{n - MaxComponentsShown * 3} more – search to narrow the list" : null;
    public bool IsFiltered => _data?.Sources.Any(s => s.Filtered) == true;
    public string ComponentsEmptyText => _data is null ? "Not checked yet." : "This vendor doesn't list components.";

    private IEnumerable<ComponentStatus> MatchingComponents()
    {
        var q = _componentSearch.Trim();
        return q.Length == 0 ? AllComponents : AllComponents.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    public string CheckedText => _data is null ? "" : "Checked " + _data.CheckedAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public string? SourceWarning => _data is not { FailedSourceCount: > 0 } d ? null
        : d.Sources.Count == 1 ? "Source unreachable" : $"{d.FailedSourceCount} of {d.Sources.Count} sources unreachable";

    public IReadOnlyList<IncidentViewModel> Incidents { get; private set; } = [];
    public IReadOnlyList<ComponentStatus> AffectedComponents { get; private set; } = [];
    public string? MoreComponentsText { get; private set; }
    public IReadOnlyList<SourceResult> FailedSources { get; private set; } = [];
    public bool HasIncidents => Incidents.Count > 0;
    public bool HasAffectedComponents => AffectedComponents.Count > 0;
    public bool HasFailedSources => FailedSources.Count > 0;
    public bool HasNothingToShow => _data is not null && !HasIncidents && !HasAffectedComponents && !HasFailedSources;

    public ICommand OpenStatusPageCommand => new RelayCommand(() => Links.Open(StatusPageUrl), () => StatusPageUrl is not null);
    public ICommand OpenLinkCommand => Links.OpenCommand;

    public void Rename(string name, string? statusPageUrl, string? category)
    {
        (Name, StatusPageUrl, Category) = (name, statusPageUrl, VendorCategories.Normalise(category));
        Notify(nameof(Name), nameof(Monogram), nameof(StatusPageUrl), nameof(Category));
    }

    public void MarkChecking() => IsChecking = true;

    public void Update(VendorStatus data)
    {
        _data = data;
        _isChecking = false;
        Incidents = data.Sources.SelectMany(s => s.Incidents.Select(i => (Incident: i, Source: s.SourceLabel)))
            .OrderByDescending(x => x.Incident.Status == ServiceStatus.Unknown ? -1 : (int)x.Incident.Status)
            .ThenByDescending(x => x.Incident.LastUpdated ?? x.Incident.Started)
            .Select(x => new IncidentViewModel(x.Incident, data.Sources.Count > 1 ? x.Source : null)).ToList();
        var comps = data.AffectedComponents.OrderByDescending(c => (int)c.Status).ThenBy(c => c.Name).ToList();
        AffectedComponents = comps.Take(MaxComponentsShown).ToList();
        MoreComponentsText = comps.Count > MaxComponentsShown ? $"+ {comps.Count - MaxComponentsShown} more" : null;
        FailedSources = data.Sources.Where(s => !s.Succeeded).ToList();
        AffectedServices = FindAffectedServices(data);
        AllComponents = data.Sources.Where(s => s.Succeeded)
            .SelectMany(s => s.Components.Select(c => data.Sources.Count > 1 ? c with { Name = $"{s.SourceLabel} · {c.Name}" } : c))
            .OrderBy(c => c.Status == ServiceStatus.Operational ? 1 : 0).ThenByDescending(c => (int)c.Status).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        OnPropertyChanged(string.Empty); // every derived property changed
    }
}

public sealed class IncidentViewModel(Incident incident, string? sourceLabel)
{
    public string Title => incident.Title;
    public string? Summary => incident.Summary;
    public string? Url => incident.Url;
    public ServiceStatus Status => incident.Status;
    public string? StageText => incident.Stage.ToDisplay();
    public bool StageRecovering => incident.Stage.IsRecovering();
    public string? StageDescription => CardViewModel.DescribeStage(incident.Stage);
    public bool HasUrl => !string.IsNullOrEmpty(incident.Url);

    public string Meta => string.Join(" · ", new[]
    {
        sourceLabel,
        incident.Started is { } s ? "Started " + When(s) : null,
        incident.LastUpdated is { } u && u != incident.Started ? "Updated " + When(u) : null,
    }.OfType<string>());

    public string? Components => incident.AffectedComponents switch
    {
        { Count: 0 } => null,
        { Count: <= 6 } c => "Affects: " + string.Join(", ", c),
        var c => $"Affects: {string.Join(", ", c.Take(5))} and {c.Count - 5} more",
    };

    /// <summary>"14:02" today, "16 Nov 10:23" otherwise.</summary>
    public static string When(DateTimeOffset d) =>
        d.LocalDateTime.ToString(d.LocalDateTime.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>One news-ticker item.</summary>
public sealed class TickerItemViewModel(TickerEntry entry, CardViewModel? card)
{
    public TickerEntry Entry => entry;
    /// <summary>Opened on click (null if the vendor was switched off meanwhile).</summary>
    public CardViewModel? Card => card;
    public string VendorName => entry.VendorName;
    public ServiceStatus Status => entry.Status;
    public string? StageText => entry.Stage.ToDisplay();
    public bool StageRecovering => entry.Stage.IsRecovering();
    public string? StageDescription => CardViewModel.DescribeStage(entry.Stage);
    public string Headline => entry.Headline;
    public string? Detail => entry.Detail;
    public string? UpdatedText => entry.Updated is { } u ? "Updated " + IncidentViewModel.When(u) : null;
    /// <summary>Plain text for screen readers and tooltips.</summary>
    public string Text => $"{VendorName}, {StatusChange.Describe(Status, entry.Stage)}: {Headline}" + (Detail is null ? "" : $". {Detail}") + (UpdatedText is null ? "" : $" ({UpdatedText})");
}

/// <summary>A board section header: the user's group, or "Other vendors" last.</summary>
public sealed record BoardSection(string Id, string Title, int Order)
{
    public static readonly BoardSection Other = new("", MainViewModel.OtherVendorsTitle, int.MaxValue);
    public override string ToString() => Title;

    /// <summary>Orders the board's section groups by <see cref="Order"/> (a CollectionView otherwise keeps first-seen order).</summary>
    public sealed class Comparer : System.Collections.IComparer
    {
        private static int OrderOf(object? g) => (g as System.Windows.Data.CollectionViewGroup)?.Name is BoardSection s ? s.Order : int.MaxValue;
        public int Compare(object? x, object? y) => OrderOf(x).CompareTo(OrderOf(y));
    }
}
