using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

/// <summary>
/// Parsing tests against saved responses in Samples/. Files without the "synthetic-" prefix were captured from the live
/// endpoints on 19 Nov 2026; synthetic ones fill gaps where the live feed was healthy at capture time.
/// </summary>
public class StatuspageTests
{
    [Fact]
    public void Hub_page_with_no_components_is_operational()
    {
        var r = StatuspageProvider.Parse(Samples.Text("statuspage-forticloud-hub-summary.json"), "Hub");
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Components);
        Assert.Empty(r.Incidents);
    }

    [Fact]
    public void FortiZTP_major_outage_with_impact_none_incident()
    {
        var r = StatuspageProvider.Parse(Samples.Text("statuspage-fortiztp-summary.json"), "FortiZTP");

        // Indicator is "major" but several components are major_outage, so the worst wins.
        Assert.Equal(ServiceStatus.MajorOutage, r.Status);
        var inc = Assert.Single(r.Incidents);
        Assert.Equal("Service Interruption Notice", inc.Title);
        // Impact "none" must not hide the incident.
        Assert.Equal(ServiceStatus.Degraded, inc.Status);
        Assert.StartsWith("https://", inc.Url);
        Assert.NotNull(inc.Summary);
        // Groups are not listed themselves; children get the group prefix.
        Assert.DoesNotContain(r.Components, c => c.Name == "FortiZTP status");
        Assert.Contains(r.Components, c => c.Name == "FortiManager Cloud Provisioning Status - Global / Global->US-WEST-1" && c.Status == ServiceStatus.MajorOutage);
    }

    [Fact]
    public void FortiSIEM_minor_indicator_with_major_impact_incident()
    {
        var r = StatuspageProvider.Parse(Samples.Text("statuspage-fortisiem-summary.json"), "FortiSIEM");
        Assert.Equal(ServiceStatus.PartialOutage, r.Status);
        var inc = Assert.Single(r.Incidents);
        Assert.Equal(ServiceStatus.PartialOutage, inc.Status);
        Assert.Contains("UK - London", inc.AffectedComponents);
        Assert.NotNull(inc.Started);
    }

    [Fact]
    public void Scheduled_but_not_started_maintenance_is_ignored()
    {
        var r = StatuspageProvider.Parse(Samples.Text("statuspage-forticlient-summary.json"), "FortiClient");
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
    }

    [Fact]
    public void In_progress_maintenance_sets_maintenance_status()
    {
        var json = Samples.Text("statuspage-forticlient-summary.json").Replace("\"status\":\"scheduled\"", "\"status\":\"in_progress\"");
        var r = StatuspageProvider.Parse(json, "FortiClient");
        Assert.Equal(ServiceStatus.Maintenance, r.Status);
        Assert.Equal(ServiceStatus.Maintenance, Assert.Single(r.Incidents).Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("<html>not json</html>")]
    [InlineData("[]")]
    public void Garbage_throws_parse_exception(string body) =>
        Assert.Throws<ParseException>(() => StatuspageProvider.Parse(body, "x"));
}

