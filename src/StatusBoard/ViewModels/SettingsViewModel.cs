using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using StatusBoard.Core;
using StatusBoard.Services;

namespace StatusBoard.ViewModels;

public sealed record RefreshOption(int Minutes, string Label);

/// <summary>Edits a copy of the config; nothing changes until Save. Vendors are chosen in the Vendors pane (<see cref="VendorsViewModel"/>).</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppConfig _draft;
    private readonly Func<VendorConfig, Task<ImageSource?>> _logo;
    private int _refresh;
    private string _theme;
    private bool _tray, _ticker, _yearCycle;
    private int _yearInterval;
    private LogoChoiceViewModel? _logoVendor;
    private string? _logoMessage;
    private bool _logoMessageIsError;

    public SettingsViewModel(AppConfig draft, string configPath, Func<VendorConfig, Task<ImageSource?>> logo, Func<AppConfig, bool> save, Action cancel)
    {
        (_draft, _logo, ConfigPath) = (draft, logo, configPath);
        (_refresh, _theme, _tray, _ticker) = (draft.RefreshIntervalMinutes, draft.Theme, draft.TrayIcon, draft.NewsTicker);
        (_yearCycle, _yearInterval) = (draft.YearProgressCycle, YearProgress.NormaliseInterval(draft.YearProgressIntervalMinutes));

        var choices = AppConfig.RefreshChoices.ToList();
        if (!choices.Contains(_refresh)) choices.Insert(choices.FindIndex(m => m == 0 || m > _refresh) is var i and >= 0 ? i : choices.Count - 1, _refresh);
        RefreshOptions = choices.Select(m => new RefreshOption(m, AppConfig.DescribeInterval(m))).ToList();

        // Logo picker: your own vendors first (they're the ones most likely to need a logo), then watched, then the rest.
        LogoVendors = draft.Vendors.OrderByDescending(v => v.Custom).ThenByDescending(v => v.Enabled).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new LogoChoiceViewModel(v, logo)).ToList();
        _logoVendor = LogoVendors.FirstOrDefault();

        SaveCommand = new RelayCommand(() => save(Apply()));
        CancelCommand = new RelayCommand(cancel);
        OpenConfigFolderCommand = new RelayCommand(() => { try { Process.Start("explorer.exe", $"/select,\"{ConfigPath}\""); } catch { } });
        UploadLogoCommand = new RelayCommand(BrowseForLogo, () => _logoVendor is not null);
        ResetLogoCommand = new RelayCommand(() => _ = ResetLogoAsync(), () => _logoVendor is { HasCustomLogo: true });
    }

    public string ConfigPath { get; }
    public IReadOnlyList<RefreshOption> RefreshOptions { get; }
    public string[] ThemeOptions { get; } = ThemeChoice.All;
    public int RefreshIntervalMinutes { get => _refresh; set => Set(ref _refresh, value); }
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public bool TrayIcon { get => _tray; set => Set(ref _tray, value); }
    public bool NewsTicker { get => _ticker; set => Set(ref _ticker, value); }
    /// <summary>The header clock shows the year-progress bar now and then.</summary>
    public bool YearProgressCycle { get => _yearCycle; set => Set(ref _yearCycle, value); }
    public int YearProgressIntervalMinutes { get => _yearInterval; set => Set(ref _yearInterval, value); }
    public IReadOnlyList<RefreshOption> YearProgressOptions { get; } =
        YearProgress.IntervalChoices.Select(m => new RefreshOption(m, m == 60 ? "Every hour" : m == 1 ? "Every minute" : $"Every {m} minutes")).ToList();

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenConfigFolderCommand { get; }

    // ---- Vendor logos ----
    public IReadOnlyList<LogoChoiceViewModel> LogoVendors { get; }
    public LogoChoiceViewModel? LogoVendor
    {
        get => _logoVendor;
        set { if (Set(ref _logoVendor, value)) { LogoMessage = null; CommandManager.InvalidateRequerySuggested(); } }
    }
    public string? LogoMessage { get => _logoMessage; private set { if (Set(ref _logoMessage, value)) OnPropertyChanged(nameof(HasLogoMessage)); } }
    public bool HasLogoMessage => !string.IsNullOrEmpty(_logoMessage);
    public bool LogoMessageIsError { get => _logoMessageIsError; private set => Set(ref _logoMessageIsError, value); }
    public ICommand UploadLogoCommand { get; }
    public ICommand ResetLogoCommand { get; }

    /// <summary>The self-test (and the file dialog) call this with an image file.</summary>
    public bool ImportLogo(string file)
    {
        if (_logoVendor is not { } choice) return false;
        try
        {
            var result = LogoImporter.Import(file, choice.Config.Id);
            choice.SetPending(result.RelativePath, result.Image);
            (LogoMessage, LogoMessageIsError) = (result.Warning ?? $"New logo ready for {choice.Name}. Save to use it.", result.Warning is not null);
            CommandManager.InvalidateRequerySuggested();
            return true;
        }
        catch (LogoImportException ex)
        {
            (LogoMessage, LogoMessageIsError) = (ex.Message, true);
            return false;
        }
        catch (Exception ex) // an image the codecs choke on must never take the app down
        {
            (LogoMessage, LogoMessageIsError) = ($"Couldn't use that image ({ex.GetType().Name}). Try saving it as a PNG first.", true);
            return false;
        }
    }

    private void BrowseForLogo()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose a logo for {_logoVendor?.Name}",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) ImportLogo(dialog.FileName);
    }

    private async Task ResetLogoAsync()
    {
        if (_logoVendor is not { } choice) return;
        await choice.ResetAsync();
        (LogoMessage, LogoMessageIsError) = ($"{choice.Name} will use its automatic logo. Save to apply.", false);
        CommandManager.InvalidateRequerySuggested();
    }

    private AppConfig Apply()
    {
        (_draft.RefreshIntervalMinutes, _draft.Theme, _draft.TrayIcon, _draft.NewsTicker) = (_refresh, _theme, _tray, _ticker);
        (_draft.YearProgressCycle, _draft.YearProgressIntervalMinutes) = (_yearCycle, _yearInterval);
        foreach (var l in LogoVendors) l.Apply();
        return _draft;
    }
}

