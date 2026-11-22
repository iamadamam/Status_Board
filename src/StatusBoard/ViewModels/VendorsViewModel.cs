using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using StatusBoard.Core;

namespace StatusBoard.ViewModels;

/// <summary>
/// The Vendors pane (header "Vendors N"): every vendor, readable and searchable, with what it watches and a link to its status page.
/// Edits a copy of the config; nothing changes until Save.
/// </summary>
public sealed class VendorsViewModel : ObservableObject
{
    public const string AllCategories = "All categories";
    public static readonly string[] ShowChoices = ["All vendors", "Watching", "Not watching"];

    private readonly AppConfig _draft;
    private readonly Func<VendorConfig, Task<ImageSource?>> _logo;
    private string _search = "", _show = ShowChoices[0], _category = AllCategories;
    private AddVendorViewModel? _addVendor;

    public VendorsViewModel(AppConfig draft, IHttpFetcher http, Func<VendorConfig, Task<ImageSource?>> logo, Func<AppConfig, bool> save, Action cancel,
        Func<VendorConfig, Task<VendorStatus>>? fetchAll = null)
    {
        (_draft, _logo, _fetchAll) = (draft, logo, fetchAll);
        // Groups: the editable list, and the choices offered per vendor (the same objects, after a "No group" entry).
        foreach (var g in draft.Groups) AddGroupRow(g.Id, g.Name);
        Vendors = new(draft.Vendors.Select(NewRow));
        foreach (var v in Vendors) v.PropertyChanged += OnVendorChanged;
        Vendors.CollectionChanged += (_, e) =>
        {
            foreach (var v in e.NewItems?.OfType<VendorSettingsViewModel>() ?? []) v.PropertyChanged += OnVendorChanged;
            Notify(nameof(WatchedSummary), nameof(WatchingCount));
        };
        // Under category headings (in the usual category order), alphabetical within each; filtered by search and the two dropdowns.
        VendorsView = new ListCollectionView(Vendors) { Filter = o => o is VendorSettingsViewModel v && Matches(v) };
        VendorsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VendorSettingsViewModel.Category)));
        VendorsView.SortDescriptions.Add(new SortDescription(nameof(VendorSettingsViewModel.CategoryOrder), ListSortDirection.Ascending));
        VendorsView.SortDescriptions.Add(new SortDescription(nameof(VendorSettingsViewModel.Name), ListSortDirection.Ascending));
        CategoryOptions = [AllCategories, .. VendorCategories.All.Where(c => Vendors.Any(v => v.Category == c) || c == VendorCategories.Custom)];

        SaveCommand = new RelayCommand(() => save(Apply()));
        CancelCommand = new RelayCommand(cancel);
        ShowAddVendorCommand = new RelayCommand(() => AddVendor = new AddVendorViewModel(http, Add, () => AddVendor = null));
        WatchShownCommand = new RelayCommand(() => SetShown(true));
        UnwatchShownCommand = new RelayCommand(() => SetShown(false));
        CollapseAllCommand = new RelayCommand(() => { foreach (var v in Vendors) v.IsExpanded = false; });
        // Built-ins back to the default 60; your own vendors are left as they are.
        DefaultSelectionCommand = new RelayCommand(() => { foreach (var v in Vendors.Where(v => !v.IsCustom)) v.Enabled = DefaultVendors.DefaultIds.Contains(v.Config.Id); });
        AddGroupCommand = new RelayCommand(() =>
        {
            var name = NewGroupName.Trim();
            if (name.Length == 0) name = $"Group {Groups.Count + 1}";
            AddGroupRow("g-" + Guid.NewGuid().ToString("N")[..8], name);
            NewGroupName = "";
            IsManagingGroups = true;
        });
        ToggleGroupsCommand = new RelayCommand(() => IsManagingGroups = !IsManagingGroups);
    }

    private readonly Func<VendorConfig, Task<VendorStatus>>? _fetchAll;
    private string _newGroupName = "";
    private bool _managingGroups;

    private VendorSettingsViewModel NewRow(VendorConfig v) => new(v, Remove, _logo, GroupChoices, _fetchAll);

    // ---- Groups ----
    /// <summary>The user's groups, in board order.</summary>
    public ObservableCollection<GroupEditViewModel> Groups { get; } = [];
    /// <summary>"No group" then every group: what each vendor's group dropdown offers.</summary>
    public ObservableCollection<GroupEditViewModel> GroupChoices { get; } = [GroupEditViewModel.None];
    public string NewGroupName { get => _newGroupName; set => Set(ref _newGroupName, value); }
    public bool IsManagingGroups { get => _managingGroups; set => Set(ref _managingGroups, value); }
    public string GroupsSummary => Groups.Count switch { 0 => "No groups yet", 1 => "1 group", var n => $"{n} groups" };
    public ICommand AddGroupCommand { get; }
    public ICommand ToggleGroupsCommand { get; }

    private void AddGroupRow(string id, string name)
    {
        var g = new GroupEditViewModel(id, name);
        g.Move = d => MoveGroup(g, d);
        g.Delete = () => DeleteGroup(g);
        Groups.Add(g);
        GroupChoices.Add(g);
        OnPropertyChanged(nameof(GroupsSummary));
    }

    private void MoveGroup(GroupEditViewModel g, int by)
    {
        var i = Groups.IndexOf(g);
        var j = Math.Clamp(i + by, 0, Groups.Count - 1);
        if (i < 0 || i == j) return;
        Groups.Move(i, j);
        GroupChoices.Move(i + 1, j + 1);
    }

    private void DeleteGroup(GroupEditViewModel g)
    {
        foreach (var v in Vendors.Where(v => v.Group == g)) v.Group = GroupEditViewModel.None; // its vendors become ungrouped
        Groups.Remove(g);
        GroupChoices.Remove(g);
        OnPropertyChanged(nameof(GroupsSummary));
    }

    public ObservableCollection<VendorSettingsViewModel> Vendors { get; }
    public ICollectionView VendorsView { get; }
    public IReadOnlyList<string> CategoryOptions { get; }
    public IReadOnlyList<string> ShowOptions => ShowChoices;
    public string SearchText { get => _search; set { if (Set(ref _search, value)) RefreshView(); } }
    public string ShowFilter { get => _show; set { if (Set(ref _show, value)) RefreshView(); } }
    public string CategoryFilter { get => _category; set { if (Set(ref _category, value)) RefreshView(); } }
    public int WatchingCount => Vendors.Count(v => v.Enabled);
    public string WatchedSummary => $"Watching {WatchingCount} of {Vendors.Count}";
    public bool NothingShown => VendorsView.IsEmpty;

    public AddVendorViewModel? AddVendor { get => _addVendor; private set { if (Set(ref _addVendor, value)) OnPropertyChanged(nameof(IsAddingVendor)); } }
    public bool IsAddingVendor => _addVendor is not null;

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ShowAddVendorCommand { get; }
    public ICommand WatchShownCommand { get; }
    public ICommand UnwatchShownCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand DefaultSelectionCommand { get; }

    private bool Matches(VendorSettingsViewModel v)
    {
        if ((_show == ShowChoices[1] && !v.Enabled) || (_show == ShowChoices[2] && v.Enabled)) return false;
        if (_category != AllCategories && v.Category != _category) return false;
        var q = _search.Trim();
        return q.Length == 0 || v.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || v.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
            || v.Sources.Any(s => s.Label.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshView() { VendorsView.Refresh(); OnPropertyChanged(nameof(NothingShown)); }

    private void SetShown(bool on)
    {
        foreach (var v in VendorsView.Cast<VendorSettingsViewModel>().ToList()) v.Enabled = on;
        if (_show != ShowChoices[0]) RefreshView();
    }

    // Not re-filtered on each tick: a row ticked under "Not watching" would vanish under the cursor.
    private void OnVendorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VendorSettingsViewModel.Enabled)) Notify(nameof(WatchedSummary), nameof(WatchingCount));
    }

    private void Add(VendorConfig vendor)
    {
        _draft.Vendors.Add(vendor);
        var row = NewRow(vendor);
        row.IsExpanded = true;
        Vendors.Add(row);
        AddVendor = null;
        RefreshView();
    }

    private void Remove(VendorSettingsViewModel vm)
    {
        _draft.Vendors.Remove(vm.Config);
        Vendors.Remove(vm);
        RefreshView();
    }

    private AppConfig Apply()
    {
        _draft.Groups = Groups.Select(g => new GroupConfig { Id = g.Id, Name = string.IsNullOrWhiteSpace(g.Name) ? "Group" : g.Name.Trim() }).ToList();
        foreach (var v in Vendors) v.Apply();
        return _draft;
    }
}

