using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using StatusBoard.Core;

namespace StatusBoard.ViewModels;

/// <summary>
/// First-time setup (and "New profile"): 1 name → 2 vendors from the built-in list (none ticked to start) → 3 theme, previewed live
/// → 4 done. Nothing is written until Finish.
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    public const int NameStep = 0, VendorsStep = 1, ThemeStep = 2, DoneStep = 3;
    public static readonly string[] Steps = ["Name", "Vendors", "Theme", "Done"];

    private readonly AppConfig _config = AppConfig.CreateDefault();
    private readonly Action<string?> _preview;
    private readonly Func<string, AppConfig, bool> _finish;
    private int _step;
    private string _name = "", _search = "", _theme;
    private string? _error;

    public SetupViewModel(bool firstRun, string currentTheme, Func<VendorConfig, Task<ImageSource?>> logo, Action<string?> previewTheme,
        Func<string, AppConfig, bool> finish, Action cancel)
    {
        (IsFirstRun, _preview, _finish, _theme) = (firstRun, previewTheme, finish, ThemeChoice.Normalise(currentTheme));
        foreach (var v in _config.Vendors) v.Enabled = false; // starts empty: you choose
        Vendors = _config.Vendors.Select(v => new SetupVendorViewModel(v, logo)).ToList();
        foreach (var v in Vendors) v.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SetupVendorViewModel.Selected)) return;
            Notify(nameof(SelectedSummary), nameof(SelectedCount));
            if (SelectedCount > 0) Error = null; // the "choose at least one" hint goes once you have
        };
        VendorsView = new ListCollectionView(Vendors.ToList()) { Filter = o => o is SetupVendorViewModel v && Matches(v) };
        VendorsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SetupVendorViewModel.Category)));
        VendorsView.SortDescriptions.Add(new SortDescription(nameof(SetupVendorViewModel.CategoryOrder), ListSortDirection.Ascending));
        VendorsView.SortDescriptions.Add(new SortDescription(nameof(SetupVendorViewModel.Name), ListSortDirection.Ascending));
        Themes = ThemeChoice.All.Select(t => new ThemeChoice(t, t == _theme)).ToList();

        NextCommand = new RelayCommand(Next, () => _step < DoneStep);
        BackCommand = new RelayCommand(() => Step--, () => _step > NameStep);
        FinishCommand = new RelayCommand(Finish, () => _step == DoneStep);
        CancelCommand = new RelayCommand(cancel, () => !IsFirstRun);
        SelectPopularCommand = new RelayCommand(() => { foreach (var v in Vendors) v.Selected = v.Popular; });
        ClearCommand = new RelayCommand(() => { foreach (var v in Vendors) v.Selected = false; });
        PickThemeCommand = new RelayCommand(p => { if (p is ThemeChoice t) Theme = t.Name; });
    }

    public bool IsFirstRun { get; }
    /// <summary>The progress bar: each step and whether it has been reached.</summary>
    public IReadOnlyList<StepItem> StepItems => Steps.Select((s, i) => new StepItem(s, i <= _step, i == _step)).ToList();
    public sealed record StepItem(string Name, bool Reached, bool Current);
    public string Title => IsFirstRun ? "Welcome to Status Board" : "New profile";
    public string Intro => IsFirstRun
        ? "Let's set up your board. You can change all of this later, and add more profiles in Settings."
        : "A profile is its own board: its own vendors, groups and theme. Switch between them from the top-left corner.";

    public int Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, Math.Clamp(value, NameStep, DoneStep))) return;
            Error = null;
            Notify(nameof(IsNameStep), nameof(IsVendorsStep), nameof(IsThemeStep), nameof(IsDoneStep), nameof(StepText), nameof(NextText), nameof(Summary), nameof(StepItems));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsNameStep => _step == NameStep;
    public bool IsVendorsStep => _step == VendorsStep;
    public bool IsThemeStep => _step == ThemeStep;
    public bool IsDoneStep => _step == DoneStep;
    public string StepText => $"Step {_step + 1} of {Steps.Length} · {Steps[_step]}";
    public string NextText => _step == ThemeStep ? "Review" : "Next";
    public string? Error { get => _error; private set => Set(ref _error, value); }

    // ---- 1. Name ----
    public string Name { get => _name; set { if (Set(ref _name, value)) Error = null; } }

    // ---- 2. Vendors ----
    public IReadOnlyList<SetupVendorViewModel> Vendors { get; }
    public ICollectionView VendorsView { get; }
    public string Search { get => _search; set { if (Set(ref _search, value)) VendorsView.Refresh(); } }
    public int SelectedCount => Vendors.Count(v => v.Selected);
    public string SelectedSummary => SelectedCount switch { 0 => "Nothing chosen yet", 1 => "1 vendor chosen", var n => $"{n} vendors chosen" };
    public ICommand SelectPopularCommand { get; }
    public ICommand ClearCommand { get; }

    private bool Matches(SetupVendorViewModel v)
    {
        var q = _search.Trim();
        return q.Length == 0 || v.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || v.Category.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 3. Theme ----
    public IReadOnlyList<ThemeChoice> Themes { get; }
    public string Theme
    {
        get => _theme;
        set
        {
            if (!Set(ref _theme, ThemeChoice.Normalise(value))) return;
            foreach (var t in Themes) t.IsSelected = t.Name == _theme;
            _preview(_theme);
        }
    }
    public ICommand PickThemeCommand { get; }

    // ---- 4. Done ----
    public string Summary => $"“{ProfileStore.CleanName(_name)}” · {SelectedSummary.ToLowerInvariant()} · {_theme} theme";

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand CancelCommand { get; }

    private void Next()
    {
        if (_step == NameStep && string.IsNullOrWhiteSpace(_name)) { Error = "Give the profile a name, e.g. your name or a team."; return; }
        if (_step == VendorsStep && SelectedCount == 0) { Error = "Choose at least one vendor (or press “Default”)."; return; }
        Step++;
    }

    private void Finish()
    {
        foreach (var v in Vendors) v.Config.Enabled = v.Selected;
        _config.Theme = _theme;
        if (!_finish(_name, _config)) Error = "Couldn't save the profile. See the message at the top of the window.";
    }
}