/// <summary>One vendor in the logo picker, with a preview of the logo it will have after Save.</summary>
public sealed class LogoChoiceViewModel : ObservableObject
{
    private readonly Func<VendorConfig, Task<ImageSource?>> _loader;
    private string? _logoSetting;
    private ImageSource? _logo;
    private bool _requested;

    public LogoChoiceViewModel(VendorConfig config, Func<VendorConfig, Task<ImageSource?>> loader)
    {
        (Config, _loader, _logoSetting) = (config, loader, config.Logo);
    }

    public VendorConfig Config { get; }
    public string Name => Config.Name;
    public string Detail => (Config.Custom ? "Your vendor" : VendorCategories.Normalise(Config.Category)) + (HasCustomLogo ? "  ·  custom logo" : "");
    public string Monogram => CardViewModel.MonogramFor(Name);
    public bool HasCustomLogo => LogoImporter.IsCustom(_logoSetting);
    public bool IsChanged => _logoSetting != Config.Logo;

    public ImageSource? Logo
    {
        get
        {
            if (!_requested) { _requested = true; _ = LoadAsync(Config); }
            return _logo;
        }
        private set { if (Set(ref _logo, value)) OnPropertyChanged(nameof(HasLogo)); }
    }
    public bool HasLogo => Logo is not null;

    private async Task LoadAsync(VendorConfig v)
    {
        try { Logo = await _loader(v); } catch { Logo = null; }
    }

    public void SetPending(string relativePath, ImageSource image)
    {
        _requested = true;
        _logoSetting = relativePath;
        Logo = image;
        Notify(nameof(HasCustomLogo), nameof(Detail), nameof(IsChanged));
    }

    /// <summary>Back to the built-in logo (or automatic discovery for your own vendors).</summary>
    public async Task ResetAsync()
    {
        _requested = true;
        _logoSetting = Config.Custom ? null : DefaultVendors.Create().FirstOrDefault(d => d.Id == Config.Id)?.Logo;
        Notify(nameof(HasCustomLogo), nameof(Detail), nameof(IsChanged));
        Logo = null;
        await LoadAsync(new VendorConfig { Id = Config.Id, Name = Config.Name, StatusPageUrl = Config.StatusPageUrl, Logo = _logoSetting, Sources = Config.Sources });
    }

    public void Apply() => Config.Logo = _logoSetting;
}