/// <summary>A board group being edited (rename, reorder, delete), also used as a choice in each vendor's group dropdown.</summary>
public sealed class GroupEditViewModel : ObservableObject
{
    /// <summary>The "No group" choice.</summary>
    public static readonly GroupEditViewModel None = new("", "No group");
    private string _name;

    public GroupEditViewModel(string id, string name)
    {
        (Id, _name) = (id, name);
        MoveUpCommand = new RelayCommand(() => Move?.Invoke(-1));
        MoveDownCommand = new RelayCommand(() => Move?.Invoke(1));
        DeleteCommand = new RelayCommand(() => Delete?.Invoke());
    }

    public string Id { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public Action<int>? Move { get; set; }
    public Action? Delete { get; set; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand DeleteCommand { get; }
}

/// <summary>One component in a vendor's picker.</summary>
public sealed class ComponentChoiceViewModel(string name, ServiceStatus status, bool selected) : ObservableObject
{
    private bool _selected = selected;
    public string Name => name;
    public ServiceStatus Status => status;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>The components one source lists, for "only the components we use".</summary>
public sealed class SourceComponentsViewModel : ObservableObject
{
    public const int MaxShown = 150;
    private string _search = "";

    public SourceComponentsViewModel(SourceConfig source, SourceResult? result)
    {
        Source = source;
        if (result is null) Message = "Not loaded.";
        else if (!result.Succeeded) Message = $"Couldn't load components: {result.Error}";
        else if (result.Components.Count == 0) Message = "This source doesn't list components, so everything it reports counts.";
        var chosen = source.Components;
        // Keep chosen names the page no longer lists (it may list them again), so saving doesn't silently drop them.
        var listed = (result?.Succeeded == true ? result.Components : [])
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var missing = (chosen ?? []).Where(n => listed.All(c => !string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)))
            .Select(n => new ComponentStatus(n, ServiceStatus.Unknown));
        All = listed.Concat(missing).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ComponentChoiceViewModel(c.Name, c.Status, chosen?.Contains(c.Name, StringComparer.OrdinalIgnoreCase) == true)).ToList();
        foreach (var c in All) c.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectedText));
        Loaded = result?.Succeeded == true;
        SelectShownCommand = new RelayCommand(() => { foreach (var c in Matching()) c.Selected = true; });
        ClearCommand = new RelayCommand(() => { foreach (var c in All) c.Selected = false; });
    }

    public SourceConfig Source { get; }
    public string Label => string.IsNullOrWhiteSpace(Source.Label) ? Source.Url : Source.Label;
    public bool Loaded { get; }
    public string? Message { get; }
    public IReadOnlyList<ComponentChoiceViewModel> All { get; }
    public bool HasComponents => All.Count > 0;
    public string Search { get => _search; set { if (Set(ref _search, value)) Notify(nameof(Shown), nameof(MoreText)); } }
    public IReadOnlyList<ComponentChoiceViewModel> Shown => Matching().Take(MaxShown).ToList();
    public string? MoreText => Matching().Count() is var n && n > MaxShown ? $"{n - MaxShown} more – search to narrow the list" : null;
    public string SelectedText => All.Count(c => c.Selected) is var n && n == 0
        ? $"None of {All.Count} chosen – only incidents that don't name a component will count"
        : $"{n} of {All.Count} chosen";
    public ICommand SelectShownCommand { get; }
    public ICommand ClearCommand { get; }

    private IEnumerable<ComponentChoiceViewModel> Matching()
    {
        var q = _search.Trim();
        return q.Length == 0 ? All : All.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    public List<string> Chosen() => All.Where(c => c.Selected).Select(c => c.Name).ToList();
}