public class FeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Component_status_feed_all_operational_is_operational_whatever_the_mode()
    {
        // StatusIQ (status.canonical.com): one item per component, "<name> - Operational", re-dated whenever it is checked.
        var r = FeedProvider.Parse(Samples.Text("statusiq-canonical.rss"), "Canonical", new(true, 24, ServiceStatus.Degraded), new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
        Assert.Equal(43, r.Components.Count);
        Assert.Contains(r.Components, c => c.Name == "Ubuntu Security API - CVEs");
    }

    [Theory]
    [InlineData("Major Outage", ServiceStatus.MajorOutage)]
    [InlineData("Partial Outage", ServiceStatus.PartialOutage)]
    [InlineData("Degraded Performance", ServiceStatus.Degraded)]
    [InlineData("Under Maintenance", ServiceStatus.Maintenance)]
    public void Component_status_feed_reports_the_component_in_trouble(string state, ServiceStatus expected)
    {
        var xml = Samples.Text("statusiq-canonical.rss").Replace("<title>DNS - Operational</title>", $"<title>DNS - {state}</title>");
        var r = FeedProvider.Parse(xml, "Canonical", FeedProvider.FeedOptions.Default, Now);
        Assert.Equal(expected, r.Status);
        Assert.Equal(expected, r.Components.Single(c => c.Name == "DNS").Status);
        Assert.Equal($"DNS - {state}", Assert.Single(r.Incidents).Title);
    }

    [Fact]
    public void Incident_feed_with_status_words_in_titles_is_not_a_component_feed()
    {
        const string rss = """
            <rss version="2.0"><channel>
            <item><title>Login failures - Resolved</title><pubDate>Thu, 19 Nov 2026 14:00:00 +0000</pubDate></item>
            <item><title>Slow API - Investigating</title><pubDate>Thu, 19 Nov 2026 14:30:00 +0000</pubDate></item>
            </channel></rss>
            """;
        var r = FeedProvider.Parse(rss, "A", FeedProvider.FeedOptions.Default, Now);
        Assert.Empty(r.Components);
        Assert.Equal(2, r.Incidents.Count); // titles alone: the active-mode default counts both
    }

    [Fact]
    public void Empty_azure_feed_is_operational()
    {
        var r = FeedProvider.Parse(Samples.Text("rss-azure-empty.xml"), "Azure", FeedProvider.FeedOptions.Default, Now);
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
    }

    [Fact]
    public void Azure_feed_item_is_active_incident_and_resolved_item_is_skipped()
    {
        var r = FeedProvider.Parse(Samples.Text("synthetic-rss-azure-incident.xml"), "Azure", FeedProvider.FeedOptions.Default, Now);
        Assert.Equal(ServiceStatus.Degraded, r.Status);
        var inc = Assert.Single(r.Incidents);
        Assert.StartsWith("Active - Azure Front Door", inc.Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 13, 40, 0, TimeSpan.Zero), inc.Started);
        Assert.Contains("intermittent errors", inc.Summary);
        Assert.DoesNotContain("<", inc.Summary);
    }

    [Fact]
    public void Statuspage_atom_history_in_recent_mode_ignores_old_and_resolved_entries()
    {
        var opts = new FeedProvider.FeedOptions(RecentMode: true, WindowHours: 24, ItemStatus: ServiceStatus.Degraded);
        var r = FeedProvider.Parse(Samples.Text("atom-forticloud-history.atom"), "Hub", opts, Now);
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
    }

    [Fact]
    public void Aws_rss_parses_named_timezones()
    {
        var opts = new FeedProvider.FeedOptions(RecentMode: false, WindowHours: 24, ItemStatus: ServiceStatus.Degraded);
        var r = FeedProvider.Parse(Samples.Text("rss-aws-all.rss"), "AWS RSS", opts, Now);
        Assert.NotEmpty(r.Incidents);
        Assert.All(r.Incidents, i => Assert.NotNull(i.Started));
        Assert.Contains(r.Incidents, i => i.Started == new DateTimeOffset(2026, 9, 15, 3, 27, 10, TimeSpan.FromHours(-7)));
    }

    [Theory]
    [InlineData("Thu, 19 Nov 2026 14:54:00 Z", "2026-11-19T14:54:00+00:00")]
    [InlineData("Tue, 15 Sep 2026 03:27:10 PDT", "2026-09-15T03:27:10-07:00")]
    [InlineData("Thu, 19 Nov 2026 05:51:11 -0800", "2026-11-19T05:51:11-08:00")]
    [InlineData("2026-11-19T05:51:11-08:00", "2026-11-19T05:51:11-08:00")]
    public void Dates(string input, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), FeedProvider.ParseDate(input));

    [Fact]
    public void Non_feed_xml_throws() =>
        Assert.Throws<ParseException>(() => FeedProvider.Parse("<html/>", "x", FeedProvider.FeedOptions.Default, Now));

    [Fact]
    public void Options_are_read_from_config()
    {
        var s = new SourceConfig { Options = new(StringComparer.OrdinalIgnoreCase) { ["mode"] = "recent", ["activeWindowHours"] = "6", ["itemStatus"] = "majoroutage" } };
        Assert.Equal(new FeedProvider.FeedOptions(true, 6, ServiceStatus.MajorOutage), FeedProvider.FeedOptions.From(s));
    }
}

