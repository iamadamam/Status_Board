using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StatusBoard.ViewModels;
using StatusBoard.Core;

namespace StatusBoard.Services;

/// <summary>
/// Developer aid: <c>StatusBoard.exe --selftest &lt;out folder&gt;</c> drives the real window through the main user flows
/// (select/close cards, splitter, narrow/wide layouts, Esc, settings, refresh modes, themes, add/remove vendor, tray),
/// checks the resulting layout, writes selftest.txt with PASS/FAIL lines and a PNG per step, then exits.
/// It changes statusboard.json next to the exe, so run it on a copy of the app folder.
/// </summary>
public sealed class SelfTest(App app, MainWindow w, MainViewModel vm, ThemeManager theme, string outDir)
{
    private readonly StringBuilder _log = new();
    private int _pass, _fail, _shot;

    public static string? Parse(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(outDir);
        try
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -20000;
            w.ShowActivated = false;
            await Resize(1300, 900);
            w.Show();
            await Settle();

            await Step("Initial refresh", async () =>
            {
                await vm.RefreshAsync();
                await vm.LoadLogosAsync();
                await Settle();
                Check("all cards have a result", vm.Cards.All(c => c.Data is not null), $"{vm.Cards.Count} cards");
                var ranks = vm.Cards.Select(MainViewModel.SortRank).ToList();
                Check("problems listed first", ranks.Zip(ranks.Skip(1)).All(p => p.First <= p.Second), string.Join(" ", vm.Cards.Take(8).Select(c => c.Name)));
                var unknown = vm.Cards.Where(c => c.Status == Core.ServiceStatus.Unknown).Select(c => c.Name).ToList();
                Check("no vendor unreachable", unknown.Count == 0, string.Join(", ", unknown));
                var noLogo = vm.Cards.Where(c => !c.HasLogo).Select(c => c.Name).ToList();
                Check("every card has a logo", noLogo.Count == 0, string.Join(", ", noLogo));
                Check("summary shows a headline", !string.IsNullOrWhiteSpace(vm.SummaryHeadline), vm.SummaryHeadline);
                Check("next refresh shown", vm.LastRefreshText.Contains("next in"), vm.LastRefreshText);
                CheckCardsFit("wide, no panel");
                await Shot("01-dashboard");
            });

            await Step("Open and close the details panel for every card (close button, Esc, click card again)", async () =>
            {
                var full = ContentWidth();
                var i = 0;
                foreach (var card in vm.Cards.ToList())
                {
                    ClickCard(card);
                    await Settle();
                    var open = vm.Selected == card && w.DetailsPanel.Visibility == Visibility.Visible && w.PanelCol.ActualWidth >= 299;
                    Check($"[{card.Name}] panel opens", open, $"panel {w.PanelCol.ActualWidth:0}px");
                    Check($"[{card.Name}] dashboard narrowed", w.Scroller.ActualWidth < full - 250, $"{w.Scroller.ActualWidth:0} of {full:0}");
                CheckCardsFit($"[{card.Name}] panel open");
                    if (i == 0) await Shot("02-panel-open");

                    switch (i++ % 3)
                    {
                        case 0: Invoke(FindButton(w.DetailsPanel, "Close details")); break;
                        case 1: PressEscape(); break;
                        default: ClickCard(card); break; // clicking the selected card again closes it
                    }
                    await Settle();
                    CheckClosed($"[{card.Name}] closed via {(new[] { "close button", "Esc", "card click" })[(i - 1) % 3]}", full);
                }
                await Shot("03-panel-closed");
            });

            await Step("Switch between cards while the panel is open", async () =>
            {
                ClickCard(vm.Cards[0]);
                await Settle();
                ClickCard(vm.Cards[^1]);
                await Settle();
                Check("panel shows the newly clicked card", vm.Selected == vm.Cards[^1] && w.DetailsPanel.DataContext == vm.Cards[^1]);
                Check("only one card selected", vm.Cards.Count(c => c.IsSelected) == 1);
                vm.CloseDetailsCommand.Execute(null);
                await Settle();
                CheckClosed("closed after switching", ContentWidth());
            });

            await Step("Drag the splitter, close, reopen", async () =>
            {
                ClickCard(vm.Cards[3]);
                await Settle();
                var before = w.PanelCol.ActualWidth;
                DragSplitter(-250);
                await Settle();
                Check("splitter widened the panel", w.PanelCol.ActualWidth > before + 200, $"{before:0} -> {w.PanelCol.ActualWidth:0}");
                Check("new width remembered", Math.Abs(vm.Config.DetailsPanelWidth - w.PanelCol.ActualWidth) < 2, $"{vm.Config.DetailsPanelWidth}");
                await Shot("04-panel-resized");
                var wide = w.PanelCol.ActualWidth;
                vm.CloseDetailsCommand.Execute(null);
                await Settle();
                CheckClosed("closed after splitter drag", ContentWidth());
                await Shot("05-closed-after-drag");
                ClickCard(vm.Cards[3]);
                await Settle();
                Check("reopens at remembered width", Math.Abs(w.PanelCol.ActualWidth - wide) < 2, $"{w.PanelCol.ActualWidth:0} vs {wide:0}");
                CheckCardsFit("wide panel open");
                DragSplitter(-2000); // drag far left: try to squash the dashboard
                await Settle();
                Check("dashboard keeps a minimum width", w.Scroller.ActualWidth >= 330, $"{w.Scroller.ActualWidth:0}");
                CheckCardsFit("dashboard at minimum width");
                await Shot("04b-dashboard-min-width");
                DragSplitter(+2000); // drag far right: try to squash the panel
                await Settle();
                Check("panel keeps a minimum width", w.PanelCol.ActualWidth >= 299, $"{w.PanelCol.ActualWidth:0}");
                vm.CloseDetailsCommand.Execute(null);
                await Settle();
                CheckClosed("closed after extreme drags", ContentWidth());
            });

            await Step("Narrow window (side-of-screen)", async () =>
            {
                await Resize(460, 820);
                CheckCardsFit("narrow, no panel");
                await Shot("06-narrow");
                ClickCard(vm.Cards[3]);
                await Settle();
                Check("panel fills the window", w.Scroller.Visibility == Visibility.Collapsed && w.PanelCol.ActualWidth > 400, $"{w.PanelCol.ActualWidth:0}");
                await Shot("07-narrow-panel");
                PressEscape();
                await Settle();
                CheckClosed("narrow panel closes back to cards", ContentWidth());
                Check("cards visible again", w.Scroller.Visibility == Visibility.Visible);
            });

            await Step("Resize while the panel is open", async () =>
            {
                ClickCard(vm.Cards[3]);
                await Resize(1300, 900);
                Check("narrow -> wide docks the panel", w.Scroller.Visibility == Visibility.Visible && w.Splitter.Visibility == Visibility.Visible
                    && w.PanelCol.ActualWidth >= 299 && w.PanelCol.ActualWidth < 1000, $"{w.PanelCol.ActualWidth:0}");
                await Resize(500, 900);
                Check("wide -> narrow takes over", w.Scroller.Visibility == Visibility.Collapsed);
                await Resize(1900, 1000);
                Check("very wide docks again", w.Scroller.Visibility == Visibility.Visible);
                CheckCardsFit("very wide with panel");
                vm.CloseDetailsCommand.Execute(null);
                await Settle();
                CheckClosed("closed at very wide", ContentWidth());
                CheckCardsFit("very wide, no panel");
                await Resize(1300, 900);
            });

            await Step("Settings: Manual refresh, Dark theme, tray + notifications", async () =>
            {
                vm.OpenSettingsCommand.Execute(null);
                await Settle();
                Check("settings drawer open", vm.IsSettingsOpen);
                await Shot("08-settings");
                var s = vm.Settings!;
                s.RefreshIntervalMinutes = 0;
                s.Theme = "Dark";
                s.TrayIcon = true;
                s.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("drawer closed after save", !vm.IsSettingsOpen);
                Check("manual refresh shown", vm.LastRefreshText.Contains("auto-refresh off"), vm.LastRefreshText);
                Check("dark theme applied", theme.IsDark);
                Check("tray icon visible", app.TrayVisible);
                if (app.Tray is { } tray)
                {
                    Check("Windows accepted the tray icon", tray.LastCallAccepted);
                    tray.SetStatus(Core.ServiceStatus.MajorOutage, "Status Board – self-test tooltip");
                    Check("tray icon status and tooltip update", tray.LastCallAccepted);
                }
                var json = File.ReadAllText(Core.ConfigStore.NextToExecutable().Path);
                Check("saved to statusboard.json", json.Contains("\"refreshIntervalMinutes\": 0") && json.Contains("\"theme\": \"Dark\""));
                await Shot("09-dark-manual");

                vm.OpenSettingsCommand.Execute(null);
                PressEscape();
                await Settle();
                Check("Esc cancels settings", !vm.IsSettingsOpen);
            });

            await Step("Vendors: switch a vendor off and AWS regions to none", async () =>
            {
                var before = vm.Cards.Count;
                vm.OpenVendorsCommand.Execute(null);
                var s = vm.Vendors!;
                s.Vendors.Single(v => v.Config.Id == "docker").Enabled = false;
                s.Vendors.Single(v => v.Config.Id == "aws").SelectNoneCommand.Execute(null);
                s.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("disabled vendor's card removed", vm.Cards.Count == before - 1 && vm.Cards.All(c => c.Id != "docker"));
                Check("AWS with no regions is operational", vm.Cards.Single(c => c.Id == "aws").Status == Core.ServiceStatus.Operational);
            });

            await Step("Add a vendor from a link, then remove it", async () =>
            {
                var before = vm.Cards.Count;
                vm.OpenVendorsCommand.Execute(null);
                var s = vm.Vendors!;
                s.ShowAddVendorCommand.Execute(null);
                var add = s.AddVendor!;
                add.Url = "status.helpscout.com"; // a Statuspage site that is not built in
                add.CheckCommand.Execute(null);
                await Task.Delay(50);
                while (add.IsChecking) await Task.Delay(100);
                Check("detection succeeded", add.CheckSucceeded, add.Message);
                await Shot("10-add-vendor");
                add.AddCommand.Execute(null);
                s.SaveCommand.Execute(null);
                await WaitRefresh();
                await vm.LoadLogosAsync();
                var zoom = vm.Cards.LastOrDefault();
                Check("new card added", vm.Cards.Count == before + 1 && zoom?.Name == "Help Scout", zoom?.Name);
                Check("new card has status", zoom?.Data is not null && zoom.Status != Core.ServiceStatus.Unknown, zoom?.Status.ToString());
                Check("new card has logo", zoom?.HasLogo == true);
                await Shot("11-vendor-added");

                // Custom logo for the new vendor: trimmed, centred on white, saved under logos\custom.
                var sample = Path.Combine(outDir, "sample-logo.png");
                File.WriteAllBytes(sample, SampleLogoPng());
                vm.OpenSettingsCommand.Execute(null);
                await Settle();
                var st = vm.Settings!;
                st.LogoVendor = st.LogoVendors.First(l => l.Name == "Help Scout");
                Check("custom vendors listed first in logo picker", st.LogoVendors[0].Config.Custom);
                Check("logo imported", st.ImportLogo(sample), st.LogoMessage);
                await Settle();
                await Shot("11b-logo-uploaded");
                st.SaveCommand.Execute(null);
                await WaitRefresh();
                await vm.LoadLogosAsync();
                var hs = vm.Config.Vendors.Single(v => v.Name == "Help Scout");
                Check("logo setting points to logos/custom", LogoImporter.IsCustom(hs.Logo), hs.Logo);
                Check("logo file written", File.Exists(Path.Combine(AppContext.BaseDirectory, hs.Logo!)));
                if (vm.Cards.FirstOrDefault(c => c.Name == "Help Scout")?.Logo is BitmapSource tile)
                {
                    var px = new byte[4];
                    new FormatConvertedBitmap(tile, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(1, 1, 1, 1), px, 4, 0);
                    Check("logo is square with a white background", tile.PixelWidth == LogoImporter.Size && tile.PixelHeight == LogoImporter.Size && px[0] == 255 && px[1] == 255 && px[2] == 255 && px[3] == 255);
                }
                else Check("card shows the uploaded logo", false);
                await Shot("11c-custom-logo-tile");

                vm.OpenVendorsCommand.Execute(null);
                vm.Vendors!.Vendors.Single(v => v.IsCustom && v.Name == "Help Scout").RemoveCommand.Execute(null);
                vm.Vendors.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("custom vendor removed", vm.Cards.All(c => c.Name != "Help Scout") && vm.Cards.Count == before);
                Check("unused custom logo deleted", !File.Exists(Path.Combine(AppContext.BaseDirectory, hs.Logo!)));
            });

            await Step("Tile size and service picker", async () =>
            {
                vm.ZoomResetCommand.Execute(null); // start from 100% whatever the saved size
                await Settle();
                double CardW() => vm.CardWidth * vm.TileScale;
                var normal = CardW();
                Check("100% is the larger base size", Math.Abs(vm.TileScale - MainViewModel.BaseTileScale) < 0.001, $"{vm.TileScale}");
                vm.ZoomInCommand.Execute(null);
                vm.ZoomInCommand.Execute(null);
                vm.ZoomInCommand.Execute(null);
                await Settle();
                // Bigger tiles mean fewer per row; "cards fit" below checks the row count for this scale.
                Check("larger tiles", vm.CardScale > 1.25 && vm.TileScale > MainViewModel.BaseTileScale * 1.25, $"{vm.CardScaleText}, {normal:0} -> {CardW():0}px");
                CheckCardsFit("tiles at " + vm.CardScaleText);
                await Shot("10-large-tiles");
                vm.ZoomResetCommand.Execute(null);
                await Settle();
                Check("tile size reset", Math.Abs(vm.CardScale - 1) < 0.001 && Math.Abs(CardW() - normal) < 2, vm.CardScaleText);

                vm.OpenVendorsCommand.Execute(null);
                await Settle();
                var s = vm.Vendors!;
                Check("Vendors button opens the vendors pane", vm.IsVendorsOpen && !vm.IsSettingsOpen && w.VendorsView.VendorSearch.IsFocused);
                await Shot("10a-vendors-pane");
                var forti = s.Vendors.Single(v => v.Config.Id == "forticloud");
                forti.ToggleExpandedCommand.Execute(null);
                s.SearchText = "forti";
                await Settle();
                Check("vendor row expands with its services", forti.IsExpanded && forti.Sources.Count > 5 && forti.StatusPageUrl is not null, forti.WatchesText);
                await Shot("10b-vendor-expanded");
                s.SearchText = "git";
                var shown = s.VendorsView.Cast<ViewModels.VendorSettingsViewModel>().Select(v => v.Name).ToList();
                Check("search filters the list", shown.Count >= 2 && shown.All(n => n.Contains("Git", StringComparison.OrdinalIgnoreCase)), string.Join(", ", shown));
                s.SearchText = "";
                s.ShowFilter = ViewModels.VendorsViewModel.ShowChoices[1];
                Check("'Watching' filter shows only watched", s.VendorsView.Cast<ViewModels.VendorSettingsViewModel>().All(v => v.Enabled));
                var rows = s.VendorsView.Cast<ViewModels.VendorSettingsViewModel>().ToList();
                Check("list is grouped by category, alphabetical within", rows.Zip(rows.Skip(1)).All(p => p.First.CategoryOrder < p.Second.CategoryOrder
                    || (p.First.CategoryOrder == p.Second.CategoryOrder && string.Compare(p.First.Name, p.Second.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)),
                    string.Join(", ", rows.Take(5).Select(r => r.Name)));
                await Shot("10-service-picker");
                s.CancelCommand.Execute(null);
                await Settle();
            });

            await Step("News ticker follows status changes and incident updates", async () =>
            {
                // Scripted results for two real cards, fed through the same path a refresh uses.
                var a = vm.Cards.First(c => c.Id == "github");
                var b = vm.Cards.First(c => c.Id == "openai");
                Core.VendorStatus V(CardViewModel c, params Core.Incident[] incidents) => new()
                {
                    VendorId = c.Id, VendorName = c.Name, StatusPageUrl = c.StatusPageUrl,
                    Sources = [new Core.SourceResult { SourceLabel = c.Name, Status = incidents.Select(i => i.Status).Worst(), Incidents = incidents }],
                };
                var now = DateTimeOffset.Now;
                const string longText = "Engineers are investigating elevated error rates affecting pushes, pulls and the web interface for a subset of users in several regions.";
                int majorBefore = vm.MajorCount;

                // 1. A major outage appears.
                vm.ApplyResults([V(a, new Core.Incident { Title = "Scripted outage A", Summary = longText, Status = Core.ServiceStatus.MajorOutage, LastUpdated = now }), V(b)]);
                await Settle();
                Check("ticker shows a new outage first", vm.TickerItems.FirstOrDefault()?.Headline == "Scripted outage A" && vm.ShowTicker,
                    string.Join(" | ", vm.TickerItems.Take(3).Select(t => t.Headline)));
                Check("ticker text is on screen", TickerTexts().Any(t => t.Contains("Scripted outage A")) && w.StatusBar.TickerStrip.IsVisible);
                Check("major counter went up", vm.MajorCount == majorBefore + 1, $"{majorBefore} -> {vm.MajorCount}");
                Check("outage card moved up with the major outages", vm.Cards.TakeWhile(c => c != a).All(c => c.Status == Core.ServiceStatus.MajorOutage),
                    $"at {vm.Cards.IndexOf(a)} after {string.Join(", ", vm.Cards.TakeWhile(c => c != a).Select(c => c.Name))}");
                var start = w.StatusBar.TickerOffset;
                await Task.Delay(700);
                Check("ticker scrolls", w.StatusBar.TickerScrolling && w.StatusBar.TickerOffset < start - 10,
                    $"offset {start:0} -> {w.StatusBar.TickerOffset:0}, scrolling {w.StatusBar.TickerScrolling}, content {w.StatusBar.TickerA.ActualWidth:0}px in {w.StatusBar.TickerViewport.ActualWidth:0}px, animations {SystemParameters.ClientAreaAnimation}");
                // Windows throttles off-screen windows (~30 fps), so bring the test window on screen to measure what a person sees.
                var (oldLeft, oldTop) = (w.Left, w.Top);
                var area = SystemParameters.WorkArea;
                w.Left = area.Left + 20; w.Top = area.Top + 20;
                await Settle();
                await Task.Delay(500);
                var (fps, longGaps, jitter) = await MeasureTickerFrames(TimeSpan.FromSeconds(3));
                // Diagnostics: is any limit the ticker or the window/machine as a whole?
                w.StatusBar.Pause(true);
                var (idleFps, idleLate, _) = await MeasureTickerFrames(TimeSpan.FromSeconds(3));
                w.StatusBar.Pause(false);
                w.CardsHost.Visibility = Visibility.Collapsed;
                await Settle();
                var (noCardsFps, noCardsLate, _) = await MeasureTickerFrames(TimeSpan.FromSeconds(2));
                w.CardsHost.Visibility = Visibility.Visible;
                w.StatusBar.AffectedHost.Visibility = Visibility.Collapsed;
                await Settle();
                var (noChipsFps, noChipsLate, _) = await MeasureTickerFrames(TimeSpan.FromSeconds(2));
                w.StatusBar.AffectedHost.Visibility = Visibility.Visible;
                _log.AppendLine($"  INFO  without cards {noCardsFps:0} fps ({noCardsLate:P0} late); without chips {noChipsFps:0} fps ({noChipsLate:P0} late)");
                w.Left = oldLeft; w.Top = oldTop;
                _log.AppendLine($"  INFO  render tier {RenderCapability.Tier >> 16}, ticker running {fps:0} fps, ticker paused {idleFps:0} fps ({idleLate:P0} late), " +
                    $"window at {w.Left:0},{w.Top:0} {w.ActualWidth:0}x{w.ActualHeight:0}, active {w.IsActive}, screen {SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}");
                // Absolute frame rates depend on what else the machine is doing, so judge the ticker against the same window with
                // the ticker paused, measured moments apart: scrolling must not add dropped frames.
                Check("ticker scrolls smoothly", fps >= idleFps - 5 && longGaps <= idleLate + 0.05,
                    $"running {fps:0} fps / {longGaps:P0} late vs paused {idleFps:0} fps / {idleLate:P0} late; per-frame step varies ±{jitter:P0}");
                await Shot("11-ticker");

                // 2. The same incident is updated and downgraded.
                vm.ApplyResults([V(a, new Core.Incident { Title = "Scripted outage A", Summary = "A fix has been deployed and we are monitoring.", Status = Core.ServiceStatus.Degraded, LastUpdated = now.AddMinutes(5) }), V(b)]);
                await Settle();
                var item = vm.TickerItems.FirstOrDefault(t => t.Headline == "Scripted outage A");
                Check("ticker shows the incident update", item?.Detail == "A fix has been deployed and we are monitoring." && item.Status == Core.ServiceStatus.Degraded,
                    item?.Detail);
                Check("updated text is on screen", TickerTexts().Any(t => t.Contains("A fix has been deployed")) && !TickerTexts().Any(t => t.Contains("Engineers are investigating elevated")));
                Check("counters follow the downgrade", vm.MajorCount == majorBefore && vm.DegradedCount >= 1, $"major {vm.MajorCount}, degraded {vm.DegradedCount}");

                // 3. A resolves, B has a new problem.
                vm.ApplyResults([V(a), V(b, new Core.Incident { Title = "Scripted outage B", Summary = "Some requests are failing.", Status = Core.ServiceStatus.PartialOutage, LastUpdated = now.AddMinutes(9) })]);
                await Settle();
                Check("resolved incident leaves the ticker", vm.TickerItems.All(t => t.Headline != "Scripted outage A") && !TickerTexts().Any(t => t.Contains("Scripted outage A")));
                Check("new incident joins the ticker", vm.TickerItems.Any(t => t.Headline == "Scripted outage B") && TickerTexts().Any(t => t.Contains("Scripted outage B")));
                Check("resolved card drops below problems", vm.Cards.IndexOf(b) < vm.Cards.IndexOf(a), $"B at {vm.Cards.IndexOf(b)}, A at {vm.Cards.IndexOf(a)}");
                var bItem = TickerButtons().FirstOrDefault(x => x.DataContext is TickerItemViewModel t && t.Headline == "Scripted outage B");
                Invoke(bItem);
                Check("details panel slides in (even with Windows animation effects off)", w.DetailsPanel.RenderTransform is System.Windows.Media.TranslateTransform { HasAnimatedProperties: true } && w.DetailsPanel.HasAnimatedProperties,
                    $"Windows animation effects {(SystemParameters.ClientAreaAnimation ? "on" : "off")}");
                await Settle();
                Check("clicking a ticker item opens that vendor", vm.Selected == b, vm.Selected?.Name);
                vm.CloseDetailsCommand.Execute(null);

                // 4. The Settings switch hides and shows it.
                vm.OpenSettingsCommand.Execute(null);
                vm.Settings!.NewsTicker = false;
                vm.Settings.SaveCommand.Execute(null);
                await Settle();
                Check("ticker can be switched off", !vm.ShowTicker && !w.StatusBar.TickerStrip.IsVisible && !vm.Config.NewsTicker);
                vm.OpenSettingsCommand.Execute(null);
                vm.Settings!.NewsTicker = true;
                vm.Settings.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("ticker back on after a real refresh", vm.Config.NewsTicker && vm.TickerItems.All(t => !t.Headline.StartsWith("Scripted")),
                    $"{vm.TickerItems.Count} items, shown {vm.ShowTicker}");
            });

            await Step("Multi-product vendor tile names the affected services", async () =>
            {
                if (vm.Cards.FirstOrDefault(c => c.Id == "forticloud") is not { } forti) { Check("FortiCloud is watched", false); return; }
                Core.SourceResult Src(string label, Core.ServiceStatus status) => new()
                {
                    SourceLabel = label, Status = status,
                    Components = status == Core.ServiceStatus.Operational ? [] : [new Core.ComponentStatus("API", status)],
                };
                vm.ApplyResults([new Core.VendorStatus
                {
                    VendorId = forti.Id, VendorName = forti.Name, StatusPageUrl = forti.StatusPageUrl,
                    Sources = [Src("Status Hub", Core.ServiceStatus.Operational), Src("FortiZTP", Core.ServiceStatus.Degraded),
                               Src("FortiSIEM Cloud", Core.ServiceStatus.MajorOutage), Src("FortiSandbox Cloud", Core.ServiceStatus.Operational)],
                }]);
                await Settle();
                Check("tile lists affected services, worst first", forti.HasAffectedServices && forti.AffectedServicesText == "FortiSIEM Cloud, FortiZTP", forti.AffectedServicesText);
                Check("healthy products not listed", forti.AffectedServices.All(s => s.Name is not ("Status Hub" or "FortiSandbox Cloud")));
                Check("outage tile still sorted to the top", vm.Cards.IndexOf(forti) < vm.Cards.Count(c => c.Status == Core.ServiceStatus.MajorOutage), $"at {vm.Cards.IndexOf(forti)}");
                w.Scroller.ScrollToTop();
                await Settle();
                await Shot("11d-affected-services");
                await vm.RefreshAsync(); // back to real data
            });

            await Step("Incident stages: tile, details, ticker and notifications", async () =>
            {
                var card = vm.Cards.First(c => c.Id == "github");
                var changes = new List<StatusChange>();
                void OnChanged(object? _, IReadOnlyList<StatusChange> c) => changes.AddRange(c);
                vm.StatusesChanged += OnChanged;
                Core.VendorStatus G(Core.ServiceStatus s, Core.IncidentStage st) => new()
                {
                    VendorId = card.Id, VendorName = card.Name, StatusPageUrl = card.StatusPageUrl,
                    Sources = [Core.SourceResult.Of(card.Name, s == Core.ServiceStatus.Operational ? [] :
                        [new Core.Incident { Title = "Scripted stage incident", Summary = "Details.", Status = s, Stage = st, LastUpdated = DateTimeOffset.Now }])],
                };
                try
                {
                    vm.ApplyResults([G(Core.ServiceStatus.Operational, Core.IncidentStage.None)]);
                    changes.Clear();
                    vm.ApplyResults([G(Core.ServiceStatus.PartialOutage, Core.IncidentStage.Investigating)]);
                    await Settle();
                    Check("tile shows the stage", card.StageText == "Investigating" && !card.StageRecovering, card.StageText);
                    Check("stage pill is on the tile", TextsUnder(w.CardsHost).Contains("Investigating"));
                    Check("ticker says the stage", vm.TickerItems.Any(t => t.Headline == "Scripted stage incident" && t.StageText == "Investigating" && t.Text.Contains("Partial outage · Investigating")));
                    Check("notification on the outage", changes.Any(c => c.ToText == "Partial outage · Investigating"), string.Join("; ", changes.Select(c => c.ToText)));
                    CheckCardsFit("stage pills on tiles");
                    w.Scroller.ScrollToTop();
                    await Shot("11e-stage-investigating");

                    changes.Clear();
                    vm.ApplyResults([G(Core.ServiceStatus.PartialOutage, Core.IncidentStage.Monitoring)]);
                    await Settle();
                    Check("same status, new stage notifies", changes.Count == 1 && changes[0].FromText == "Partial outage · Investigating" && changes[0].ToText == "Partial outage · Monitoring",
                        string.Join("; ", changes.Select(c => $"{c.FromText} -> {c.ToText}")));
                    Check("monitoring shows as recovering", card.StageRecovering);
                    ClickCard(card);
                    await Settle();
                    Check("details explain the stage", TextsUnder(w.DetailsPanel).Any(t => t.Contains("A fix has been applied")) && TextsUnder(w.DetailsPanel).Contains("Monitoring"));
                    CheckCardsFit("details with stage");
                    await Shot("11f-stage-details");
                    vm.CloseDetailsCommand.Execute(null);

                    changes.Clear();
                    vm.ApplyResults([G(Core.ServiceStatus.PartialOutage, Core.IncidentStage.Monitoring)]);
                    Check("no repeat notification when nothing changed", changes.Count == 0);
                    vm.ApplyResults([G(Core.ServiceStatus.Maintenance, Core.IncidentStage.InProgress)]);
                    Check("maintenance shows In progress", card.StageText == "In progress");
                    vm.ApplyResults([G(Core.ServiceStatus.Operational, Core.IncidentStage.None)]);
                    await Settle();
                    Check("stage clears when healthy", card.StageText is null && !TextsUnder(w.CardsHost).Contains("Scripted stage incident"));
                }
                finally { vm.StatusesChanged -= OnChanged; }
                await vm.RefreshAsync();
            });

            await Step("Groups: sections at the top of the board", async () =>
            {
                vm.OpenVendorsCommand.Execute(null);
                await Settle();
                var p = vm.Vendors!;
                p.NewGroupName = "Critical";
                p.AddGroupCommand.Execute(null);
                p.NewGroupName = "";
                p.AddGroupCommand.Execute(null); // blank name gets a default
                Check("groups added with sensible names", p.Groups.Select(g => g.Name).SequenceEqual(["Critical", "Group 2"]), string.Join(", ", p.Groups.Select(g => g.Name)));
                p.Groups[1].Name = "Security";
                p.Groups[1].MoveUpCommand.Execute(null);
                Check("groups reorder", p.Groups[0].Name == "Security" && p.GroupChoices[1].Name == "Security");
                var critical = p.Groups.Single(g => g.Name == "Critical");
                foreach (var id in new[] { "github", "openai" }) p.Vendors.Single(v => v.Config.Id == id).Group = critical;
                p.Vendors.Single(v => v.Config.Id == "forticloud").Group = p.Groups.Single(g => g.Name == "Security");
                Check("row summary shows the group", p.Vendors.Single(v => v.Config.Id == "github").RowSummary.StartsWith("Critical"));
                p.SearchText = "github";
                p.Vendors.Single(v => v.Config.Id == "github").IsExpanded = true;
                await Settle();
                await Shot("14a-groups-manager");
                p.SaveCommand.Execute(null);
                await WaitRefresh();
                await vm.LoadLogosAsync();
                var groups = vm.CardsView.Groups?.OfType<System.Windows.Data.CollectionViewGroup>().Select(g => ((BoardSection)g.Name).Title).ToList() ?? [];
                Check("board shows group sections in order", groups.SequenceEqual(["Security", "Critical", MainViewModel.OtherVendorsTitle]), string.Join(" | ", groups));
                Check("grouped vendors come first", vm.Cards[0].Id == "forticloud" && vm.Cards.Skip(1).Take(2).All(c => c.GroupId is not null), string.Join(", ", vm.Cards.Take(4).Select(c => c.Name)));
                Check("saved", vm.Config.Groups.Count == 2 && vm.Config.Vendors.Single(v => v.Id == "github").GroupId == critical.Id);
                w.Scroller.ScrollToTop();
                CheckCardsFit("with group sections");
                ClickCard(vm.Cards.First(c => c.Id == "openai"));
                await Settle();
                Check("cards in a group still open", vm.Selected?.Id == "openai");
                vm.CloseDetailsCommand.Execute(null);
                await Shot("14b-group-sections");

                // Deleting a group ungroups its vendors.
                vm.OpenVendorsCommand.Execute(null);
                var sec = vm.Vendors!.Groups.Single(g => g.Name == "Security");
                sec.DeleteCommand.Execute(null);
                Check("deleting a group ungroups its vendors", vm.Vendors.Vendors.Single(v => v.Config.Id == "forticloud").Group == GroupEditViewModel.None);
                vm.Vendors.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("one group left", vm.Config.Groups.Count == 1 && vm.Config.Vendors.Single(v => v.Id == "forticloud").GroupId is null);

                // Try to break it: a 60-character name, then an empty group, then no groups at all.
                vm.OpenVendorsCommand.Execute(null);
                vm.Vendors!.Groups[0].Name = new string('W', 60);
                vm.Vendors.NewGroupName = "Empty";
                vm.Vendors.AddGroupCommand.Execute(null);
                vm.Vendors.SaveCommand.Execute(null);
                await WaitRefresh();
                w.Scroller.ScrollToTop();
                await Settle();
                Check("an empty group shows no section", vm.CardsView.Groups?.OfType<System.Windows.Data.CollectionViewGroup>().All(g => ((BoardSection)g.Name).Title != "Empty") == true);
                CheckCardsFit("very long group name");
                await Shot("14c-long-group-name");
                await Resize(420, 820);
                CheckNoClippedText("very long group name, narrow", w.Scroller);
                var header = FindDescendants<TextBlock>(w.CardsHost).FirstOrDefault(t => t.Text.StartsWith("WWWW"));
                Check("long group name trims on a narrow window", header is not null && header.ActualWidth < w.Scroller.ViewportWidth, $"{header?.ActualWidth:0} in {w.Scroller.ViewportWidth:0}");
                await Resize(1300, 900);
                vm.OpenVendorsCommand.Execute(null);
                foreach (var g in vm.Vendors!.Groups.ToList()) g.DeleteCommand.Execute(null);
                vm.Vendors.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("no groups: no sections", !vm.HasGroups && vm.CardsView.Groups is null && vm.Config.Vendors.All(v => v.GroupId is null));
                CheckCardsFit("groups removed");

                // Leave one group for the rest of the run, so later steps exercise sectioned layouts too.
                vm.OpenVendorsCommand.Execute(null);
                vm.Vendors!.NewGroupName = "Critical";
                vm.Vendors.AddGroupCommand.Execute(null);
                foreach (var id in new[] { "github", "openai" }) vm.Vendors.Vendors.Single(v => v.Config.Id == id).Group = vm.Vendors.Groups[0];
                vm.Vendors.SaveCommand.Execute(null);
                await WaitRefresh();
            });

            await Step("Component filtering: only the components we use", async () =>
            {
                vm.OpenVendorsCommand.Execute(null);
                await Settle();
                var row = vm.Vendors!.Vendors.Single(v => v.Config.Id == "cloudflare");
                vm.Vendors.SearchText = "cloudflare";
                row.IsExpanded = true;
                row.FilterComponents = true;
                for (var i = 0; i < 300 && (row.IsLoadingComponents || row.ComponentSources.Count == 0); i++) await Task.Delay(100);
                var src = row.ComponentSources.FirstOrDefault();
                Check("components load from the vendor", src is { HasComponents: true } && src.All.Count > 20, $"{src?.All.Count} components, {row.ComponentsMessage}");
                if (src is null || !src.HasComponents) return;
                src.Search = "API";
                Check("component search narrows the list", src.Shown.Count > 0 && src.Shown.All(c => c.Name.Contains("API", StringComparison.OrdinalIgnoreCase)), $"{src.Shown.Count} shown");
                src.Search = "";
                var pick = src.All.First(c => c.Status == Core.ServiceStatus.Operational);
                pick.Selected = true;
                Check("picker counts the choice", src.SelectedText.StartsWith("1 of") && row.RowSummary.Contains("1 component chosen"), row.RowSummary);
                await Settle();
                await Shot("15a-component-picker");
                vm.Vendors.SaveCommand.Execute(null);
                await WaitRefresh();
                var card = vm.Cards.Single(c => c.Id == "cloudflare");
                Check("saved only the chosen component", vm.Config.Vendors.Single(v => v.Id == "cloudflare").Sources[0].Components is [var only] && only == pick.Name);
                Check("vendor status follows only that component", card.Status == Core.ServiceStatus.Operational && card.IsFiltered, card.Status.ToString());
                Check("components tab lists just it", card.AllComponents.Count == 1 && card.ComponentsTabText == "Components (1)", card.ComponentsTabText);
                ClickCard(card);
                await Settle();
                card.ShowComponents = true;
                await Settle();
                Check("filter note shown in details", TextsUnder(w.DetailsPanel).Any(t => t.Contains("only the components you chose")));
                await Shot("15b-components-tab");
                card.ShowComponents = false;
                vm.CloseDetailsCommand.Execute(null);

                // Back to all components.
                vm.OpenVendorsCommand.Execute(null);
                vm.Vendors!.Vendors.Single(v => v.Config.Id == "cloudflare").FilterComponents = false;
                vm.Vendors.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("filter removed", vm.Config.Vendors.Single(v => v.Id == "cloudflare").Sources.All(s => s.Components is null) && !vm.Cards.Single(c => c.Id == "cloudflare").IsFiltered);
                var github = vm.Cards.Single(c => c.Id == "github");
                ClickCard(github);
                await Settle();
                github.ShowComponents = true;
                github.ComponentSearch = "Actions";
                await Settle();
                Check("unfiltered components tab searches", github.ComponentsShown.Count > 0 && github.ComponentsShown.All(c => c.Name.Contains("Actions")), $"{github.ComponentsShown.Count}");
                CheckNoClippedText("components tab", w.DetailsPanel);
                github.ComponentSearch = "";
                github.ShowComponents = false;
                vm.CloseDetailsCommand.Execute(null);
            });

            await Step("Website and ping checks", async () =>
            {
                async Task<bool> Add(string kind, string address, string name)
                {
                    vm.OpenVendorsCommand.Execute(null);
                    var p = vm.Vendors!;
                    p.ShowAddVendorCommand.Execute(null);
                    var a = p.AddVendor!;
                    a.Kind = kind;
                    a.Url = address;
                    a.CheckCommand.Execute(null);
                    await Task.Delay(50);
                    for (var i = 0; i < 200 && a.IsChecking; i++) await Task.Delay(100);
                    _log.AppendLine($"  INFO  test of {address}: {a.Message}");
                    a.Name = name;
                    var ok = a.CheckSucceeded && a.AddCommand.CanExecute(null);
                    if (ok) a.AddCommand.Execute(null);
                    p.SaveCommand.Execute(null);
                    await WaitRefresh();
                    return ok;
                }
                Check("ping check added", await Add(AddVendorViewModel.KindPing, "127.0.0.1", "This PC"));
                Check("website check added", await Add(AddVendorViewModel.KindWebsite, "https://www.githubstatus.com", "GitHub Status site"));
                Check("check of a host that doesn't exist can still be added", await Add(AddVendorViewModel.KindPing, "no-such-host.invalid", "Broken host"));
                var local = vm.Cards.Single(c => c.Name == "This PC");
                var site = vm.Cards.Single(c => c.Name == "GitHub Status site");
                var broken = vm.Cards.Single(c => c.Name == "Broken host");
                Check("local ping is up with a reading", local.Status == Core.ServiceStatus.Operational && local.ShortHeadline.StartsWith("Up · "), local.ShortHeadline);
                Check("website is up with a reading", site.Status == Core.ServiceStatus.Operational && site.ShortHeadline.StartsWith("Up · "), site.ShortHeadline);
                Check("one failure isn't an outage yet", broken.Status == Core.ServiceStatus.Operational && broken.ShortHeadline.StartsWith("Check failed (1 of 3)"), broken.ShortHeadline);
                await vm.RunChecksAsync();
                await vm.RunChecksAsync();
                Check("down after three failures in a row", broken.Status == Core.ServiceStatus.MajorOutage && broken.Headline.Contains("1 active incident"), $"{broken.Status}: {broken.ShortHeadline}");
                Check("checks are marked as checks", local.IsCheck && broken.IsCheck && !vm.Cards.Single(c => c.Id == "github").IsCheck);
                w.Scroller.ScrollToTop();
                CheckCardsFit("with checks");
                await Shot("16-checks");

                // Clean up: remove the three checks.
                vm.OpenVendorsCommand.Execute(null);
                foreach (var r in vm.Vendors!.Vendors.Where(v => v.Name is "This PC" or "GitHub Status site" or "Broken host").ToList()) r.RemoveCommand.Execute(null);
                vm.Vendors.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("checks removed", vm.Cards.All(c => !c.IsCheck));
            });

            await Step("List view", async () =>
            {
                vm.IsListView = true;
                await Settle();
                w.Scroller.ScrollToTop();
                Check("list view saved", vm.Config.BoardView == "List");
                CheckNoClippedText("list view");
                ClickCard(vm.Cards[0]);
                await Settle();
                Check("rows open details", vm.Selected == vm.Cards[0]);
                await Shot("17a-list-view");
                vm.CloseDetailsCommand.Execute(null);
                await Resize(460, 820);
                CheckNoClippedText("list view, narrow");
                await Shot("17b-list-narrow");
                await Resize(1300, 900);
                vm.IsListView = false;
                await Settle();
                Check("back to tiles", vm.Config.BoardView == "Tiles");
                CheckCardsFit("tiles after list");
            });

            await Step("TV mode", async () =>
            {
                MainWindow.TvPause = TimeSpan.FromMilliseconds(400);
                MainWindow.TvTopPause = TimeSpan.FromMilliseconds(700);
                MainWindow.TvIdle = TimeSpan.FromMilliseconds(600);
                var scale = vm.TileScale;
                vm.IsTvMode = true;
                await Settle();
                Check("rests at the top before scrolling, nothing held", w.TvScrolling && w.Scroller.VerticalOffset < 0.5 && w.StickyShift == (0, 0));
                // Scroll well past the headline: the counters, then the ticker, are held at the top of the board.
                double ViewTop(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), w.Scroller).Y;
                var reach = w.StatusBar.TickerStrip.TranslatePoint(new Point(0, 0), w.ScrollContent).Y + 200;
                w.Scroller.ScrollToVerticalOffset(Math.Min(reach, w.Scroller.ScrollableHeight));
                await Settle();
                var countersTop = ViewTop(w.StatusBar.CountersBlock);
                Check("counters held at the top", w.StickyShift.Counters > 0 && Math.Abs(countersTop - w.Scroller.Padding.Top) < 1, $"at {countersTop:0}, shift {w.StickyShift.Counters:0}");
                Check("ticker held just beneath them", !vm.ShowTicker || Math.Abs(ViewTop(w.StatusBar.TickerStrip) - (countersTop + w.StatusBar.CountersBlock.ActualHeight)) < 1,
                    $"{ViewTop(w.StatusBar.TickerStrip):0} vs {countersTop + w.StatusBar.CountersBlock.ActualHeight:0}");
                Check("tiles pass behind them", w.StatusBar.CountersBlock.Background != null && Panel.GetZIndex(w.StatusBar) > Panel.GetZIndex(w.CardsHost));
                await Task.Delay(700);
                Check("still held while it keeps scrolling", Math.Abs(ViewTop(w.StatusBar.CountersBlock) - countersTop) < 1, $"{ViewTop(w.StatusBar.CountersBlock):0}");
                Check("ticker still running", w.StatusBar.TickerScrolling || !vm.ShowTicker);
                await Shot("18c-tv-sticky");
                Check("buttons fade after the mouse is idle", w.TvControlsHidden && w.TvControls.Opacity < 0.5 && w.Cursor == Cursors.None);
                w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.LeftCtrl) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                await Task.Delay(300);
                Check("a key brings them back", !w.TvControlsHidden && w.TvControls.Opacity > 0.9 && w.Cursor != Cursors.None);
                // Near the bottom: rest, glide back up (not jump), then rest at the top.
                w.Scroller.ScrollToVerticalOffset(Math.Max(0, w.Scroller.ScrollableHeight - 3));
                var (glided, topAt) = (false, DateTime.MinValue);
                for (var i = 0; i < 120 && topAt == DateTime.MinValue; i++)
                {
                    await Task.Delay(40);
                    if (w.TvReturning && w.Scroller.VerticalOffset > 1 && w.Scroller.VerticalOffset < w.Scroller.ScrollableHeight - 5) glided = true;
                    if (!w.TvReturning && glided && w.Scroller.VerticalOffset < 0.5) topAt = DateTime.UtcNow;
                }
                Check("glides back to the top after the bottom rest", w.Scroller.ScrollableHeight <= 0 || (glided && topAt != DateTime.MinValue));
                await Task.Delay(400);
                Check("then rests at the top, everything back in place", w.Scroller.ScrollableHeight <= 0 || (w.Scroller.VerticalOffset < 0.5 && w.StickyShift == (0, 0)
                    && w.StatusBar.CountersBlock.Background == null), $"{w.Scroller.VerticalOffset:0}");
                await Task.Delay(900);
                Check("and starts the next pass", w.Scroller.ScrollableHeight <= 0 || w.Scroller.VerticalOffset > 1, $"{w.Scroller.VerticalOffset:0}");
                MainWindow.TvTopPause = TimeSpan.FromMilliseconds(400);
                Check("full screen and borderless", w.WindowStyle == WindowStyle.None && w.WindowState == WindowState.Maximized);
                Check("controls hidden, exit shown", !w.TvButton.IsVisible && !w.VendorsButton.IsVisible && w.ExitTvButton.IsVisible);
                Check("tiles larger and read-only", vm.TileScale > scale * 1.2 && !w.CardsHost.IsHitTestVisible, $"{scale:0.00} -> {vm.TileScale:0.00}");
                Check("auto-scroll running", w.TvScrolling);
                var start = w.Scroller.VerticalOffset;
                await Task.Delay(1800);
                Check("board scrolls by itself", w.Scroller.ScrollableHeight <= 0 || w.Scroller.VerticalOffset > start + 10, $"{start:0} -> {w.Scroller.VerticalOffset:0} of {w.Scroller.ScrollableHeight:0}");
                Check("scroll toggle shown, on by default", w.TvScrollToggle.IsVisible && w.TvScrollToggle.IsChecked == true && vm.TvScrollText == "Auto-scroll on");
                w.TvScrollToggle.IsChecked = false; // click the toggle
                await Settle();
                var still = w.Scroller.VerticalOffset;
                await Task.Delay(1200);
                Check("toggle off: the board stays still", !vm.TvAutoScroll && !w.TvScrolling && Math.Abs(w.Scroller.VerticalOffset - still) < 0.5 && vm.TvScrollText == "Auto-scroll off",
                    $"{still:0} -> {w.Scroller.VerticalOffset:0}");
                Check("the choice is saved", File.ReadAllText(Core.ConfigStore.NextToExecutable().Path).Contains("\"tvAutoScroll\": false"));
                await Shot("18b-tv-scroll-off");
                var source = PresentationSource.FromVisual(w)!;
                w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.S) { RoutedEvent = Keyboard.KeyDownEvent });
                await Settle();
                Check("S turns it back on", vm.TvAutoScroll && w.TvScrolling && w.TvScrollToggle.IsChecked == true);
                var resumed = w.Scroller.VerticalOffset;
                await Task.Delay(1500);
                Check("and it scrolls again", w.Scroller.ScrollableHeight <= 0 || w.Scroller.VerticalOffset > resumed + 5 || w.Scroller.VerticalOffset < resumed, $"{resumed:0} -> {w.Scroller.VerticalOffset:0}");
                vm.TvAutoScroll = false;
                vm.IsTvMode = false;
                await Settle();
                vm.IsTvMode = true;
                await Settle();
                Check("off is remembered next time TV mode starts", !w.TvScrolling && w.TvScrollToggle.IsChecked == false);
                vm.TvAutoScroll = true;

