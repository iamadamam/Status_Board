using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

public class ComponentFilterTests
{
    private static SourceResult Sample() => SourceResult.Of("Page",
        [
            new Incident { Title = "API errors", Status = ServiceStatus.MajorOutage, AffectedComponents = ["API"] },
            new Incident { Title = "Billing slow", Status = ServiceStatus.Degraded, AffectedComponents = ["Billing", "Dashboard"] },
            new Incident { Title = "Unspecified", Status = ServiceStatus.Degraded },
        ],
        [new("API", ServiceStatus.MajorOutage), new("Billing", ServiceStatus.Degraded), new("Dashboard", ServiceStatus.Operational)],
        ServiceStatus.MajorOutage);

    [Fact]
    public void No_selection_changes_nothing()
    {
        var s = Sample();
        Assert.Same(s, ComponentFilter.Apply(s, null));
    }

    [Fact]
    public void An_outage_in_an_unused_component_no_longer_counts()
    {
        var r = ComponentFilter.Apply(Sample(), ["Dashboard"]);
        Assert.True(r.Filtered);
        Assert.Equal(["Dashboard"], r.Components.Select(c => c.Name));
        Assert.DoesNotContain(r.Incidents, i => i.Title == "API errors");
        var billing = r.Incidents.Single(i => i.Title == "Billing slow");
        Assert.Equal(["Dashboard"], billing.AffectedComponents);            // only the selected ones are listed
        Assert.Contains(r.Incidents, i => i.Title == "Unspecified");       // can't tell, so kept
        Assert.Equal(ServiceStatus.Degraded, r.Status);                     // was Major outage page-wide
    }

    [Fact]
    public void Nothing_selected_affected_is_operational()
    {
        var src = SourceResult.Of("Page", [new Incident { Title = "API", Status = ServiceStatus.MajorOutage, AffectedComponents = ["API"] }],
            [new("API", ServiceStatus.MajorOutage), new("Web", ServiceStatus.Operational)], ServiceStatus.MajorOutage);
        Assert.Equal(ServiceStatus.Operational, ComponentFilter.Apply(src, ["Web"]).Status);
    }

    [Fact]
    public void Failed_sources_are_left_alone() => Assert.False(ComponentFilter.Apply(SourceResult.Failed("x", "down"), ["A"]).Filtered);

    [Theory]
    [InlineData("API", "api", true)]
    [InlineData("Cortex Cloud / Australia", "Australia", true)]      // Statuspage incident names drop the group
    [InlineData("Australia", "Cortex Cloud / Australia", true)]
    [InlineData("Amazon EC2 (London)", "Amazon EC2", true)]          // AWS incident names drop the region
    [InlineData("Amazon EC2", "Amazon EC2 (London)", true)]
    [InlineData("Cortex Cloud", "Cortex Cloud / Australia", true)]   // a whole group
    [InlineData("API", "API Gateway", false)]
    [InlineData("EC2", "Amazon EC2", false)]
    public void Name_matching(string selected, string name, bool expected) => Assert.Equal(expected, ComponentFilter.Matches(selected, name));

    [Fact]
    public async Task The_service_applies_filters_but_the_picker_can_see_everything()
    {
        var http = new FakeFetcher().Add("https://ztp.example/api/v2/summary.json", Samples.Text("statuspage-fortiztp-summary.json"));
        var service = new StatusService(ProviderRegistry.CreateDefault(), http);
        var all = StatuspageProvider.Parse(Samples.Text("statuspage-fortiztp-summary.json"), "ZTP").Components;
        var healthy = all.First(c => c.Status == ServiceStatus.Operational).Name;
        var vendor = new VendorConfig { Id = "z", Name = "ZTP", Sources = [new SourceConfig { Type = "statuspage", Label = "ZTP", Url = "https://ztp.example", Components = [healthy] }] };

        var filtered = await service.RefreshVendorAsync(vendor, CancellationToken.None);
        Assert.NotEqual(ServiceStatus.Operational, StatuspageProvider.Parse(Samples.Text("statuspage-fortiztp-summary.json"), "ZTP").Status);
        Assert.True(filtered.Sources[0].Filtered);
        Assert.Single(filtered.Sources[0].Components);

        var raw = await service.RefreshVendorAsync(vendor, CancellationToken.None, applyFilters: false);
        Assert.Equal(all.Count, raw.Sources[0].Components.Count);
    }
}