public class MicrosoftTests
{
    [Fact]
    public void Consumer_services_all_operational()
    {
        var r = MicrosoftStatusProvider.Parse(Samples.Text("ms-m365consumer.json"), "Consumer");
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Equal(10, r.Components.Count);
        Assert.Contains(r.Components, c => c.Name == "Outlook.com");
        Assert.Empty(r.Incidents);
    }

    [Theory]
    [InlineData("ms-mac.json")]
    [InlineData("ms-azure.json")]
    public void Single_post_available_is_operational(string file)
    {
        var r = MicrosoftStatusProvider.Parse(Samples.Text(file), "x");
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
        Assert.Empty(r.Components);
    }

    [Fact]
    public void Service_degradation_becomes_incident()
    {
        var r = MicrosoftStatusProvider.Parse(Samples.Text("synthetic-ms-m365consumer-degraded.json"), "Consumer");
        Assert.Equal(ServiceStatus.Degraded, r.Status);
        var inc = Assert.Single(r.Incidents);
        Assert.Equal("Some users can't access Outlook.com", inc.Title);
        Assert.Equal("We're investigating reports that users can't sign in. More info soon.", inc.Summary);
        Assert.Equal(["Outlook.com"], inc.AffectedComponents);
        Assert.Equal(ServiceStatus.Degraded, r.Components.Single(c => c.Name == "Outlook.com").Status);
    }

    [Theory]
    [InlineData("Available", ServiceStatus.Operational)]
    [InlineData("Service restored", ServiceStatus.Operational)]
    [InlineData("Service degradation", ServiceStatus.Degraded)]
    [InlineData("Unknown", ServiceStatus.Unknown)]
    [InlineData("Something new", ServiceStatus.Degraded)]
    public void Status_mapping(string raw, ServiceStatus expected) =>
        Assert.Equal(expected, MicrosoftStatusProvider.MapStatus(raw));
}

public class AwsTests
{
    private static string Events() => HttpFetcher.Decode(Samples.Bytes("aws-currentevents-utf16be.json"), "utf-16");

    [Fact]
    public void Sample_is_utf16_be_with_bom_and_decodes()
    {
        var bytes = Samples.Bytes("aws-currentevents-utf16be.json");
        Assert.Equal(0xFE, bytes[0]);
        Assert.Equal(0xFF, bytes[1]);
        Assert.StartsWith("[{", Events());
    }

    [Fact]
    public void All_regions_shows_middle_east_disruptions()
    {
        var r = AwsHealthProvider.Parse(Events(), "AWS", regions: null);
        Assert.Equal(ServiceStatus.MajorOutage, r.Status);
        Assert.Equal(2, r.Incidents.Count);
        Assert.Contains(r.Incidents, i => i.Title.Contains("UAE"));
        var uae = r.Incidents.First(i => i.Title.Contains("UAE"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1772369485), uae.Started);
        Assert.NotEmpty(uae.AffectedComponents);
        Assert.NotNull(uae.Summary);
    }

    [Fact]
    public void Default_uk_us_global_filter_excludes_middle_east()
    {
        var r = AwsHealthProvider.Parse(Events(), "AWS", AwsHealthProvider.ParseRegions(DefaultVendors.DefaultAwsRegions));
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
        Assert.Empty(r.Components);
    }

    [Fact]
    public void Region_filter_includes_matching_region()
    {
        var r = AwsHealthProvider.Parse(Events(), "AWS", AwsHealthProvider.ParseRegions("me-central-1"));
        Assert.Single(r.Incidents);
    }