/// <summary>One vendor in the Vendors pane: a readable row that expands to show what it watches and its options.</summary>
public sealed class VendorSettingsViewModel : ObservableObject
{
    private readonly SourceConfig? _aws, _salesforce;
    private readonly Func<VendorConfig, Task<ImageSource?>>? _logoLoader;
    private string _instances;
    private bool _enabled, _expanded, _logoRequested;
    private ImageSource? _logo;

    public VendorSettingsViewModel(VendorConfig vendor, Action<VendorSettingsViewModel> remove, Func<VendorConfig, Task<ImageSource?>>? logo = null,
        IReadOnlyList<GroupEditViewModel>? groupChoices = null, Func<VendorConfig, Task<VendorStatus>>? fetchAll = null)
    {
        Config = vendor;
        _logoLoader = logo;
        _fetchAll = fetchAll;
        GroupChoices = groupChoices ?? [GroupEditViewModel.None];
        _group = GroupChoices.FirstOrDefault(g => g.Id.Length > 0 && string.Equals(g.Id, vendor.GroupId, StringComparison.OrdinalIgnoreCase)) ?? GroupEditViewModel.None;
        _filterComponents = vendor.Sources.Any(s => s.Components is not null);
        LoadComponentsCommand = new RelayCommand(() => _ = LoadComponentsAsync(), () => !IsLoadingComponents && _fetchAll is not null);
        _enabled = vendor.Enabled;
        Sources = vendor.Sources.Count > 1 ? vendor.Sources.Select(s => new SourceSettingsViewModel(s)).ToList() : [];
        foreach (var s in Sources) s.PropertyChanged += (_, _) => Notify(nameof(WatchesText), nameof(RowSummary));
        _aws = vendor.Sources.FirstOrDefault(s => s.Type.Equals("aws-health", StringComparison.OrdinalIgnoreCase));
        if (_aws is not null)
        {
            var selected = AwsHealthProvider.ParseRegions(_aws.GetOption("regions"));
            Regions = AwsRegions.Known.Select(r => new RegionViewModel(r.Code, r.Name, selected is null || selected.Contains(r.Code)))
                .Concat((selected ?? []).Where(c => AwsRegions.Known.All(k => k.Code != c)).Select(c => new RegionViewModel(c, c, true))).ToList();
            foreach (var r in Regions) r.PropertyChanged += (_, _) => Notify(nameof(WatchesText), nameof(RowSummary));
        }
        _salesforce = vendor.Sources.FirstOrDefault(s => s.Type.Equals("salesforce", StringComparison.OrdinalIgnoreCase));
        _instances = _salesforce?.GetOption("instances") ?? "";
        SelectAllCommand = new RelayCommand(() => SelectAll(true));
        SelectNoneCommand = new RelayCommand(() => SelectAll(false));
        RemoveCommand = new RelayCommand(() => remove(this));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        OpenStatusPageCommand = new RelayCommand(() => Links.Open(StatusPageUrl), () => StatusPageUrl is not null);
    }