public class CheckProviderTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }

    private static SourceConfig Ping(string host = "10.0.0.1", int? fails = null, int? slowMs = null)
    {
        var s = new SourceConfig { Type = "ping", Label = "Router", Url = host };
        if (fails is { } f) s.Options["failuresBeforeDown"] = f.ToString();
        if (slowMs is { } ms) s.Options["slowMs"] = ms.ToString();
        return s;
    }

    private static PingCheckProvider Pinger(Queue<(bool, int, string?)> replies, Clock? clock = null) => new(clock ?? new Clock(), (_, _, _) =>
    {
        var (up, ms, problem) = replies.Dequeue();
        return Task.FromResult((up, TimeSpan.FromMilliseconds(ms), problem));
    });

    [Fact]
    public async Task Down_only_after_three_failures_in_a_row()
    {
        var p = Pinger(new([(false, 0, "No reply"), (false, 0, "No reply"), (false, 0, "No reply"), (true, 20, null)]));
        var http = new FakeFetcher();
        var r1 = await p.FetchAsync(Ping(), http, default);
        Assert.Equal(ServiceStatus.Operational, r1.Status);
        Assert.StartsWith("Check failed (1 of 3)", r1.Note);
        Assert.Equal(ServiceStatus.Operational, (await p.FetchAsync(Ping(), http, default)).Status);
        var r3 = await p.FetchAsync(Ping(), http, default);
        Assert.Equal(ServiceStatus.MajorOutage, r3.Status);
        Assert.Equal("Router is not responding", r3.Incidents.Single().Title);
        Assert.Contains("3 failed checks", r3.Incidents.Single().Summary);
        var back = await p.FetchAsync(Ping(), http, default);
        Assert.Equal(ServiceStatus.Operational, back.Status);
        Assert.Equal("Up · 20 ms", back.Note);
    }

    [Fact]
    public async Task A_success_in_between_resets_the_count()
    {
        var p = Pinger(new([(false, 0, "x"), (false, 0, "x"), (true, 5, null), (false, 0, "x"), (false, 0, "x")]));
        for (var i = 0; i < 5; i++) Assert.Equal(ServiceStatus.Operational, (await p.FetchAsync(Ping(), new FakeFetcher(), default)).Status);
    }

    [Fact]
    public async Task Slow_after_three_slow_replies()
    {
        var p = Pinger(new([(true, 900, null), (true, 900, null), (true, 900, null)]));
        await p.FetchAsync(Ping(), new FakeFetcher(), default);
        var r2 = await p.FetchAsync(Ping(), new FakeFetcher(), default);
        Assert.Contains("slow 2 of 3", r2.Note);
        var r3 = await p.FetchAsync(Ping(), new FakeFetcher(), default);
        Assert.Equal(ServiceStatus.Degraded, r3.Status);
        Assert.Contains("limit 300 ms", r3.Incidents.Single().Summary);
    }

    [Fact]
    public async Task Threshold_of_one_reports_immediately()
    {
        var p = Pinger(new([(false, 0, "Host unreachable")]));
        Assert.Equal(ServiceStatus.MajorOutage, (await p.FetchAsync(Ping(fails: 1), new FakeFetcher(), default)).Status);
    }

    [Fact]
    public async Task Separate_hosts_keep_separate_counts()
    {
        var p = Pinger(new([(false, 0, "x"), (false, 0, "x"), (false, 0, "x")]));
        await p.FetchAsync(Ping("a"), new FakeFetcher(), default);
        await p.FetchAsync(Ping("b"), new FakeFetcher(), default);
        Assert.Equal(ServiceStatus.Operational, (await p.FetchAsync(Ping("a"), new FakeFetcher(), default)).Status);
    }

    [Fact]
    public async Task Website_up_down_and_unexpected_codes()
    {
        var http = new FakeFetcher().Add("https://ok.example/", "<html/>", "text/html");
        var p = new HttpCheckProvider();
        var site = new SourceConfig { Type = "http", Label = "Intranet", Url = "https://ok.example/", Options = new() { ["failuresBeforeDown"] = "1" } };
        var ok = await p.FetchAsync(site, http, default);
        Assert.Equal(ServiceStatus.Operational, ok.Status);
        Assert.StartsWith("Up · ", ok.Note);

        var missing = new SourceConfig { Type = "http", Label = "Gone", Url = "https://gone.example/", Options = new() { ["failuresBeforeDown"] = "1" } };
        var down = await p.FetchAsync(missing, http, default);       // FakeFetcher: 404
        Assert.Equal(ServiceStatus.MajorOutage, down.Status);
        Assert.Contains("404", down.Incidents.Single().Summary);

        missing.Options["expect"] = "404";                            // a 404 can be the expected answer
        Assert.Equal(ServiceStatus.Operational, (await new HttpCheckProvider().FetchAsync(missing, http, default)).Status);
        Assert.Single(http.Requests.Where(r => r.Url == "https://ok.example/"), r => r.Options?.Attempts == 1);
    }

    [Theory]
    [InlineData(null, 200, true)]
    [InlineData(null, 302, true)]
    [InlineData(null, 404, false)]
    [InlineData("200,301", 301, true)]
    [InlineData("200,301", 302, false)]
    [InlineData("401", 401, true)]
    [InlineData("500-400", 450, true)]
    [InlineData("rubbish", 200, true)]
    public void Expected_codes(string? spec, int code, bool up) => Assert.Equal(up, HttpCheckProvider.ParseExpected(spec)(code));

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("router.local:8080", "router.local")]
    [InlineData("https://intranet.example.com/login", "intranet.example.com")]
    [InlineData("server/path", "server")]
    [InlineData("::1", "::1")]
    [InlineData("[fe80::1]", "fe80::1")]
    public void Ping_host_from_address(string address, string host) => Assert.Equal(host, PingCheckProvider.HostOf(address));

    [Fact]
    public async Task Empty_address_is_a_clear_error() =>
        await Assert.ThrowsAsync<ParseException>(() => new PingCheckProvider().FetchAsync(new SourceConfig { Type = "ping" }, new FakeFetcher(), default));

    [Fact]
    public void Check_vendors_are_recognised()
    {
        Assert.True(StatusService.IsCheck(new VendorConfig { Sources = [new() { Type = "ping" }, new() { Type = "HTTP" }] }));
        Assert.False(StatusService.IsCheck(new VendorConfig { Sources = [new() { Type = "ping" }, new() { Type = "statuspage" }] }));
        Assert.False(StatusService.IsCheck(new VendorConfig()));
    }
}