    [Fact]
    public void Event_without_region_is_global()
    {
        const string json = """[{"date":"1700000000","arn":"arn:aws:health:global::event/IAM/X/Y","region_name":"Global","status":"2","service":"iam","service_name":"AWS IAM","summary":"Increased latency","event_log":[{"summary":"x","message":"Investigating.","status":2,"timestamp":1700000100}]}]""";
        var r = AwsHealthProvider.Parse(json, "AWS", AwsHealthProvider.ParseRegions("global,eu-west-2"));
        var inc = Assert.Single(r.Incidents);
        Assert.Equal(ServiceStatus.PartialOutage, inc.Status);
        Assert.Equal("Investigating.", inc.Summary);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000100), inc.LastUpdated);
    }

    [Fact]
    public void Empty_array_is_operational() =>
        Assert.Equal(ServiceStatus.Operational, AwsHealthProvider.Parse("[]", "AWS", null).Status);
}

public class RollUpTests
{
    [Fact]
    public void Worst_ignores_unknown()
    {
        Assert.Equal(ServiceStatus.Degraded, ServiceStatus.Unknown.Worst(ServiceStatus.Degraded));
        Assert.Equal(ServiceStatus.MajorOutage, new[] { ServiceStatus.Maintenance, ServiceStatus.MajorOutage, ServiceStatus.Degraded }.Worst());
    }

    [Fact]
    public void Vendor_is_unknown_only_when_every_source_failed()
    {
        var ok = new SourceResult { SourceLabel = "a", Status = ServiceStatus.Operational };
        var bad = SourceResult.Failed("b", "Timed out");
        var mixed = new VendorStatus { VendorId = "v", VendorName = "V", Sources = [ok, bad] };
        Assert.Equal(ServiceStatus.Operational, mixed.Status);
        Assert.Equal(1, mixed.FailedSourceCount);
        var allBad = new VendorStatus { VendorId = "v", VendorName = "V", Sources = [bad] };
        Assert.Equal(ServiceStatus.Unknown, allBad.Status);
    }
}

public class HundFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly FeedProvider.FeedOptions Recent = new(true, 24, ServiceStatus.Degraded);

    [Fact]
    public void Information_ended_and_cancelled_entries_are_not_incidents()
    {
        // Sample: one [Information] announcement from yesterday plus [Ended]/[Cancelled] maintenance windows.
        var r = FeedProvider.Parse(Samples.Text("hund-bitwarden-state.xml"), "Bitwarden", Recent, Now);
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
    }

    [Fact]
    public void Only_newest_entry_per_incident_counts_and_tags_set_status()
    {
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>X</title>
              <entry><title>[Resolved] Login errors</title><link href="https://s.test/issues/1"/><updated>2026-11-19T12:00:00Z</updated></entry>
              <entry><title>[Investigating] Login errors</title><link href="https://s.test/issues/1"/><updated>2026-11-19T10:00:00Z</updated></entry>
              <entry><title>[Major Outage] API down</title><link href="https://s.test/issues/2"/><updated>2026-11-19T11:00:00Z</updated></entry>
              <entry><title>[Started] Maintenance Window: DB</title><link href="https://s.test/issues/3"/><updated>2026-11-19T11:30:00Z</updated></entry>
            </feed>
            """;
        var r = FeedProvider.Parse(feed, "X", Recent, Now);
        Assert.Equal(2, r.Incidents.Count);
        Assert.Contains(r.Incidents, i => i.Title == "[Major Outage] API down" && i.Status == ServiceStatus.MajorOutage);
        Assert.Contains(r.Incidents, i => i.Status == ServiceStatus.Maintenance);
        Assert.DoesNotContain(r.Incidents, i => i.Title.Contains("Login"));
    }

    [Fact]
    public void Feeds_sharing_one_link_are_not_grouped()
    {
        // Azure-style: every item links to the same page; both active items must stay.
        const string rss = """
            <rss version="2.0"><channel><title>A</title>
              <item><title>Active - Storage</title><link>https://a.test/status/</link><pubDate>Thu, 19 Nov 2026 12:00:00 Z</pubDate></item>
              <item><title>Active - Compute</title><link>https://a.test/status/</link><pubDate>Thu, 19 Nov 2026 11:00:00 Z</pubDate></item>
            </channel></rss>
            """;
        Assert.Equal(2, FeedProvider.Parse(rss, "A", FeedProvider.FeedOptions.Default, Now).Incidents.Count);
    }
}