    public VendorConfig Config { get; }
    public string Name => Config.Name;
    public string Category => VendorCategories.Normalise(Config.Category);
    public int CategoryOrder => VendorCategories.Order(Config.Category);
    public bool IsCustom => Config.Custom;
    public string? StatusPageUrl => !string.IsNullOrWhiteSpace(Config.StatusPageUrl) ? Config.StatusPageUrl
        : Config.Sources.FirstOrDefault()?.Url is { Length: > 0 } u ? u : null;
    /// <summary>For custom vendors: what kind of source and where.</summary>
    public string? Subtitle => Config.Custom && Config.Sources.FirstOrDefault() is { } s ? $"{TypeName(s.Type)} · {s.Url}" : null;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>The row's second line: category (or group), how much of the vendor is watched, and any component filter.</summary>
    public string RowSummary => string.Join("  ·  ", new[]
    {
        _group.Id.Length > 0 ? _group.Name : Category, WatchesText,
        FilterComponents ? (ChosenCount is { } n ? $"{n} component{(n == 1 ? "" : "s")} chosen" : "chosen components only") : null,
    }.OfType<string>());

    // ---- Group ----
    public IReadOnlyList<GroupEditViewModel> GroupChoices { get; }
    private GroupEditViewModel _group;
    public GroupEditViewModel Group
    {
        get => _group;
        set { if (Set(ref _group, value ?? GroupEditViewModel.None)) OnPropertyChanged(nameof(RowSummary)); }
    }

