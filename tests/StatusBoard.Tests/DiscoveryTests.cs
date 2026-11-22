using System.Text;
using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

public class DetectorTests
{
    [Fact]
    public async Task Detects_statuspage_from_page_link()
    {
        var http = new FakeFetcher().Add("https://status.fortiztp.forticloud.com/api/v2/summary.json", Samples.Text("statuspage-fortiztp-summary.json"));
        var r = await VendorDetector.DetectAsync("status.fortiztp.forticloud.com/", http, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal("statuspage", r.Type);
        Assert.Equal("https://status.fortiztp.forticloud.com", r.SourceUrl);
        Assert.Equal("FortiZTP", r.SuggestedName);
        Assert.Contains("components", r.Message);
    }

    [Fact]
    public async Task Detects_statuspage_from_pasted_api_url()
    {
        var http = new FakeFetcher().Add("https://status.example.test/api/v2/summary.json", Samples.Text("statuspage-forticloud-hub-summary.json"));
        var r = await VendorDetector.DetectAsync("https://status.example.test/api/v2/summary.json", http, CancellationToken.None);
        Assert.Equal("statuspage", r.Type);
        Assert.Equal("https://status.example.test", r.SourceUrl);
    }

    [Fact]
    public async Task Detects_direct_feed_link()
    {
        var http = new FakeFetcher().Add("https://example.test/status/feed.xml", Samples.Text("rss-azure-empty.xml"), "text/xml");
        var r = await VendorDetector.DetectAsync("https://example.test/status/feed.xml", http, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal("feed", r.Type);
        Assert.Equal("Azure", r.SuggestedName); // "Azure Status" cleaned
        Assert.Contains("0 items", r.Message);
    }

    [Fact]
    public async Task Detects_feed_advertised_by_html_page()
    {
        const string html = """<html><head><title>Acme Cloud Status</title><link rel="alternate" type="application/atom+xml" href="/history.atom?x=1&amp;y=2"></head></html>""";
        var http = new FakeFetcher()
            .Add("https://status.acme.test/", html, "text/html")
            .Add("https://status.acme.test/history.atom?x=1&y=2", Samples.Text("atom-forticloud-history.atom"), "application/atom+xml");
        var r = await VendorDetector.DetectAsync("https://status.acme.test/", http, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal("feed", r.Type);
        Assert.Equal("https://status.acme.test/history.atom?x=1&y=2", r.SourceUrl);
        Assert.Equal("https://status.acme.test/", r.StatusPageUrl);
        Assert.Equal("FortiCloud", r.SuggestedName);
    }

    [Fact]
    public async Task Falls_back_to_common_feed_paths()
    {
        var http = new FakeFetcher()
            .Add("https://status.acme.test/", "<html><title>Acme</title></html>", "text/html")
            .Add("https://status.acme.test/feed.rss", Samples.Text("synthetic-rss-azure-incident.xml"), "application/rss+xml");
        var r = await VendorDetector.DetectAsync("https://status.acme.test/", http, CancellationToken.None);
        Assert.Equal("https://status.acme.test/feed.rss", r.SourceUrl);
    }

    [Fact]
    public async Task Reports_failure_with_guidance()
    {
        var http = new FakeFetcher().Add("https://nothing.test/", "<html><title>Nope</title></html>", "text/html");
        var r = await VendorDetector.DetectAsync("https://nothing.test/", http, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("RSS or Atom link", r.Message);
    }

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("ftp://x.test/")]
    public async Task Rejects_non_web_addresses(string input) =>
        Assert.False((await VendorDetector.DetectAsync(input, new FakeFetcher(), CancellationToken.None)).Success);

    [Theory]
    [InlineData("GitHub Status - Incident History", "GitHub")]
    [InlineData("FortiCloud Status Hub Status", "FortiCloud")]
    [InlineData("Status", "Status")]
    public void Cleans_names(string input, string expected) => Assert.Equal(expected, VendorDetector.CleanName(input));
}

public class LogoTests
{
    [Fact]
    public void Ranks_touch_icons_and_large_pngs_first_and_skips_svg()
    {
        const string html = """
            <link rel="icon" type="image/ico" href="https://a0.awsstatic.com/fav/favicon.ico">
            <link rel="icon" type="image/svg+xml" href="/logo.svg">
            <link rel="icon" type="image/png" href="/fav-16.png" sizes="16x16">
            <link rel="icon" type="image/png" href="/fav-32.png" sizes="32x32">
            <link rel="apple-touch-icon" sizes="72x72" href="https://a0.awsstatic.com/touch-144.png">
            <link rel="shortcut icon" href='//cdn.example.test/FortiCloud.png'>
            """;
        var list = LogoFinder.CandidatesFromHtml(html, new Uri("https://status.example.test/page"));
        Assert.Equal("https://a0.awsstatic.com/touch-144.png", list[0]);
        Assert.Equal("https://status.example.test/fav-32.png", list[1]);
        Assert.Contains("https://cdn.example.test/FortiCloud.png", list);
        Assert.DoesNotContain(list, u => u.EndsWith(".svg"));
    }

    [Fact]
    public void Sniffs_image_formats_by_content()
    {
        Assert.Equal(".png", LogoFinder.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal(".ico", LogoFinder.Sniff([0, 0, 1, 0, 1, 0, 16, 16]));
        Assert.Null(LogoFinder.Sniff(Encoding.UTF8.GetBytes("<html>404</html>")));
    }

    [Fact]
    public async Task Uses_configured_logo_then_falls_back_to_page_icon()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var http = new FakeFetcher()
            .Add("https://status.acme.test/", """<link rel="icon" type="image/png" href="/i.png">""", "text/html")
            .Add("https://status.acme.test/i.png", png, "image/png");
        var vendor = new VendorConfig { Logo = "https://broken.test/logo.png", StatusPageUrl = "https://status.acme.test/" };
        var logo = await LogoFinder.FindAsync(vendor, http, CancellationToken.None);
        Assert.NotNull(logo);
        Assert.Equal(".png", logo.Extension);
        Assert.Equal("https://broken.test/logo.png", http.Requests[0].Url); // tried the configured logo first
    }

    [Fact]
    public async Task Skips_platform_placeholder_and_uses_the_vendors_main_site()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var http = new FakeFetcher()
            .Add("https://status.acme.test/", """<link rel="icon" href='https://cdn.statusiq.test/favicon/x=?default=1'>""", "text/html")
            .Add("https://cdn.statusiq.test/favicon/x=?default=1", png, "image/x-icon")
            .Add("https://acme.test/", """<link rel="icon" type="image/png" sizes="48x48" href="/acme-48.png">""", "text/html")
            .Add("https://acme.test/acme-48.png", png, "image/png");
        var logo = await LogoFinder.FindAsync(new VendorConfig { StatusPageUrl = "https://status.acme.test/" }, http, CancellationToken.None);
        Assert.NotNull(logo);
        Assert.Equal("https://acme.test/acme-48.png", http.Requests[^1].Url);
        Assert.DoesNotContain(http.Requests, r => r.Url.Contains("default=1"));
    }

    [Theory]
    [InlineData("https://status.canonical.com/", "https://canonical.com/")]
    [InlineData("https://status.cloud.microsoft/", "https://cloud.microsoft/")]
    [InlineData("https://www.githubstatus.com/", null)]
    [InlineData("https://example.com/", null)]
    public void Main_site_of_a_status_subdomain(string page, string? expected) =>
        Assert.Equal(expected, LogoFinder.MainSite(new Uri(page))?.AbsoluteUri);
}
