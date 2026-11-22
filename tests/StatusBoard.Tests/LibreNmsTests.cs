using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

public class LibreNmsTests
{
    private const string Base = "https://nms.example.test";

    [Fact]
    public void Parses_alerts_down_devices_and_rules()
    {
        var r = LibreNmsProvider.Parse(
            Samples.Text("synthetic-librenms-alerts.json"), Samples.Text("synthetic-librenms-devices-down.json"),
            Samples.Text("synthetic-librenms-rules.json"), new Dictionary<string, string>(), Base, "LibreNMS");

        Assert.Equal(ServiceStatus.PartialOutage, r.Status);
        Assert.Equal(4, r.Incidents.Count); // 1 down device + 3 alerts

        var down = r.Incidents.Single(i => i.Title == "ups-rack2 is down"); // sysName preferred over IP hostname
        Assert.Equal("Reason: icmp", down.Summary);
        Assert.Equal($"{Base}/device/device=7/", down.Url);

        // Alert without hostname/name/severity: uses rule name + severity from /rules and hostname from the down list.
        var deviceDown = r.Incidents.Single(i => i.Title.StartsWith("Device Down!"));
        Assert.Equal(ServiceStatus.PartialOutage, deviceDown.Status);
        Assert.EndsWith("– Device 7", deviceDown.Title); // not in hostname map passed in

        var warn = r.Incidents.Single(i => i.Title.StartsWith("Disk usage"));
        Assert.Equal(ServiceStatus.Degraded, warn.Status);
        Assert.Equal("Expected – log rotation runs at 12:00", warn.Summary);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 19, 11, 44, 10, DateTimeKind.Local)), warn.Started);

        Assert.Contains(r.Components, c => c.Name == "core-sw-01.lan" && c.Status == ServiceStatus.PartialOutage);
    }

    [Fact]
    public void Empty_lists_are_operational()
    {
        var r = LibreNmsProvider.Parse("""{"status":"ok","count":0,"alerts":[]}""",
            """{"status":"error","message":"No devices found"}""", null, new Dictionary<string, string>(), Base, "LibreNMS");
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
    }

    [Fact]
    public void Html_login_page_is_reported_as_parse_error() =>
        Assert.Throws<ParseException>(() => LibreNmsProvider.Parse("<html>login</html>", "{}", null, new Dictionary<string, string>(), Base, "x"));

    [Fact]
    public async Task Fetch_sends_token_headers_and_resolves_missing_hostnames()
    {
        var http = new FakeFetcher()
            .Add($"{Base}/api/v0/alerts?state=1", Samples.Text("synthetic-librenms-alerts.json"))
            .Add($"{Base}/api/v0/devices?type=down", """{"status":"ok","count":0,"devices":[]}""")
            .Add($"{Base}/api/v0/rules", Samples.Text("synthetic-librenms-rules.json"))
            .Add($"{Base}/api/v0/devices/7", """{"status":"ok","devices":[{"device_id":"7","hostname":"ups-rack2.lan"}]}""");
        var source = new SourceConfig
        {
            Type = "librenms", Label = "LibreNMS", Url = Base + "/",
            Options = new(StringComparer.OrdinalIgnoreCase) { ["token"] = Secrets.Protect("abc123"), ["allowInvalidCertificate"] = "true" },
        };

        var r = await new LibreNmsProvider().FetchAsync(source, http, CancellationToken.None);

        Assert.Contains(r.Incidents, i => i.Title == "Device Down! Due to no ICMP response. – ups-rack2.lan");
        var (_, opts) = http.Requests.First();
        Assert.Equal("Bearer abc123", opts!.Headers["Authorization"]);
        Assert.Equal("abc123", opts.Headers["X-Auth-Token"]);
        Assert.True(opts.AllowInvalidCertificate);
    }

    [Fact]
    public async Task Missing_token_fails_clearly_through_status_service()
    {
        var cfg = new AppConfig { Vendors = [new() { Id = "n", Name = "NMS", Sources = [new() { Type = "librenms", Label = "LibreNMS", Url = Base }] }] };
        var v = (await new StatusService(ProviderRegistry.CreateDefault(), new FakeFetcher()).RefreshAsync(cfg, CancellationToken.None)).Single();
        Assert.Equal(ServiceStatus.Unknown, v.Status);
        Assert.Equal("No API token configured", v.Sources[0].Error);
    }
}
