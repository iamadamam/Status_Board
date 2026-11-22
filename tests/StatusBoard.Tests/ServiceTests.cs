using System.Net;
using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

/// <summary>Fetcher retry behaviour and source isolation, using a fake HTTP handler (no network).</summary>
public class ServiceTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request, ++Calls));
    }

    private static IHttpFetcher Fetcher(FakeHandler h) => new HttpFetcher(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1), handler: h);

    [Fact]
    public async Task Retries_server_errors_then_succeeds()
    {
        var h = new FakeHandler((_, n) => n < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        Assert.Equal("ok", await Fetcher(h).GetTextAsync("https://example.test/", CancellationToken.None));
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task Does_not_retry_404()
    {
        var h = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<FetchException>(() => Fetcher(h).GetTextAsync("https://example.test/", CancellationToken.None));
        Assert.Contains("404", ex.Message);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task One_broken_source_does_not_break_others()
    {
        var h = new FakeHandler((req, _) => req.RequestUri!.Host == "good.test"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Samples.Text("statuspage-forticloud-hub-summary.json")) }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var svc = new StatusService(ProviderRegistry.CreateDefault(), Fetcher(h));
        var cfg = new AppConfig
        {
            Vendors =
            [
                new() { Id = "a", Name = "A", Sources = [new() { Type = "statuspage", Label = "Good", Url = "https://good.test" }] },
                new() { Id = "b", Name = "B", Sources = [new() { Type = "statuspage", Label = "Bad", Url = "https://bad.test" }] },
                new() { Id = "c", Name = "C", Sources = [new() { Type = "nonsense", Label = "Typo", Url = "https://good.test" }] },
                new() { Id = "d", Name = "D", Enabled = false, Sources = [new() { Type = "statuspage", Url = "https://good.test" }] },
            ],
        };

        var result = await svc.RefreshAsync(cfg, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal(ServiceStatus.Operational, result.Single(v => v.VendorId == "a").Status);
        var b = result.Single(v => v.VendorId == "b");
        Assert.Equal(ServiceStatus.Unknown, b.Status);
        Assert.Contains("500", b.Sources[0].Error);
        Assert.Contains("No adapter", result.Single(v => v.VendorId == "c").Sources[0].Error);
    }

    [Fact]
    public void Default_config_round_trips_and_uses_known_adapter_types()
    {
        var json = ConfigStore.Serialize(AppConfig.CreateDefault());
        var cfg = ConfigStore.Deserialize(json);
        var registry = ProviderRegistry.CreateDefault();
        Assert.Equal(DefaultVendors.Create().Count, cfg.Vendors.Count);
        Assert.Equal(cfg.Vendors.Count, cfg.Vendors.Select(v => v.Id).Distinct().Count());
        Assert.Equal(DefaultVendors.Version, cfg.DefaultsVersion);
        Assert.All(cfg.Vendors.SelectMany(v => v.Sources), s => Assert.NotNull(registry.Find(s.Type)));
        Assert.Equal(16, cfg.Vendors.Single(v => v.Id == "forticloud").Sources.Count);
    }

    [Fact]
    public void Corrupt_config_falls_back_to_defaults_and_keeps_backup()
    {
        var dir = Directory.CreateTempSubdirectory("sb-test");
        try
        {
            var path = Path.Combine(dir.FullName, ConfigStore.FileName);
            File.WriteAllText(path, "{ not json");
            var store = new ConfigStore(path);
            var cfg = store.Load();
            Assert.NotNull(store.LoadWarning);
            Assert.Equal(5, cfg.RefreshIntervalMinutes);
            Assert.True(File.Exists(path + ".invalid"));
        }
        finally { dir.Delete(recursive: true); }
    }
}