    // ---- Components: "only the ones we use" ----
    private readonly Func<VendorConfig, Task<VendorStatus>>? _fetchAll;
    private bool _filterComponents, _loadingComponents;
    private string? _componentsMessage;
    private IReadOnlyList<SourceComponentsViewModel> _componentSources = [];

    /// <summary>False = every component counts (default); true = only the chosen ones.</summary>
    public bool FilterComponents
    {
        get => _filterComponents;
        set
        {
            if (!Set(ref _filterComponents, value)) return;
            Notify(nameof(RowSummary), nameof(ShowComponentPicker));
            if (value && _componentSources.Count == 0 && _fetchAll is not null) _ = LoadComponentsAsync();
        }
    }
    public bool CanFilterComponents => _fetchAll is not null && !StatusService.IsCheck(Config);
    public bool ShowComponentPicker => _filterComponents && _componentSources.Count > 0;
    public bool IsLoadingComponents { get => _loadingComponents; private set { if (Set(ref _loadingComponents, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string? ComponentsMessage { get => _componentsMessage; private set => Set(ref _componentsMessage, value); }
    public IReadOnlyList<SourceComponentsViewModel> ComponentSources { get => _componentSources; private set { if (Set(ref _componentSources, value)) Notify(nameof(ShowComponentPicker), nameof(RowSummary)); } }
    public ICommand LoadComponentsCommand { get; }
    private int? ChosenCount => _componentSources.Count > 0 ? _componentSources.Sum(s => s.All.Count(c => c.Selected))
        : Config.Sources.Any(s => s.Components is not null) ? Config.Sources.Sum(s => s.Components?.Count ?? 0) : null;

    /// <summary>Reads the vendor live (every source, unfiltered) so you can pick from what it really lists.</summary>
    public async Task LoadComponentsAsync()
    {
        if (_fetchAll is null || IsLoadingComponents) return;
        IsLoadingComponents = true;
        ComponentsMessage = "Loading components from the vendor…";
        try
        {
            var status = await _fetchAll(Config);
            var sources = Config.Sources.Select((s, i) => new SourceComponentsViewModel(s, i < status.Sources.Count ? status.Sources[i] : null)).ToList();
            foreach (var s in sources) foreach (var c in s.All) c.PropertyChanged += (_, _) => OnPropertyChanged(nameof(RowSummary));
            ComponentSources = sources;
            var total = sources.Sum(s => s.All.Count);
            ComponentsMessage = total == 0 ? "This vendor doesn't list components, so everything it reports counts." : null;
        }
        catch (Exception ex) { ComponentsMessage = "Couldn't load components: " + ex.Message; }
        finally { IsLoadingComponents = false; }
    }

    public string WatchesText => Regions.Count > 0 ? $"{Regions.Count(r => r.Selected)} of {Regions.Count} regions"
        : Sources.Count > 0 ? $"{Sources.Count(s => s.Enabled)} of {Sources.Count} services"
        : _salesforce is not null ? (string.IsNullOrWhiteSpace(Instances) ? "All instances" : Instances.Trim())
        : Config.Sources.FirstOrDefault() is { } s ? TypeName(s.Type) : "No sources";

    public string SourceType => Config.Sources.Count == 0 ? "None" : string.Join(", ", Config.Sources.Select(s => TypeName(s.Type)).Distinct());

    public bool IsExpanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value)) OnPropertyChanged(nameof(ExpandGlyph)); }
    }
    /// <summary>Chevron down / up.</summary>
    public string ExpandGlyph => _expanded ? "" : "";

    // ---- Logo (loaded the first time the row is drawn) ----
    public ImageSource? Logo
    {
        get
        {
            if (!_logoRequested && _logoLoader is not null) { _logoRequested = true; _ = LoadLogoAsync(); }
            return _logo;
        }
        set { if (Set(ref _logo, value)) OnPropertyChanged(nameof(HasLogo)); }
    }
    public bool HasLogo => Logo is not null;
    public string Monogram => CardViewModel.MonogramFor(Name);

