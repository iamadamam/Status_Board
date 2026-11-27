using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using StatusBoard.Core;
using StatusBoard.Services;
using StatusBoard.ViewModels;

namespace StatusBoard;

/// <summary>Startup and wiring: config, theme, single instance, tray, and the self-test/snapshot modes.</summary>
public partial class App : Application
{
    private ThemeManager? _theme;
    private TrayService? _tray;
    private HttpFetcher? _http;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private Mutex? _instance;
    private volatile bool _exiting;

    internal bool TrayVisible => _tray?.Visible == true;
    internal TrayService? Tray => _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        var selfTestDir = SelfTest.Parse(e.Args);
        var snapshot = SnapshotOptions.Parse(e.Args);
        // A second launch (Start menu, shortcut, the exe while in the tray) brings this copy forward instead of starting another
        // with its own tray icon and its own writes to statusboard.json. Self-test and snapshot runs are throwaway.
        if (selfTestDir is null && snapshot is null && !ClaimSingleInstance()) { Shutdown(); return; }

        // Profiles: each is its own settings file. A brand-new copy starts empty and runs setup; self-test and snapshot runs
        // (and copies from before profiles) use statusboard.json as before, which becomes the "Default" profile.
        var profiles = new ProfileStore(AppContext.BaseDirectory);
        if (selfTestDir is not null || snapshot is not null) ConfigStore.NextToExecutable().Load();
        profiles.Load();
        var firstRun = profiles.NeedsSetup;
        var store = firstRun ? ConfigStore.NextToExecutable() : profiles.StoreFor(profiles.Active!);
        var config = firstRun ? new AppConfig { DefaultsVersion = DefaultVendors.Version } : store.Load(); // nothing written until setup finishes
        _theme = new ThemeManager(this);
        _theme.Apply(config.Theme);
        _http = new HttpFetcher();
        _vm = new MainViewModel(store, config, new StatusService(ProviderRegistry.CreateDefault(), _http), _http, profiles,
            keepHistory: selfTestDir is null && snapshot is null); // scripted test results must never reach the real 24-hour history
        MainWindow = _window = new MainWindow(_vm, _theme);

        _vm.ConfigApplied += (_, _) => { _theme.Apply(_vm.Config.Theme); SyncTray(); };
        _vm.ThemePreview += (_, theme) => _theme.Apply(theme ?? _vm.Config.Theme);
        if (firstRun && selfTestDir is null && snapshot is null) _vm.StartSetup(firstRun: true);
        _vm.SummaryChanged += (_, _) => _tray?.SetStatus(_vm.OverallStatus, "Status Board – " + _vm.SummaryHeadline);
        // With the tray icon on, minimising tucks the window away into the tray.
        _window.StateChanged += (_, _) => { if (_window.WindowState == WindowState.Minimized && TrayVisible) _window.Hide(); };
        SyncTray();

        if (selfTestDir is not null) _ = new SelfTest(this, _window, _vm, _theme, selfTestDir).RunAsync();
        else if (snapshot is not null) _ = snapshot.RunAsync(this, _window, _vm, _theme);
        else
        {
            _window.Show();
            // "StatusBoard.exe --tv" starts as a wall board (e.g. from a shortcut on an office screen's PC).
            if (e.Args.Any(a => string.Equals(a, "--tv", StringComparison.OrdinalIgnoreCase))) _vm.IsTvMode = true;
            _ = _vm.RefreshAsync();        }
    }

    private void SyncTray()
    {
        if (_vm is null) return;
        if (!_vm.Config.TrayIcon) { _tray?.Dispose(); _tray = null; return; }
        _tray ??= new TrayService(ShowWindow, () => _ = _vm.RefreshAsync(), () => _window?.Close());
        _tray.Visible = true;
        _tray.SetStatus(_vm.OverallStatus, "Status Board – " + _vm.SummaryHeadline);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>True if this is the only copy running from this folder; otherwise asks that copy to show itself. Copies in other
    /// folders (each with its own statusboard.json) run independently.</summary>
    private bool ClaimSingleInstance()
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.TrimEnd('\\', '/').ToUpperInvariant())))[..16];
        _instance = new Mutex(true, $@"Local\StatusBoard-{id}", out var first);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\StatusBoard-{id}-show");
        if (!first)
        {
            AllowSetForegroundWindow(-1); // this launch owns the foreground; let the running copy take it
            show.Set();
            _instance.Dispose();
            _instance = null;
            return false;
        }
        new Thread(() => { while (show.WaitOne() && !_exiting) Dispatcher.BeginInvoke(ShowWindow); }) { IsBackground = true, Name = "Single instance" }.Start();
        return true;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the dashboard alive; adapters are already isolated, so this is a UI bug.
        MessageBox.Show(e.Exception.Message, "Status Board – unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch (ApplicationException) { }
            _instance.Dispose();
        }
        _vm?.Shutdown();
        _tray?.Dispose();
        _theme?.Dispose();
        _http?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
