using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sb-profiles");
    public void Dispose() => _dir.Delete(true);
    private string F(string name) => Path.Combine(_dir.FullName, name);

    [Fact]
    public void A_new_copy_needs_setup_and_writes_nothing()
    {
        var s = new ProfileStore(_dir.FullName).Load();
        Assert.True(s.NeedsSetup);
        Assert.Null(s.Active);
        Assert.Empty(_dir.GetFiles());
    }

    [Fact]
    public void An_existing_statusboard_json_becomes_Default()
    {
        new ConfigStore(F(ConfigStore.FileName)).Save(AppConfig.CreateDefault());
        var s = new ProfileStore(_dir.FullName).Load();
        Assert.False(s.NeedsSetup);
        Assert.Equal("Default", s.Active!.Name);
        Assert.Equal(ConfigStore.FileName, s.Active.File);
        Assert.True(File.Exists(F(ProfileStore.IndexFile)));
    }

    [Fact]
    public void First_profile_uses_statusboard_json_and_later_ones_their_own_files()
    {
        var s = new ProfileStore(_dir.FullName).Load();
        var first = s.Create("Service desk", new AppConfig { Theme = "Dark" });
        Assert.Equal(ConfigStore.FileName, first.File);
        var second = s.Create("Office TV!", new AppConfig { Theme = "Hacker" });
        Assert.StartsWith("profile-office-tv-", second.File);
        Assert.Equal(second, s.Active);
        Assert.Equal("Hacker", s.StoreFor(second).Load().Theme);

        var again = new ProfileStore(_dir.FullName).Load();       // survives a restart
        Assert.Equal(["Service desk", "Office TV!"], again.Profiles.Select(p => p.Name));
        Assert.Equal("Office TV!", again.Active!.Name);
    }

    [Fact]
    public void Names_are_cleaned_and_kept_unique()
    {
        var s = new ProfileStore(_dir.FullName).Load();
        Assert.Equal("Default", s.Create("   ", new AppConfig()).Name);
        Assert.Equal("default (2)", s.Create("default", new AppConfig()).Name); // your spelling, made unique
        var p = s.Create("A\tB\nC", new AppConfig());
        Assert.Equal("ABC", p.Name);
        s.Rename(p, new string('z', 100));
        Assert.Equal(ProfileStore.MaxNameLength, p.Name.Length);
        s.Rename(p, "Default");
        Assert.Equal("Default (3)", p.Name);
    }

    [Fact]
    public void Delete_removes_the_file_switches_away_and_keeps_the_last()
    {
        var s = new ProfileStore(_dir.FullName).Load();
        var a = s.Create("A", new AppConfig());
        var b = s.Create("B", new AppConfig());
        Assert.True(s.Delete(b));                     // the active one
        Assert.False(File.Exists(F(b.File)));
        Assert.Equal(a, s.Active);
        Assert.False(s.Delete(a));                    // last one stays
        Assert.Single(s.Profiles);
    }

    [Fact]
    public void A_damaged_index_or_missing_files_are_survivable()
    {
        var s = new ProfileStore(_dir.FullName).Load();
        s.Create("A", new AppConfig());
        var b = s.Create("B", new AppConfig());
        File.Delete(F(b.File));                       // file removed by hand
        var reloaded = new ProfileStore(_dir.FullName).Load();
        Assert.Equal(["A"], reloaded.Profiles.Select(p => p.Name));
        Assert.Equal("A", reloaded.Active!.Name);

        File.WriteAllText(F(ProfileStore.IndexFile), "{ broken");
        Assert.Equal(ConfigStore.FileName, new ProfileStore(_dir.FullName).Load().Active!.File);

        File.WriteAllText(F(ProfileStore.IndexFile), """{ "active": "x", "profiles": [ { "id": "x", "name": "Evil", "file": "..\\..\\windows.json" }, null ] }""");
        var safe = new ProfileStore(_dir.FullName).Load();
        Assert.DoesNotContain(safe.Profiles, p => p.File.Contains(".."));   // never reads outside the app folder
    }

    [Fact]
    public void New_catalogue_vendors_reach_existing_configs_switched_off()
    {
        var old = AppConfig.CreateDefault();
        old.Vendors.RemoveAll(v => v.Id == "sentinelone");
        old.DefaultsVersion = 1;
        Assert.True(DefaultVendors.Upgrade(old));
        var added = old.Vendors.Single(v => v.Id == "sentinelone");
        Assert.False(added.Enabled);
        Assert.Equal(DefaultVendors.Version, old.DefaultsVersion);
    }

    [Fact]
    public void The_default_selection_is_60_and_includes_the_old_defaults_and_Sage()
    {
        Assert.Equal(60, DefaultVendors.DefaultIds.Count);           // 2, 3, 4, 5 or 6 tiles per row: no ragged last row
        foreach (var id in new[] { "azure", "m365", "aws", "forticloud", "github", "sage", "huntress", "sentinelone", "pax8" })
            Assert.Contains(id, DefaultVendors.DefaultIds);
        Assert.Equal(60, AppConfig.CreateDefault().Vendors.Count(v => v.Enabled));
    }

    [Fact]
    public void Default_on_vendors_new_to_a_board_still_arrive_switched_off()
    {
        var board = AppConfig.CreateDefault();
        board.Vendors.RemoveAll(v => v.Id is "sage" or "huntress");
        board.DefaultsVersion = 2;
        DefaultVendors.Upgrade(board);
        Assert.False(board.Vendors.Single(v => v.Id == "sage").Enabled);
        Assert.False(board.Vendors.Single(v => v.Id == "huntress").Enabled);
    }

    [Fact]
    public void Old_settings_with_the_removed_notifications_option_still_load()
    {
        File.WriteAllText(F(ConfigStore.FileName), """{ "defaultsVersion": 99, "notifications": true, "trayIcon": true, "vendors": [] }""");
        var store = new ConfigStore(F(ConfigStore.FileName));
        var cfg = store.Load();
        Assert.Null(store.LoadWarning);
        Assert.True(cfg.TrayIcon);
        store.Save(cfg);
        Assert.DoesNotContain("notifications", File.ReadAllText(F(ConfigStore.FileName)));
    }

    [Fact]
    public void Catalogue_ids_and_sources_are_unique()
    {
        var all = DefaultVendors.Create();
        Assert.Equal(all.Count, all.Select(v => v.Id).Distinct().Count());
        var urls = all.SelectMany(v => v.Sources).Select(s => s.Url.TrimEnd('/').ToLowerInvariant()).ToList();
        Assert.Equal(urls.Count, urls.Distinct().Count());
        Assert.True(all.Count >= 180, $"{all.Count} vendors");
    }
}
