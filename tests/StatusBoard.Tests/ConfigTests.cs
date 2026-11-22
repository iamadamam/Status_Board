using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

public class SecretsTests
{
    [Fact]
    public void Round_trips_and_is_not_plain_text()
    {
        var p = Secrets.Protect("s3cret-token");
        Assert.StartsWith("dpapi:", p);
        Assert.DoesNotContain("s3cret", p);
        Assert.Equal("s3cret-token", Secrets.Unprotect(p));
    }

    [Fact]
    public void Plain_value_typed_into_the_file_is_used_as_is() => Assert.Equal("plain", Secrets.Unprotect("plain"));

    [Fact]
    public void Undecryptable_value_gives_actionable_error() =>
        Assert.Contains("Re-enter", Assert.Throws<InvalidOperationException>(() => Secrets.Unprotect("dpapi:AAAA")).Message);
}

public class HandEditedConfigTests
{
    [Fact]
    public void Nulls_blank_and_repeated_ids_are_repaired_not_fatal()
    {
        var dir = Directory.CreateTempSubdirectory("sb-test");
        try
        {
            var path = Path.Combine(dir.FullName, ConfigStore.FileName);
            File.WriteAllText(path, """
                { "defaultsVersion": 99, "vendors": [
                    null,
                    {"id":"weird","name":"","sources":[{"type":"no-such-type","label":null,"url":null}, null]},
                    {"id":"WEIRD","name":"Duplicate id","sources":null},
                    {"id":"","name":"Blank id"},
                    {"id":"weird","name":"Third","sources":[{"type":"feed","url":"https://x","options":{"mode":null}}]}
                ] }
                """);
            var store = new ConfigStore(path);
            var cfg = store.Load();
            Assert.Null(store.LoadWarning);
            Assert.Equal(4, cfg.Vendors.Count);
            Assert.Equal(cfg.Vendors.Count, cfg.Vendors.Select(v => v.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(["weird", "WEIRD-2", "vendor", "weird-3"], cfg.Vendors.Select(v => v.Id));
            Assert.Equal("Unnamed vendor", cfg.Vendors[0].Name);
            Assert.Single(cfg.Vendors[0].Sources);
            Assert.Equal("", cfg.Vendors[0].Sources[0].Url);
            Assert.Empty(cfg.Vendors[1].Sources);
            Assert.Empty(cfg.Vendors[3].Sources[0].Options);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task A_vendor_with_an_unknown_source_type_reports_it_instead_of_failing()
    {
        var cfg = new AppConfig { Vendors = [new VendorConfig { Id = "x", Name = "X", Sources = [new SourceConfig { Type = "no-such-type", Label = "Odd" }] }] };
        var r = (await new StatusService(ProviderRegistry.CreateDefault(), new FakeFetcher()).RefreshAsync(cfg, CancellationToken.None)).Single();
        Assert.Equal(ServiceStatus.Unknown, r.Status);
        Assert.Contains("No adapter", r.Sources.Single().Error);
    }
}

public class RefreshSettingTests
{
    [Fact]
    public void Manual_refresh_survives_save_and_load()
    {
        var dir = Directory.CreateTempSubdirectory("sb-test");
        try
        {
            var store = new ConfigStore(Path.Combine(dir.FullName, ConfigStore.FileName));
            var cfg = AppConfig.CreateDefault();
            cfg.RefreshIntervalMinutes = 0;
            store.Save(cfg);
            Assert.Equal(0, store.Load().RefreshIntervalMinutes);
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData(0, "Manual")]
    [InlineData(1, "1 minute")]
    [InlineData(15, "15 minutes")]
    [InlineData(60, "1 hour")]
    public void Describes_intervals(int minutes, string text) => Assert.Equal(text, AppConfig.DescribeInterval(minutes));
}

/// <summary>The built-in catalogue and how existing configs pick up new built-ins.</summary>
public class DefaultsTests
{
    [Fact]
    public void Every_builtin_has_a_known_section_and_unique_id()
    {
        var all = DefaultVendors.Create();
        Assert.All(all, v => Assert.NotEqual(VendorCategories.Custom, VendorCategories.Normalise(v.Category)));
        Assert.Equal(all.Count, all.Select(v => v.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(all.Count(v => v.Enabled) >= 45, $"{all.Count(v => v.Enabled)} enabled");
        var registry = ProviderRegistry.CreateDefault();
        Assert.All(all.SelectMany(v => v.Sources), s => Assert.NotNull(registry.Find(s.Type)));
    }

    [Fact]
    public void Upgrade_adds_missing_builtins_and_fills_missing_sections()
    {
        var cfg = new AppConfig
        {
            DefaultsVersion = 0,
            Vendors =
            [
                new() { Id = "azure", Name = "Microsoft Azure", Sources = [new() { Type = "feed", Url = "https://rssfeed.azure.status.microsoft/en-us/status/feed/" }] },
                new() { Id = "custom-zoom", Name = "Zoom", Custom = true, Sources = [new() { Type = "statuspage", Url = "https://www.zoomstatus.com" }] },
            ],
        };
        Assert.True(DefaultVendors.Upgrade(cfg));
        Assert.Equal(VendorCategories.Cloud, cfg.Vendors[0].Category);
        Assert.Equal(VendorCategories.Custom, cfg.Vendors[1].Category);
        Assert.Contains(cfg.Vendors, v => v.Id == "slack");
        Assert.DoesNotContain(cfg.Vendors, v => v.Id == "zoom"); // the user already watches that Statuspage
        Assert.Equal(DefaultVendors.Version, cfg.DefaultsVersion);
        Assert.False(DefaultVendors.Upgrade(cfg));
    }

    /// <summary>Opt-in: set STATUSBOARD_LIVE=1 to fetch every built-in source (enabled or not) from the internet.</summary>
    [Fact]
    public async Task Live_every_builtin_source_is_reachable_and_parses()
    {
        if (Environment.GetEnvironmentVariable("STATUSBOARD_LIVE") != "1") return;
        using var http = new HttpFetcher();
        var registry = ProviderRegistry.CreateDefault();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(DefaultVendors.Create().SelectMany(v => v.Sources.Where(s => s.Enabled).Select(s => (v, s))),
            new ParallelOptions { MaxDegreeOfParallelism = 12 }, async (x, ct) =>
            {
                try { await registry.Find(x.s.Type)!.FetchAsync(x.s, http, ct); }
                catch (Exception ex) { failures.Add($"{x.v.Name} / {x.s.Label}: {ex.Message}"); }
            });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.OrderBy(f => f)));
    }

    [Fact]
    public void Config_from_an_earlier_catalogue_gains_new_builtins_untouched()
    {
        var cfg = new AppConfig
        {
            DefaultsVersion = 0,
            Vendors =
            [
                new() { Id = "aws", Name = "My AWS", Enabled = false, Sources = [new() { Type = "aws-health", Url = "x" }] },
                // Added by hand earlier via "Add vendor": must not be duplicated by the built-in GitHub.
                new() { Id = "custom-github-abc", Name = "GitHub", Custom = true, Sources = [new() { Type = "statuspage", Url = "https://www.githubstatus.com/" }] },
            ],
        };
        Assert.True(DefaultVendors.Upgrade(cfg));
        Assert.Equal(DefaultVendors.Version, cfg.DefaultsVersion);
        Assert.Equal("My AWS", cfg.Vendors.Single(v => v.Id == "aws").Name);
        Assert.False(cfg.Vendors.Single(v => v.Id == "aws").Enabled);
        Assert.DoesNotContain(cfg.Vendors, v => v.Id == "github");
        Assert.Contains(cfg.Vendors, v => v.Id == "cloudflare");
        Assert.Contains(cfg.Vendors, v => v.Id == "salesforce");
        Assert.False(DefaultVendors.Upgrade(cfg)); // idempotent
    }

    [Fact]
    public void File_without_catalogue_version_gains_builtins_on_load()
    {
        var dir = Directory.CreateTempSubdirectory("sb-test");
        try
        {
            var path = Path.Combine(dir.FullName, ConfigStore.FileName);
            File.WriteAllText(path, """{ "refreshIntervalMinutes": 15, "vendors": [ { "id": "azure", "name": "Microsoft Azure", "sources": [] } ] }""");
            var cfg = new ConfigStore(path).Load();
            Assert.Equal(15, cfg.RefreshIntervalMinutes);
            Assert.Equal(DefaultVendors.Create().Count, cfg.Vendors.Count);
            Assert.Contains($"\"defaultsVersion\": {DefaultVendors.Version}", File.ReadAllText(path)); // saved back
        }
        finally { dir.Delete(true); }
    }
}