                await Shot("18-tv-mode");
                PressEscape();
                await Settle();
                Check("Esc leaves TV mode and restores the window", !vm.IsTvMode && w.WindowStyle != WindowStyle.None && w.WindowState != WindowState.Maximized && !w.TvScrolling);
                Check("tile size back", Math.Abs(vm.TileScale - scale) < 0.001);
                Check("nothing held, buttons and pointer restored", w.StickyShift == (0, 0) && w.Cursor != Cursors.None && !w.TvControlsHidden);
                MainWindow.TvPause = TimeSpan.FromSeconds(8);
                MainWindow.TvTopPause = TimeSpan.FromSeconds(12);
                MainWindow.TvIdle = TimeSpan.FromSeconds(10);
                await Resize(1300, 900);
            });

            await Step("No toast notifications; Default selection; Sage", async () =>
            {
                vm.OpenSettingsCommand.Execute(null);
                await Settle();
                Check("no notification option in Settings", !TextsUnder(w.Drawer).Any(t => t.Contains("Notify", StringComparison.OrdinalIgnoreCase)));
                Check("tray icon option still there", TextsUnder(w.Drawer).Any(t => t.Contains("notification area")));
                vm.Settings!.CancelCommand.Execute(null);
                Check("settings file has no notifications setting", !File.ReadAllText(Core.ConfigStore.NextToExecutable().Path).Contains("\"notifications\""));

                var sage = vm.Cards.FirstOrDefault(c => c.Id == "sage");
                Check("Sage is on the default board and reads live", sage is { Data: not null } && sage.Status != Core.ServiceStatus.Unknown && sage.AllComponents.Count > 10,
                    $"{sage?.Status}, {sage?.AllComponents.Count} components");

                vm.OpenVendorsCommand.Execute(null);
                await Settle();
                var p = vm.Vendors!;
                p.UnwatchShownCommand.Execute(null);
                p.DefaultSelectionCommand.Execute(null);
                Check("Default button watches exactly the default 60", p.WatchingCount == 60 && p.Vendors.Where(v => v.Enabled).All(v => Core.DefaultVendors.DefaultIds.Contains(v.Config.Id)),
                    p.WatchedSummary);
                Check("Default button is next to Add vendor", w.VendorsView.DefaultSelectionButton.IsVisible);
                await Shot("21a-vendors-default");
                p.CancelCommand.Execute(null);
                Check("cancel leaves the board as it was", vm.Cards.Count == vm.Config.Vendors.Count(v => v.Enabled));
            });

            await Step("Header clock and status boxes", async () =>
            {
                await Settle();
                Check("no app title in the header", !TextsUnder(w.HeaderGrid).Contains("Status Board"));
                Check("date and time shown", w.ClockPanel.IsVisible && w.ClockDate.IsVisible && w.ClockTime.Text == DateTime.Now.ToString("HH:mm") && w.ClockDate.Text.Contains(DateTime.Now.Year.ToString()),
                    $"{w.ClockTime.Text} / {w.ClockDate.Text}");
                var mid = w.ClockPanel.TranslatePoint(new Point(w.ClockPanel.ActualWidth / 2, 0), w).X;
                Check("clock is in the middle", Math.Abs(mid - w.ActualWidth / 2) < 30, $"centre {mid:0} of {w.ActualWidth:0}");
                var counters = FindDescendants<Views.Counter>(w.StatusBar).Select(c => c.Label).ToList();
                Check("status boxes include Maintenance and Couldn't check", counters.SequenceEqual(["Operational", "Major outage", "Partial outage", "Degraded", "Maintenance", "Couldn't check"]),
                    string.Join(", ", counters));
                Check("maintenance count matches tiles", vm.MaintenanceCount == vm.Cards.Count(c => c.Status == Core.ServiceStatus.Maintenance));
                await Shot("19a-header-clock");
                bool NoOverlap()
                {
                    Rect R(FrameworkElement e) => e.TransformToAncestor(w).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
                    var clock = R(w.ClockPanel);
                    return !clock.IntersectsWith(R(w.HeaderControls)) && !clock.IntersectsWith(R(w.ProfileButton));
                }
                Check("clock never overlaps the buttons (1300 px)", NoOverlap());
                await Resize(1900, 1000);
                Check("wide: one line, centred", w.ClockInline && w.ClockPanel.IsVisible && NoOverlap());
                await Shot("19b-header-wide");
                foreach (var width in new[] { 700, 420 })
                {
                    await Resize(width, 820);
                    var c = w.ClockPanel.TranslatePoint(new Point(w.ClockPanel.ActualWidth / 2, 0), w).X;
                    Check($"{width} px: clock still shown, centred, above the buttons", w.ClockPanel.IsVisible && !w.ClockInline && Math.Abs(c - w.ActualWidth / 2) < 30 && NoOverlap(),
                        $"centre {c:0} of {w.ActualWidth:0}, date {(w.ClockDate.IsVisible ? "shown" : "hidden")}");
                    CheckNoClippedText($"header at {width} px", w.HeaderGrid);
                }
                await Shot("19c-header-narrow");
                await Resize(1300, 900);
                Check("back to normal width", w.ClockPanel.IsVisible && w.ClockDate.IsVisible);
                vm.IsTvMode = true;
                await Settle();
                Check("TV mode: clock and date on one centred line", w.ClockPanel.IsVisible && w.ClockDate.IsVisible && w.ClockInline && NoOverlap());
                vm.IsTvMode = false;
                await Settle();
            });

            await Step("Themes: AMOLED and Hacker", async () =>
            {
                foreach (var t in new[] { "AMOLED", "Hacker" })
                {
                    vm.OpenSettingsCommand.Execute(null);
                    Check($"{t} offered in the theme menu", vm.Settings!.ThemeOptions.Contains(t));
                    vm.Settings.Theme = t;
                    vm.Settings.SaveCommand.Execute(null);
                    await WaitRefresh();
                    var bg = (app.FindResource("Bg") as SolidColorBrush)?.Color;
                    var font = (app.FindResource("UiFont") as FontFamily)?.Source ?? "";
                    Check($"{t}: true black background", bg == Colors.Black, bg?.ToString());
                    Check($"{t}: saved", vm.Config.Theme == t && theme.Current == t && theme.IsDark);
                    if (t == "Hacker")
                    {
                        Check("Hacker: monospace font everywhere", font.StartsWith("Cascadia Mono") && w.FontFamily.Source.StartsWith("Cascadia Mono") && w.ClockTime.FontFamily.Source.StartsWith("Cascadia Mono"), font);
                        Check("Hacker: green text", (app.FindResource("Text") as SolidColorBrush)?.Color.G > 200);
                    }
                    else Check("AMOLED: no grey panels", (app.FindResource("Surface") as SolidColorBrush)?.Color == Colors.Black);
                    w.Scroller.ScrollToTop();
                    CheckCardsFit($"{t} theme");
                    await Shot($"19-theme-{t.ToLowerInvariant()}");
                }
                vm.OpenSettingsCommand.Execute(null);
                vm.Settings!.Theme = "Dark";
                vm.Settings.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("back to Dark with the normal font", theme.Current == "Dark" && !((app.FindResource("UiFont") as FontFamily)?.Source ?? "").Contains("Mono"));
            });

            await Step("Profiles and the setup wizard", async () =>
            {
                var store = vm.ProfileStore!;
                Check("existing settings became the Default profile", store.Profiles.Count == 1 && store.Active?.Name == "Default" && store.Active.File == Core.ConfigStore.FileName,
                    string.Join(", ", store.Profiles.Select(p => $"{p.Name}:{p.File}")));
                Check("profile shown top left", vm.ProfileName == "Default" && w.ProfileButton.IsVisible);
                var defaultCount = vm.Cards.Count;

                // New profile, the long way: every step, including trying to skip ahead.
                vm.NewProfileCommand.Execute(null);
                await Settle();
                var s = vm.Setup!;
                Check("wizard opens on the name step", vm.IsSetupOpen && s.IsNameStep && !s.IsFirstRun && w.SetupView.IsVisible);
                await Shot("20a-setup-name");
                s.NextCommand.Execute(null);
                Check("can't continue without a name", s.IsNameStep && s.Error is not null, s.Error);
                s.Name = "  Office TV  ";
                s.NextCommand.Execute(null);
                Check("vendors step starts empty", s.IsVendorsStep && s.SelectedCount == 0 && s.Vendors.Count == Core.DefaultVendors.Create().Count, $"{s.SelectedCount} of {s.Vendors.Count}");
                s.NextCommand.Execute(null);
                Check("can't continue with nothing chosen", s.IsVendorsStep && s.Error is not null);
                Check("step bar shows progress", s.StepItems.Count(i => i.Reached) == 2 && s.StepItems[1].Current);
                s.SelectPopularCommand.Execute(null);
                Check("the hint clears once vendors are chosen", s.Error is null);
                Check("Default ticks the default 60", s.SelectedCount == 60 && w.SetupView.DefaultButton.Content as string == "Default", s.SelectedSummary);
                s.ClearCommand.Execute(null);
                s.Search = "micro";
                var shown = s.VendorsView.Cast<SetupVendorViewModel>().ToList();
                Check("search in setup", shown.Count >= 2 && shown.All(v => v.Name.Contains("Micro", StringComparison.OrdinalIgnoreCase)), string.Join(", ", shown.Select(v => v.Name)));
                foreach (var v in shown) v.Selected = true;
                s.Search = "";
                s.Vendors.Single(v => v.Config.Id == "sentinelone").Selected = true; // one of the new catalogue vendors
                Check("vendors grouped by category in setup", s.VendorsView.Groups?.Count >= 5);
                await Settle();
                await Shot("20b-setup-vendors");
                s.NextCommand.Execute(null);
                Check("theme step", s.IsThemeStep && s.Themes.Count == 5);
                s.PickThemeCommand.Execute(s.Themes.Single(t => t.Name == "Hacker"));
                await Settle();
                Check("theme previews live", theme.Current == "Hacker");
                await Shot("20c-setup-theme");
                s.BackCommand.Execute(null);
                Check("back keeps choices", s.IsVendorsStep && s.SelectedCount == shown.Count + 1);
                s.NextCommand.Execute(null);
                s.NextCommand.Execute(null);
                Check("done step summarises", s.IsDoneStep && s.Summary.Contains("Office TV") && s.Summary.Contains("Hacker"), s.Summary);
                await Shot("20d-setup-done");
                s.FinishCommand.Execute(null);
                await WaitRefresh();
                Check("new profile created and active", !vm.IsSetupOpen && store.Profiles.Count == 2 && store.Active?.Name == "Office TV" && vm.ProfileName == "Office TV");
                Check("new profile has only its choices", vm.Cards.Count == shown.Count + 1 && vm.Cards.Any(c => c.Id == "sentinelone"), $"{vm.Cards.Count} cards");
                Check("new profile has its theme", vm.Config.Theme == "Hacker" && theme.Current == "Hacker");
                Check("new profile has its own file", store.Active!.File.StartsWith("profile-office-tv-") && File.Exists(Path.Combine(AppContext.BaseDirectory, store.Active.File)), store.Active.File);
                w.Scroller.ScrollToTop();
                await Shot("20e-new-profile-board");

                // Switch back, rename, cancel a wizard, delete.
                vm.SwitchProfile(store.Profiles.Single(p => p.Name == "Default"));
                await WaitRefresh();
                Check("switching restores the other board and its theme", vm.ProfileName == "Default" && vm.Cards.Count == defaultCount && theme.Current == "Dark", $"{vm.Cards.Count} cards, {theme.Current}");
                vm.RefreshProfileRows();
                var row = vm.ProfileRows.Single(r => r.Name == "Office TV");
                row.Name = "default"; // clashes with "Default"
                Check("rename keeps names unique", row.Name == "default (2)" && store.Profiles.Any(p => p.Name == "default (2)"), row.Name);
                row.Name = new string('x', 80);
                Check("rename trims long names", row.Name.Length == Core.ProfileStore.MaxNameLength);
                vm.NewProfileCommand.Execute(null);
                vm.Setup!.PickThemeCommand.Execute(vm.Setup.Themes.Single(t => t.Name == "Light"));
                PressEscape();
                await Settle();
                Check("Esc cancels the wizard and undoes the preview", !vm.IsSetupOpen && theme.Current == "Dark" && store.Profiles.Count == 2);
                vm.OpenSettingsCommand.Execute(null);
                await Settle();
                Check("profiles listed in settings", vm.ProfileRows.Count == 2 && vm.ProfileRows.Count(r => r.IsActive) == 1);
                await Shot("20f-settings-profiles");
                var other = vm.ProfileRows.Single(r => !r.IsActive);
                var file = other.FileName;
                other.DeleteCommand.Execute(null);
                Check("deleting a profile removes it and its file", store.Profiles.Count == 1 && !File.Exists(Path.Combine(AppContext.BaseDirectory, file)));
                Check("the last profile can't be deleted", !vm.ProfileRows.Single().DeleteCommand.CanExecute(null));
                vm.Settings?.CancelCommand.Execute(null);

                // A broken profiles.json must not stop the app: it falls back to the files that exist.
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, Core.ProfileStore.IndexFile), "{ not json");
                var reloaded = new Core.ProfileStore(AppContext.BaseDirectory).Load();
                Check("corrupt profiles.json recovers", reloaded.Profiles.Count == 1 && reloaded.Active?.File == Core.ConfigStore.FileName);
            });

            await Step("Try to break it: bad logo files", async () =>
            {
                var dir = Path.Combine(outDir, "bad-logos");
                Directory.CreateDirectory(dir);
                string F(string name, byte[] bytes) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, bytes); return p; }
                vm.OpenSettingsCommand.Execute(null);
                await Settle();
                var s = vm.Settings!;
                var target = s.LogoVendors.First(l => l.Config.Id == "github");
                s.LogoVendor = target;

                Check("text file named .png is refused", !s.ImportLogo(F("not-an-image.png", Encoding.UTF8.GetBytes("hello, I am not a picture"))) && s.LogoMessageIsError && s.LogoMessage!.Contains("isn't an image"), s.LogoMessage);
                Check("empty file is refused", !s.ImportLogo(F("empty.png", [])) && s.LogoMessageIsError, s.LogoMessage);
                Check("missing file is refused", !s.ImportLogo(Path.Combine(dir, "does-not-exist.png")) && s.LogoMessageIsError, s.LogoMessage);
                Check("SVG is refused with advice", !s.ImportLogo(F("logo.svg", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><rect width='10' height='10'/></svg>"))) && s.LogoMessage!.Contains("SVG"), s.LogoMessage);
                Check("truncated PNG is refused", !s.ImportLogo(F("truncated.png", SampleLogoPng()[..60])) && s.LogoMessageIsError, s.LogoMessage);

                var before = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ok = s.ImportLogo(F("huge.png", BigPng(9000, 7000)));
                sw.Stop();
                GC.Collect();
                var grew = (System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 - before) / (1024 * 1024);
                Check("9000×7000 image imports quickly without a memory spike", ok && sw.Elapsed < TimeSpan.FromSeconds(15) && grew < 250, $"{sw.Elapsed.TotalSeconds:0.0}s, +{grew} MB, {s.LogoMessage}");
                Check("tiny image accepted with a warning", s.ImportLogo(F("tiny.png", SolidPng(4, 4, Colors.DarkBlue))) && s.LogoMessage!.Contains("small"), s.LogoMessage);
                Check("white logo accepted with a warning", s.ImportLogo(F("white.png", SolidPng(120, 80, Colors.White))) && s.LogoMessage!.Contains("white"), s.LogoMessage);
                Check("transparent image accepted with a warning", s.ImportLogo(F("clear.png", SolidPng(120, 80, Colors.Transparent))) && s.LogoMessage!.Contains("empty"), s.LogoMessage);
                CheckNoClippedText("settings with logo messages", w.Drawer);
                await Shot("13a-bad-logo-messages");

                var oldLogo = vm.Config.Vendors.Single(v => v.Id == "github").Logo;
                s.CancelCommand.Execute(null);
                await Settle();
                Check("cancel keeps the old logo", vm.Config.Vendors.Single(v => v.Id == "github").Logo == oldLogo);
                // The next save sweeps the files picked and then cancelled.
                vm.OpenSettingsCommand.Execute(null);
                vm.Settings!.SaveCommand.Execute(null);
                await WaitRefresh();
                var leftovers = Directory.Exists(LogoImporter.Dir) ? Directory.GetFiles(LogoImporter.Dir).Length : 0;
                Check("cancelled uploads are cleaned up", leftovers == vm.Config.Vendors.Count(v => LogoImporter.IsCustom(v.Logo)), $"{leftovers} files");
            });

            await Step("Try to break it: Vendors pane", async () =>
            {
                var watched = vm.Config.Vendors.Where(v => v.Enabled).Select(v => v.Id).ToList();

                // Esc discards edits.
                vm.OpenVendorsCommand.Execute(null);
                await Settle();
                vm.Vendors!.Vendors.First(v => v.Enabled).Enabled = false;
                PressEscape();
                await Settle();
                Check("Esc closes the pane without saving", !vm.IsVendorsOpen && vm.Config.Vendors.Where(v => v.Enabled).Select(v => v.Id).SequenceEqual(watched));

                // Ctrl+wheel must not resize tiles behind an open pane.
                vm.OpenVendorsCommand.Execute(null);
                await Settle();
                var scale = vm.CardScale;
                w.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                Check("tile zoom ignored while the pane is open", Math.Abs(vm.CardScale - scale) < 0.001);

                // Add form: nonsense, then cancel mid-check; add and remove before saving.
                var p = vm.Vendors!;
                p.ShowAddVendorCommand.Execute(null);
                var add = p.AddVendor!;
                Check("Check disabled for an empty address", !add.CheckCommand.CanExecute(null));
                add.Url = ":::: not a web address ::::";
                add.CheckCommand.Execute(null);
                await Task.Delay(50);
                for (var i = 0; i < 300 && add.IsChecking; i++) await Task.Delay(100);
                Check("nonsense address fails gracefully", !add.CheckSucceeded && add.HasMessage && !add.AddCommand.CanExecute(null), add.Message);
                add.Url = "status.helpscout.com";
                add.CheckCommand.Execute(null);
                add.CancelCommand.Execute(null);
                await Settle();
                Check("cancel during a check closes the form", !p.IsAddingVendor);
                var count = p.Vendors.Count;

                // Unwatch everything shown and save: the board must cope with nothing to show.
                p.SearchText = "";
                p.ShowFilter = VendorsViewModel.ShowChoices[0];
                p.CategoryFilter = VendorsViewModel.AllCategories;
                p.UnwatchShownCommand.Execute(null);
                Check("'None' unwatches every vendor", p.WatchingCount == 0, p.WatchedSummary);
                p.SearchText = "zzzz-no-such-vendor";
                Check("no-match message shown", p.NothingShown);
                p.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("empty board says so", vm.HasNoVendors && vm.Cards.Count == 0 && vm.WatchingCount == 0 && vm.SummaryHeadline == "No vendors enabled", vm.SummaryHeadline);
                Check("empty ticker and chips", vm.TickerItems.Count == 0 && vm.Affected.Count == 0);
                await Shot("13b-empty-board");
                vm.RefreshCommand.Execute(null);
                await WaitRefresh();
                Check("refresh with nothing watched is harmless", !vm.IsRefreshing && vm.Cards.Count == 0);

                // Put everything back.
                vm.OpenVendorsCommand.Execute(null);
                foreach (var v in vm.Vendors!.Vendors) v.Enabled = watched.Contains(v.Config.Id);
                Check("vendor list unchanged in size", vm.Vendors.Vendors.Count == count);
                vm.Vendors.SaveCommand.Execute(null);
                await WaitRefresh();
                await vm.LoadLogosAsync();
                Check("watched vendors restored", vm.Cards.Count == watched.Count, $"{vm.Cards.Count} of {watched.Count}");

                // Narrow window: the pane takes the full width and nothing spills over.
                await Resize(400, 820);
                vm.OpenVendorsCommand.Execute(null);
                await Settle();
                var forti = vm.Vendors!.Vendors.Single(v => v.Config.Id == "forticloud");
                forti.IsExpanded = true;
                vm.Vendors.SearchText = "FortiCloud";
                await Settle();
                Check("pane fits a narrow window", w.VendorsDrawer.ActualWidth <= w.ActualWidth + 1, $"{w.VendorsDrawer.ActualWidth:0} in {w.ActualWidth:0}");
                CheckNoClippedText("vendors pane, narrow", w.VendorsDrawer);
                await Shot("13c-vendors-narrow");
                vm.Vendors.CancelCommand.Execute(null);
                vm.OpenSettingsCommand.Execute(null);
                await Settle();
                CheckNoClippedText("settings, narrow", w.Drawer);
                await Shot("13d-settings-narrow");
                vm.Settings!.CancelCommand.Execute(null);
                await Resize(1300, 900);
            });

            await Step("Try to break it: odd vendor data", async () =>
            {
                var c = vm.Cards.First(x => x.Id == "github");
                Core.VendorStatus G(params Core.Incident[] i) => new()
                {
                    VendorId = c.Id, VendorName = c.Name, StatusPageUrl = c.StatusPageUrl,
                    Sources = [Core.SourceResult.Of(c.Name, i)],
                };
                var longTitle = string.Concat(Enumerable.Repeat("Extremely long incident title that keeps going ", 100));
                vm.ApplyResults([G(
                    new Core.Incident { Title = "", Status = Core.ServiceStatus.MajorOutage, Stage = Core.IncidentStage.Investigating },
                    new Core.Incident { Title = longTitle, Summary = longTitle, Status = Core.ServiceStatus.Degraded, AffectedComponents = Enumerable.Range(1, 400).Select(n => $"Component {n}").ToList() },
                    new Core.Incident { Title = "Unknown severity", Status = Core.ServiceStatus.Unknown, Stage = (Core.IncidentStage)999 })]);
                await Settle();
                Check("odd incidents don't crash the tile", c.Status == Core.ServiceStatus.MajorOutage && c.StageText == "Investigating", c.StageText);
                CheckCardsFit("odd incident data");
                ClickCard(c);
                await Settle();
                Check("details cope with 400 components and a 4,800-character title", w.DetailsPanel.Visibility == Visibility.Visible);
                CheckNoClippedText("details with odd data", w.DetailsPanel);
                await Shot("13e-odd-data");
                vm.CloseDetailsCommand.Execute(null);

                var cards = vm.Cards.Count;
                vm.ApplyResults([new Core.VendorStatus { VendorId = "not-on-the-board", VendorName = "Ghost", Sources = [] }]);
                Check("results for unknown vendors are ignored", vm.Cards.Count == cards && vm.Cards.All(x => x.Name != "Ghost"));
                vm.ApplyResults([new Core.VendorStatus { VendorId = c.Id, VendorName = c.Name, Sources = [] }]);
                Check("vendor with no sources reads clearly", c.Headline == "No sources enabled" && c.StageText is null, c.Headline);
                await vm.RefreshAsync();
            });

            await Step("Back to Light, 5-minute refresh; refresh button", async () =>
            {
                vm.OpenSettingsCommand.Execute(null);
                vm.Settings!.Theme = "Light";
                vm.Settings.RefreshIntervalMinutes = 5;
                vm.Settings.TrayIcon = false;
                vm.Settings.SaveCommand.Execute(null);
                await WaitRefresh();
                Check("light theme", !theme.IsDark);
                Check("tray removed", !app.TrayVisible);
                Check("auto refresh back on", vm.LastRefreshText.Contains("next in 5 min") || vm.LastRefreshText.Contains("next in 4 min"), vm.LastRefreshText);
                vm.RefreshCommand.Execute(null);
                vm.RefreshCommand.Execute(null); // double click while refreshing must be harmless
                await WaitRefresh();
                Check("refresh completed", !vm.IsRefreshing && vm.Cards.All(c => c.Data is not null));
                await Shot("12-final");
            });
        }
        catch (Exception ex)
        {
            Fail("UNHANDLED " + ex.GetType().Name, ex.ToString());
        }
        finally
        {
            _log.Insert(0, $"Status Board self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {_pass} passed, {_fail} failed{Environment.NewLine}{Environment.NewLine}");
            File.WriteAllText(Path.Combine(outDir, "selftest.txt"), _log.ToString());
            app.Shutdown(_fail == 0 ? 0 : 1);
        }
    }

    // ---------- helpers ----------

    private async Task Step(string name, Func<Task> body)
    {
        _log.AppendLine($"== {name}");
        try { await body(); }
        catch (Exception ex) { Fail("step threw " + ex.GetType().Name, ex.Message); }
    }

    private void Check(string what, bool ok, string? detail = null)
    {
        if (ok) { _pass++; _log.AppendLine($"  PASS  {what}" + (string.IsNullOrEmpty(detail) ? "" : $"  ({detail})")); }
        else Fail(what, detail);
    }

    private void Fail(string what, string? detail)
    {
        _fail++;
        _log.AppendLine($"  FAIL  {what}" + (string.IsNullOrEmpty(detail) ? "" : $"  ({detail})"));
    }

    /// <summary>Width the dashboard should have with no panel: the whole content grid.</summary>
    private double ContentWidth() => w.ContentGrid.ActualWidth;

    private void CheckClosed(string what, double full)
    {
        var ok = vm.Selected is null
                 && w.DetailsPanel.Visibility != Visibility.Visible
                 && w.PanelCol.ActualWidth < 0.5 && w.SplitCol.ActualWidth < 0.5
                 && Math.Abs(w.Scroller.ActualWidth - full) < 1;
        Check(what, ok, $"dashboard {w.Scroller.ActualWidth:0}/{full:0}px, panel col {w.PanelCol.ActualWidth:0}px, splitter col {w.SplitCol.ActualWidth:0}px");
        CheckCardsFit(what);
    }

    /// <summary>No visible text runs past the right edge of the rounded box (card, summary, incident) that contains it.</summary>
    private void CheckNoClippedText(string context, params FrameworkElement[] roots)
    {
        var clipped = new List<string>();
        foreach (var root in roots.Length > 0 ? roots : [w.Scroller, w.DetailsPanel])
        {
            if (root.Visibility != Visibility.Visible) continue;
            foreach (var tb in FindDescendants<TextBlock>(root))
            {
                if (!tb.IsVisible || tb.ActualWidth < 1 || string.IsNullOrWhiteSpace(tb.Text) || tb.FontFamily.Source.Contains("Icons")) continue;
                var box = Ancestors(tb).OfType<Border>().FirstOrDefault(b => b.CornerRadius.TopLeft > 0 && b.ActualWidth > 100);
                if (box is null) continue;
                var right = tb.TransformToAncestor(box).Transform(new Point(tb.ActualWidth, 0)).X;
                if (right > box.ActualWidth + 1) clipped.Add($"“{(tb.Text.Length > 30 ? tb.Text[..30] + "…" : tb.Text)}” {right - box.ActualWidth:0}px over");
            }
        }
        Check($"no clipped text ({context})", clipped.Count == 0, string.Join("; ", clipped.Distinct().Take(5)));
    }

    /// <summary>Text of every visible TextBlock under an element.</summary>
    private static List<string> TextsUnder(DependencyObject root) =>
        FindDescendants<TextBlock>(root).Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text).ToList();

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p)) yield return p;
    }

    /// <summary>The summary headline has real width and the summary bar doesn't balloon.</summary>
    private void CheckSummaryReadable(string context)
    {
        if (w.Scroller.Visibility != Visibility.Visible) return;
        var headline = w.StatusBar.SummaryText.ActualWidth;
        var height = w.StatusBar.SummaryText.ActualHeight; // the headline block; the "needs attention" chips below it may wrap freely
        var refreshShown = w.StatusBar.LastRefresh.IsVisible && w.StatusBar.LastRefresh.ActualWidth > 50;
        Check($"summary readable ({context})", headline >= 200 && height < 140 && refreshShown,
            $"headline area {headline:0}px, headline {height:0}px tall, refresh text {(refreshShown ? "shown" : "missing")}");
    }

    /// <summary>Every card lies inside the visible dashboard area and no two cards overlap.</summary>
    private void CheckCardsFit(string context)
    {
        CheckNoClippedText(context);
        CheckSummaryReadable(context);
        if (w.Scroller.Visibility != Visibility.Visible) return;
        var viewport = w.Scroller.ViewportWidth + w.Scroller.Padding.Left;
        var rects = new List<Rect>();
        foreach (var cardVm in vm.Cards)
        {
            if (w.CardsHost.ItemContainerGenerator.ContainerFromItem(cardVm) is not FrameworkElement c) continue;
            var child = VisualTreeHelper.GetChildrenCount(c) > 0 ? VisualTreeHelper.GetChild(c, 0) as FrameworkElement : null;
            if (child is null) continue;
            // On-screen bounds: tiles are drawn through a scale transform, so measure after it.
            rects.Add(child.TransformToAncestor(w.Scroller).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight)));

        }
        var overflow = rects.Where(r => r.Right > viewport + 1).ToList();
        var overlap = rects.SelectMany((a, i) => rects.Skip(i + 1).Where(b => Rect.Intersect(a, b) is { IsEmpty: false, Width: > 1, Height: > 1 })).Any();
        var widest = rects.Count > 0 ? rects.Max(r => r.Width) : 0;
        Check($"cards fit ({context})", overflow.Count == 0 && !overlap && widest >= 270,
            $"{rects.Count} cards, width {widest:0}px, viewport {viewport:0}px{(overflow.Count > 0 ? $", {overflow.Count} overflow" : "")}{(overlap ? ", overlap" : "")}");
        // Rows must also fill: the widest row should hold as many cards as the width allows (no empty column).
        // Tiles are laid out in scaled units: the host is (viewport + gap) / scale wide.
        var expected = Math.Min(vm.Cards.Count, Math.Max(1, (int)((w.Scroller.ViewportWidth + MainViewModel.Gap) / vm.TileScale / (MainViewModel.MinCardWidth + MainViewModel.Gap))));
        var fullest = rects.Count == 0 ? 0 : rects.GroupBy(r => Math.Round(r.Top / 30)).Max(g => g.Count());
        Check($"rows fill the width ({context})", rects.Count == 0 || fullest == expected, $"{fullest} per row, expected {expected}" +
            (fullest == expected ? "" : $"; host {w.CardsHost.ActualWidth:0}, card {vm.CardWidth:0}, panels {string.Join("/", FindDescendants<WrapPanel>(w.CardsHost).Select(p => p.ActualWidth.ToString("0")))}"));
    }

    private void ClickCard(CardViewModel card)
    {
        var style = w.FindResource("CardButton");
        var container = w.CardsHost.ItemContainerGenerator.ContainerFromItem(card);
        var button = container is null ? null : FindDescendants<Button>(container).FirstOrDefault(b => ReferenceEquals(b.Style, style));
        if (button is null) { Fail($"card button for {card.Name} not found", null); return; }
        Invoke(button);
    }

    /// <summary>
    /// Samples the ticker on every rendered frame: frames per second, the share of frames that arrive late (a visible
    /// stutter), and how much the distance moved per frame varies relative to the average (uneven motion).
    /// </summary>
    private async Task<(double Fps, double LongGaps, double Jitter)> MeasureTickerFrames(TimeSpan duration)
    {
        var samples = new List<(TimeSpan T, double X)>();
        void OnFrame(object? s, EventArgs e)
        {
            if (e is RenderingEventArgs r && (samples.Count == 0 || samples[^1].T != r.RenderingTime)) samples.Add((r.RenderingTime, w.StatusBar.TickerOffset));
        }
        CompositionTarget.Rendering += OnFrame;
        await Task.Delay(duration);
        CompositionTarget.Rendering -= OnFrame;
        if (samples.Count < 3) return (0, 1, 1);

        var gaps = samples.Zip(samples.Skip(1), (a, b) => (b.T - a.T).TotalMilliseconds).ToList();
        var steps = samples.Zip(samples.Skip(1), (a, b) => a.X - b.X).Where(d => d > 0 && d < 200).ToList(); // ignore the loop jump
        var fps = (samples.Count - 1) / (samples[^1].T - samples[0].T).TotalSeconds;
        var late = gaps.Count(g => g > 25) / (double)gaps.Count;
        var mean = steps.Count > 0 ? steps.Average() : 0;
        var jitter = mean > 0 ? Math.Sqrt(steps.Average(d => (d - mean) * (d - mean))) / mean : 1;
        return (fps, late, jitter);
    }

    private IEnumerable<string> TickerTexts() =>
        w.StatusBar.TickerStrip.IsVisible ? FindDescendants<TextBlock>(w.StatusBar.TickerA).Where(t => t.IsVisible).Select(t => t.Text) : [];

    private IEnumerable<Button> TickerButtons() => FindDescendants<Button>(w.StatusBar.TickerA);

    private static Button? FindButton(DependencyObject root, string automationName) =>
        FindDescendants<Button>(root).FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == automationName);

    private void Invoke(Button? b)
    {
        if (b is null) { Fail("button not found", null); return; }
        ((IInvokeProvider)new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke)!).Invoke();
    }

    private void PressEscape()
    {
        var source = PresentationSource.FromVisual(w)!;
        w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    /// <summary>Simulates dragging the splitter by dx pixels (negative = left, widening the panel).</summary>
    private void DragSplitter(double dx)
    {
        var s = w.Splitter;
        s.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        s.RaiseEvent(new DragDeltaEventArgs(dx, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        s.RaiseEvent(new DragCompletedEventArgs(dx, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    private async Task Resize(double width, double height)
    {
        w.Width = width;
        w.Height = height;
        await Settle();
    }

    private static byte[] Png(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] SolidPng(int w, int h, Color c)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) (px[i], px[i + 1], px[i + 2], px[i + 3]) = (c.B, c.G, c.R, c.A);
        return Png(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4));
    }

    /// <summary>A very large black-and-white image (1 bit per pixel, so it's cheap to make) with a mark in the middle.</summary>
    private static byte[] BigPng(int w, int h)
    {
        var stride = (w + 7) / 8;
        var px = new byte[stride * h];
        Array.Fill(px, (byte)0xFF); // white
        for (var y = h / 3; y < 2 * h / 3; y++)
            for (var x = stride / 3; x < 2 * stride / 3; x++) px[y * stride + x] = 0; // black block
        return Png(BitmapSource.Create(w, h, 96, 96, PixelFormats.BlackWhite, null, px, stride));
    }

    /// <summary>A test logo: a teal mark off-centre on a large transparent canvas, so trimming and centring are exercised.</summary>
    private static byte[] SampleLogoPng()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0B, 0x85, 0x77)), null, new Point(90, 70), 40, 40);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1D, 0x5F, 0xD1)), null, new Rect(110, 80, 70, 40));
        }
        var bmp = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private async Task WaitRefresh()
    {
        await Task.Delay(50);
        while (vm.IsRefreshing) await Task.Delay(100);
        await Settle();
    }

    private async Task Settle()
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(120);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private async Task Shot(string name)
    {
        await Settle();
        var root = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(w.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(outDir, $"{++_shot:00}-{name[3..]}.png"));
        enc.Save(fs);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindDescendants<T>(child)) yield return d;
        }
    }
}