    private async Task LoadLogoAsync()
    {
        try { if (await _logoLoader!(Config) is { } image) Logo = image; } catch { /* no logo: monogram */ }
    }

    public IReadOnlyList<SourceSettingsViewModel> Sources { get; }
    public IReadOnlyList<RegionViewModel> Regions { get; } = [];
    public bool HasPicker => Sources.Count > 0 || Regions.Count > 0;
    public string PickerTitle => Regions.Count > 0 ? "REGIONS" : "SERVICES";

    /// <summary>Salesforce only: instances to watch ("EU45, NA224"; Tableau pods "PDUKAA"). Blank = all.</summary>
    public bool HasInstances => _salesforce is not null;
    public string Instances { get => _instances; set { if (Set(ref _instances, value)) Notify(nameof(WatchesText), nameof(RowSummary)); } }
    public string InstancesHint => _salesforce is { } s && SalesforceProvider.ProductKey(s.Url) is { } product
        ? $"e.g. PDUKAA. {product.Replace('_', ' ')} pod names are listed on its page at status.salesforce.com."
        : "e.g. EU45, NA224. Find it in Salesforce under Setup › Company Information.";

    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand OpenStatusPageCommand { get; }

    private void SelectAll(bool on)
    {
        foreach (var s in Sources) s.Enabled = on;
        foreach (var r in Regions) r.Selected = on;
    }

    public void Apply()
    {
        Config.Enabled = Enabled;
        Config.GroupId = _group.Id.Length > 0 ? _group.Id : null;
        if (!_filterComponents) foreach (var s in Config.Sources) s.Components = null;
        else foreach (var s in _componentSources.Where(s => s.Loaded || s.HasComponents)) s.Source.Components = s.HasComponents ? s.Chosen() : null;
        foreach (var s in Sources) s.Apply();
        if (_aws is not null) _aws.Options["regions"] = string.Join(",", Regions.Where(r => r.Selected).Select(r => r.Code));
        if (_salesforce is not null) _salesforce.Options["instances"] = string.Join(",", SalesforceProvider.ParseInstances(Instances) ?? []);
    }

    public static string TypeName(string type) => type switch
    {
        "statuspage" => "Statuspage", "feed" => "RSS/Atom feed", "librenms" => "LibreNMS", "microsoft" => "Microsoft status",
        "aws-health" => "AWS Health", "statusio" => "Status.io page", "salesforce" => "Salesforce Trust", _ => type,
    };
}

public sealed class SourceSettingsViewModel(SourceConfig source) : ObservableObject
{
    private bool _enabled = source.Enabled;
    public string Label => source.Label;
    public string Url => source.Url;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public void Apply() => source.Enabled = Enabled;
}

public sealed class RegionViewModel(string code, string name, bool selected) : ObservableObject
{
    private bool _selected = selected;
    public string Code => code;
    public string Display => code == name ? code : $"{name} ({code})";
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public static class AwsRegions
{
    public static readonly (string Code, string Name)[] Known =
    [
        ("global", "Global services"), ("eu-west-2", "London"), ("us-east-1", "N. Virginia"), ("us-east-2", "Ohio"),
        ("us-west-1", "N. California"), ("us-west-2", "Oregon"), ("eu-west-1", "Ireland"), ("eu-central-1", "Frankfurt"),
        ("eu-central-2", "Zurich"), ("eu-west-3", "Paris"), ("eu-north-1", "Stockholm"), ("eu-south-1", "Milan"), ("eu-south-2", "Spain"),
        ("ca-central-1", "Canada Central"), ("sa-east-1", "São Paulo"), ("ap-south-1", "Mumbai"), ("ap-southeast-1", "Singapore"),
        ("ap-southeast-2", "Sydney"), ("ap-northeast-1", "Tokyo"), ("ap-northeast-2", "Seoul"), ("ap-east-1", "Hong Kong"),
        ("me-central-1", "UAE"), ("me-south-1", "Bahrain"), ("af-south-1", "Cape Town"),
        ("us-gov-west-1", "GovCloud (US-West)"), ("us-gov-east-1", "GovCloud (US-East)"),
    ];
}
