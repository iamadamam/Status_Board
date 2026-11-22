using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

public class StatusIoTests
{
    [Fact]
    public void GitLab_all_operational()
    {
        var r = StatusIoProvider.Parse(Samples.Text("statusio-gitlab.json"), "GitLab", "https://status.gitlab.com/");
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
        Assert.Contains(r.Components, c => c.Name.StartsWith("Git Operations"));
    }

    [Fact]
    public void Mimecast_grids_with_several_containers_become_grid_and_container_components()
    {
        var r = StatusIoProvider.Parse(Samples.Text("statusio-mimecast.json"), "Mimecast", "https://status.mimecast.com/");
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Contains(r.Components, c => c.Name == "United States Grid / Email Processing");
        Assert.All(r.Components, c => Assert.Equal(ServiceStatus.Operational, c.Status));
    }

    [Fact]
    public void ConnectWise_open_incidents_being_monitored_still_show()
    {
        var r = StatusIoProvider.Parse(Samples.Text("statusio-connectwise.json"), "ConnectWise", "https://status.connectwise.com/");
        Assert.Equal(3, r.Incidents.Count);
        var x = r.Incidents.Single(i => i.Title.Contains("x360Sync"));
        Assert.Equal(ServiceStatus.Degraded, x.Status); // latest update is "operational" but the incident is still open
        Assert.Equal("We believe issue to be resolved, monitoring to confirml", x.Summary); // latest message, not first
        Assert.Equal(["x360Sync"], x.AffectedComponents);
        Assert.Equal("https://status.connectwise.com/", x.Url);
        Assert.Equal(ServiceStatus.Degraded, r.Status);
    }

    [Fact]
    public void Component_and_maintenance_codes_map()
    {
        var json = Samples.Text("statusio-gitlab.json")
            .Replace("\"status_code\": 100", "\"status_code\": 500")
            .Replace("\"active\": []", "\"active\": [{\"name\":\"DB upgrade\",\"datetime_planned_start\":\"2026-11-19T10:00:00Z\",\"messages\":[]}]");
        var r = StatusIoProvider.Parse(json, "GitLab");
        Assert.Equal(ServiceStatus.MajorOutage, r.Status);
        Assert.Contains(r.Incidents, i => i.Title == "DB upgrade" && i.Status == ServiceStatus.Maintenance);
    }

    [Theory]
    [InlineData("5b36dc6502d06804c08349f7", "https://api.status.io/1.0/status/5b36dc6502d06804c08349f7")]
    [InlineData("https://api.status.io/1.0/status/abc", "https://api.status.io/1.0/status/abc")]
    public void Accepts_page_id_or_url(string input, string expected) => Assert.Equal(expected, StatusIoProvider.ApiUrl(input));

    [Fact]
    public void Not_statusio_throws() => Assert.Throws<ParseException>(() => StatusIoProvider.Parse("{\"x\":1}", "x"));

    [Fact]
    public async Task Detector_finds_statusio_page_and_uses_api()
    {
        var http = new FakeFetcher()
            .Add("https://status.gitlab.test/", """<html><title>GitLab System Status</title><a href="/pages/5b36dc6502d06804c08349f7/rss">RSS</a> Powered by Status.io</html>""", "text/html")
            .Add("https://api.status.io/1.0/status/5b36dc6502d06804c08349f7", Samples.Text("statusio-gitlab.json"));
        var r = await VendorDetector.DetectAsync("https://status.gitlab.test/", http, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal("statusio", r.Type);
        Assert.Equal("GitLab", r.SuggestedName);
    }

    [Fact]
    public void Page_id_found_without_pages_link() =>
        Assert.Equal("619cf82551fec9053d612f09",
            VendorDetector.StatusIoId("x 619cf82551fec9053d612f09 y 6afe11d4c412742b98dab694 z 619cf82551fec9053d612f09"));
}