public sealed class SetupVendorViewModel : ObservableObject
{
    private readonly Func<VendorConfig, Task<ImageSource?>> _loader;
    private bool _selected, _requested;
    private ImageSource? _logo;

    public SetupVendorViewModel(VendorConfig config, Func<VendorConfig, Task<ImageSource?>> loader) => (Config, _loader) = (config, loader);

    public VendorConfig Config { get; }
    public string Name => Config.Name;
    public string Category => VendorCategories.Normalise(Config.Category);
    public int CategoryOrder => VendorCategories.Order(Config.Category);
    /// <summary>In the default selection ("Default" ticks these).</summary>
    public bool Popular => DefaultVendors.DefaultIds.Contains(Config.Id);
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Monogram => CardViewModel.MonogramFor(Name);
    public ImageSource? Logo
    {
        get
        {
            if (!_requested) { _requested = true; _ = Load(); }
            return _logo;
        }
        private set { if (Set(ref _logo, value)) OnPropertyChanged(nameof(HasLogo)); }
    }
    public bool HasLogo => Logo is not null;
    private async Task Load() { try { Logo = await _loader(Config); } catch { } }
}

/// <summary>A theme the user can pick (Settings and setup).</summary>
public sealed class ThemeChoice(string name, bool selected) : ObservableObject
{
    public static readonly string[] All = ["System", .. AppConfig.ThemeNames];
    private bool _selected = selected;
    public string Name => name;
    public string Description => name switch
    {
        "System" => "Follows Windows light or dark",
        "Light" => "Clean and bright",
        "Dark" => "Easy on the eyes",
        "AMOLED" => "Pure black for OLED TVs and screens",
        "Hacker" => "Green on black, terminal style",
        "Obsidianite" => "Violet-black with a cyan glow",
        _ => "",
    };
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    // A tiny preview: background, a panel, the accent, text (System shows Light; it follows Windows).
    private (string Bg, string Panel, string Accent, string Text) Colors => name switch
    {
        "Dark" => ("#0A0F0E", "#121917", "#3DDBBE", "#E6EEEB"),
        "AMOLED" => ("#000000", "#000000", "#2EE6C3", "#F5F5F5"),
        "Hacker" => ("#000000", "#010801", "#39FF14", "#4DFF6A"),
        "Obsidianite" => ("#0D0B12", "#191621", "#0FB6D6", "#CBD1DC"),
        _ => ("#F2F4F3", "#FFFFFF", "#0B8577", "#0F1715"),
    };
    public Brush SwatchBg => Brush(Colors.Bg);
    public Brush SwatchPanel => Brush(Colors.Panel);
    public Brush SwatchAccent => Brush(Colors.Accent);
    public Brush SwatchText => Brush(Colors.Text);
    public Brush SwatchStroke => Brush(name switch { "Hacker" => "#17611E", "AMOLED" => "#333333", "Dark" => "#2E3B37", "Obsidianite" => "#383347", _ => "#CBD3D0" });
    public FontFamily SwatchFont => new(name == "Hacker" ? "Cascadia Mono, Consolas" : "Segoe UI Variable Display, Segoe UI");
    private static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    public static string Normalise(string? t) => All.FirstOrDefault(a => string.Equals(a, t?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "System";
}