public class GroupConfigTests
{
    [Fact]
    public void Groups_are_cleaned_and_dangling_memberships_dropped()
    {
        var dir = Directory.CreateTempSubdirectory("sb-test");
        try
        {
            var path = Path.Combine(dir.FullName, ConfigStore.FileName);
            File.WriteAllText(path, """
                { "defaultsVersion": 99, "boardView": "LIST",
                  "groups": [ {"id":"crit","name":" Critical "}, {"id":"CRIT","name":"dupe"}, {"id":"","name":"blank"}, null, {"id":"sec","name":""} ],
                  "vendors": [ {"id":"a","name":"A","groupId":"crit"}, {"id":"b","name":"B","groupId":"gone"}, {"id":"c","name":"C","groupId":"SEC"},
                               {"id":"d","name":"D","sources":[{"type":"statuspage","url":"x","components":[" API ","api","", null]}]} ] }
                """);
            var cfg = new ConfigStore(path).Load();
            Assert.Equal("List", cfg.BoardView);
            Assert.Equal(["crit", "sec"], cfg.Groups.Select(g => g.Id));
            Assert.Equal(["Critical", "Group"], cfg.Groups.Select(g => g.Name));
            Assert.Equal("crit", cfg.Vendors[0].GroupId);
            Assert.Null(cfg.Vendors[1].GroupId);
            Assert.Equal("sec", cfg.Vendors[2].GroupId);   // case-insensitive, stored with the group's own spelling
            Assert.Equal(["API"], cfg.Vendors[3].Sources[0].Components!);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Defaults_have_no_groups_and_tiles() { var c = AppConfig.CreateDefault(); Assert.Empty(c.Groups); Assert.Equal("Tiles", c.BoardView); }
}
