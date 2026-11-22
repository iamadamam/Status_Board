using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StatusBoard.ViewModels;

namespace StatusBoard.Services;

/// <summary>
/// Developer aid: <c>StatusBoard.exe --snapshot out.png [--size 1200x800] [--theme Dark] [--select forticloud] [--settings] [--vendors] [--expand forticloud] [--add https://status.example.com]</c>
/// refreshes once, renders the window to a PNG and exits. Used to check layouts without a visible desktop.
/// The theme override applies to this run only; the config file is not changed.
/// </summary>
public sealed class SnapshotOptions
{
    public required string OutPath { get; init; }
    public double Width { get; init; } = 1200;
    public double Height { get; init; } = 800;
    public string? Theme { get; init; }
    public string[] Select { get; init; } = [];
    public bool OpenSettings { get; init; }
    /// <summary>--setup: show first-time setup (name step) as a brand-new copy would.</summary>
    public bool FirstRunSetup { get; init; }
    /// <summary>--vendors: open the Vendors pane; --expand forticloud: and open that vendor's row.</summary>
    public bool OpenVendors { get; init; }
    public string? Expand { get; init; }
    public string? AddVendorUrl { get; init; }
    public string? AddKind { get; init; }
    public string? AddToken { get; init; }
    /// <summary>Also press Add and Save (writes statusboard.json), then refresh and show the new vendor.</summary>
    public bool Commit { get; init; }

    public static SnapshotOptions? Parse(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var outPath = Arg("--snapshot");
        if (outPath is null) return null;
        double w = 1200, h = 800;
        if (Arg("--size")?.Split('x') is [var ws, var hs] && double.TryParse(ws, out var pw) && double.TryParse(hs, out var ph)) { w = pw; h = ph; }
        return new SnapshotOptions
        {
            OutPath = Path.GetFullPath(outPath),
            Width = w,
            Height = h,
            Theme = Arg("--theme"),
            Select = Arg("--select")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [],
            OpenSettings = args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)),
            OpenVendors = args.Any(a => string.Equals(a, "--vendors", StringComparison.OrdinalIgnoreCase)) || Arg("--add") is not null || Arg("--expand") is not null,
            FirstRunSetup = args.Any(a => string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase)),
            Expand = Arg("--expand"),
            AddVendorUrl = Arg("--add"),
            AddKind = Arg("--add-kind"),
            AddToken = Arg("--add-token"),
            Commit = args.Any(a => string.Equals(a, "--commit", StringComparison.OrdinalIgnoreCase)),
        };
    }

    public async Task RunAsync(Application app, Window window, MainViewModel vm, ThemeManager theme)
    {
        try
        {
            if (Theme is not null) theme.Apply(Theme);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000; // off-screen
            window.Width = Width;
            window.Height = Height;
            window.ShowActivated = false;
            window.Show();

            await vm.RefreshAsync();
            await vm.LoadLogosAsync();
            vm.Selected = vm.Cards.FirstOrDefault(c => Select.Contains(c.Id, StringComparer.OrdinalIgnoreCase));
            if (FirstRunSetup) vm.StartSetup(firstRun: true);
            if (OpenSettings && AddVendorUrl is null) vm.OpenSettingsCommand.Execute(null);
            if (OpenVendors)
            {
                vm.OpenVendorsCommand.Execute(null);
                if (Expand is not null && vm.Vendors!.Vendors.FirstOrDefault(v => string.Equals(v.Config.Id, Expand, StringComparison.OrdinalIgnoreCase)) is { } row)
                {
                    row.IsExpanded = true;
                    vm.Vendors.SearchText = row.Name;
                }
                if (AddVendorUrl is not null)
                {
                    vm.Vendors!.ShowAddVendorCommand.Execute(null);
                    var add = vm.Vendors.AddVendor!;
                    if (AddKind is "librenms") add.Kind = AddVendorViewModel.KindLibreNms;
                    add.Url = AddVendorUrl;
                    if (AddToken is not null) add.Token = AddToken;
                    add.CheckCommand.Execute(null);
                    await Task.Delay(50);
                    while (add.IsChecking) await Task.Delay(100);
                    if (Commit && add.CheckSucceeded)
                    {
                        add.AddCommand.Execute(null);
                        vm.Vendors.SaveCommand.Execute(null); // writes statusboard.json, then refreshes
                        await Task.Delay(50);
                        while (vm.IsRefreshing) await Task.Delay(100);
                        await vm.LoadLogosAsync();
                        vm.Selected = vm.Cards.LastOrDefault();
                    }
                }
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(400);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

            var root = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(root);
            var bmp = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            // Paint the window background first; the root grid itself is transparent.
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(bg);
            bmp.Render(root);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            await using (var fs = File.Create(OutPath)) enc.Save(fs);
        }
        finally
        {
            app.Shutdown();
        }
    }
}
