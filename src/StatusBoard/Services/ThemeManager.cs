using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace StatusBoard.Services;

/// <summary>
/// System (follows Windows), Light, Dark, AMOLED (true black), Hacker (green terminal) or Obsidianite (violet-black, cyan): swaps the colour/font dictionary and WPF's
/// Fluent theme mode, and tints the title bar to match. Only reads the registry ("apps use light theme"); never writes it.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private readonly Application _app;
    private readonly Dictionary<string, ResourceDictionary> _themes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Light"] = Load("Light"), ["Dark"] = Load("Dark"), ["AMOLED"] = Load("Amoled"), ["Hacker"] = Load("Hacker"), ["Obsidianite"] = Load("Obsidianite"),
    };
    private string _mode = "System";

    public ThemeManager(Application app)
    {
        _app = app;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static ResourceDictionary Load(string file) => new() { Source = new Uri($"Themes/{file}.xaml", UriKind.Relative) };

    public bool IsDark { get; private set; }
    /// <summary>The theme actually showing ("Light", "Dark", "AMOLED", "Hacker" or "Obsidianite"; System resolves to Light or Dark).</summary>
    public string Current { get; private set; } = "Light";

    public void Apply(string mode)
    {
        _mode = mode;
        Current = _themes.ContainsKey(mode) && mode != "System" ? _themes.Keys.First(k => k.Equals(mode, StringComparison.OrdinalIgnoreCase))
            : SystemUsesDarkTheme() ? "Dark" : "Light";
        IsDark = Current != "Light";
        var merged = _app.Resources.MergedDictionaries;
        foreach (var d in _themes.Values) merged.Remove(d);
        merged.Insert(0, _themes[Current]);
#pragma warning disable WPF0001 // ThemeMode is marked experimental but supported
        _app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        foreach (Window w in _app.Windows) ApplyTitleBar(w);
    }

    /// <summary>Dark title bar to match, and (Windows 11) the caption painted in the window background so they read as one surface.</summary>
    public void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (_app.TryFindResource("BgColor") is Color bg)
        {
            var caption = bg.R | (bg.G << 8) | (bg.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (ignored before Windows 11)
        }
        if (_app.TryFindResource("Text") is SolidColorBrush text)
        {
            var c = text.Color.R | (text.Color.G << 8) | (text.Color.B << 16);
            DwmSetWindowAttribute(hwnd, 36, ref c, sizeof(int)); // DWMWA_TEXT_COLOR: green title text in Hacker
        }
    }

    private static bool SystemUsesDarkTheme()
    {
        try { return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is 0; }
        catch { return false; }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_mode != "System" || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        _app.Dispatcher.BeginInvoke(() => { if (SystemUsesDarkTheme() != IsDark) Apply(_mode); });
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
