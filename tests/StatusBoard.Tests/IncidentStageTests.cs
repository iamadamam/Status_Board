using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

/// <summary>Incident stages (Investigating, Identified, Monitoring, In progress…) read from each kind of source.</summary>
public class IncidentStageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("investigating", IncidentStage.Investigating)]
    [InlineData("Identified", IncidentStage.Identified)]
    [InlineData("monitoring", IncidentStage.Monitoring)]
    [InlineData("in_progress", IncidentStage.InProgress)]
    [InlineData("In Progress", IncidentStage.InProgress)]
    [InlineData("verifying", IncidentStage.Verifying)]
    [InlineData("scheduled", IncidentStage.Scheduled)]
    [InlineData("update", IncidentStage.Update)]
    [InlineData("notice", IncidentStage.Notice)]
    [InlineData("resolved", IncidentStage.None)]
    [InlineData("", IncidentStage.None)]
    [InlineData(null, IncidentStage.None)]
    [InlineData("🙂 something new", IncidentStage.None)]
    public void Vendor_words_map_to_stages(string? word, IncidentStage expected) => Assert.Equal(expected, IncidentStages.FromWord(word));

    [Theory]
    [InlineData("Monitoring - A fix has been implemented", IncidentStage.Monitoring)]
    [InlineData("[Identified] Root cause found", IncidentStage.Identified)]
    [InlineData("Update: still working on it", IncidentStage.Update)]
    [InlineData("  Investigating – reports of errors", IncidentStage.Investigating)]
    [InlineData("In progress", IncidentStage.InProgress)]
    [InlineData("We are investigating reports of errors", IncidentStage.None)]   // prose, not a label
    [InlineData("Monitoring the situation closely", IncidentStage.None)]
    [InlineData("The scheduled maintenance is under way", IncidentStage.None)]
    [InlineData("", IncidentStage.None)]
    [InlineData(null, IncidentStage.None)]
    public void Only_leading_labels_are_read_from_text(string? text, IncidentStage expected) => Assert.Equal(expected, IncidentStages.FromText(text));

    [Fact]
    public void Statuspage_incident_stages_come_from_the_incident_status()
    {
        var ztp = StatuspageProvider.Parse(Samples.Text("statuspage-fortiztp-summary.json"), "FortiZTP");
        Assert.All(ztp.Incidents, i => Assert.Equal(IncidentStage.Investigating, i.Stage));
        var siem = StatuspageProvider.Parse(Samples.Text("statuspage-fortisiem-summary.json"), "FortiSIEM");
        Assert.All(siem.Incidents, i => Assert.Equal(IncidentStage.Monitoring, i.Stage));
    }

    [Theory]
    [InlineData("in_progress", IncidentStage.InProgress)]
    [InlineData("verifying", IncidentStage.Verifying)]
    public void Statuspage_maintenance_stages(string state, IncidentStage expected)
    {
        var json = Samples.Text("statuspage-forticlient-summary.json").Replace("\"status\":\"scheduled\"", $"\"status\":\"{state}\"");
        var r = StatuspageProvider.Parse(json, "FortiClient");
        Assert.NotEmpty(r.Incidents);
        Assert.All(r.Incidents, i => { Assert.Equal(ServiceStatus.Maintenance, i.Status); Assert.Equal(expected, i.Stage); });
    }

    [Fact]
    public void Statuspage_upcoming_maintenance_is_still_ignored() =>
        Assert.Empty(StatuspageProvider.Parse(Samples.Text("statuspage-forticlient-summary.json"), "FortiClient").Incidents);

    [Fact]
    public void StatusIo_stage_is_the_latest_message_state()
    {
        var r = StatusIoProvider.Parse(Samples.Text("statusio-connectwise.json"), "ConnectWise");
        Assert.Equal(IncidentStage.Monitoring, r.Incidents.Single(i => i.Title.Contains("x360Sync")).Stage);   // latest state 300
        Assert.Equal(IncidentStage.Update, r.Incidents.Single(i => i.Title.Contains("Support Case")).Stage);   // state 400
    }

    [Theory]
    [InlineData("100", IncidentStage.Investigating)]
    [InlineData("200", IncidentStage.Identified)]
    [InlineData("300", IncidentStage.Monitoring)]
    [InlineData("400", IncidentStage.Update)]
    [InlineData(null, IncidentStage.None)]
    public void StatusIo_state_codes(string? code, IncidentStage expected) => Assert.Equal(expected, StatusIoProvider.MapState(code));

    [Theory]
    [InlineData("investigatingCauseOfIssue", IncidentStage.Investigating)]
    [InlineData("rootCauseIdentified", IncidentStage.Identified)]
    [InlineData("workaroundAvailable", IncidentStage.Monitoring)]
    [InlineData("update", IncidentStage.Update)]
    [InlineData(null, IncidentStage.None)]
    public void Salesforce_event_types(string? type, IncidentStage expected) => Assert.Equal(expected, SalesforceProvider.MapEvent(type));

    private const string FeedTemplate = """
        <?xml version="1.0"?><rss version="2.0"><channel><title>t</title>
        <item><title>{0}</title><link>https://example.com/i/1</link><description>{1}</description><pubDate>Sun, 22 Nov 2026 11:00:00 GMT</pubDate></item>
        <item><title>Other</title><link>https://example.com/i/2</link><description>x</description><pubDate>Sun, 22 Nov 2026 11:00:00 GMT</pubDate></item>
        </channel></rss>
        """;

    private static SourceResult Feed(string title, string body) =>
        FeedProvider.Parse(string.Format(FeedTemplate, title, System.Security.SecurityElement.Escape(body)), "Feed", FeedProvider.FeedOptions.Default, Now);

    [Fact]
    public void Feed_stage_from_the_newest_update_label()
    {
        var i = Feed("API errors", "<p><strong>Monitoring</strong> - A fix has been deployed.</p><p><strong>Investigating</strong> - We are looking into it.</p>")
            .Incidents.Single(x => x.Title == "API errors");
        Assert.Equal(IncidentStage.Monitoring, i.Stage);
    }

    [Fact]
    public void Feed_prose_is_not_read_as_a_stage()
    {
        var i = Feed("API errors", "We are investigating and the scheduled fix will follow.").Incidents.Single(x => x.Title == "API errors");
        Assert.Equal(IncidentStage.None, i.Stage);
    }

    [Fact]
    public void Feed_maintenance_announced_but_not_started_is_not_current()
    {
        var r = Feed("Scheduled maintenance of database", "<strong>Scheduled</strong> - We will be upgrading on 30 Nov.");
        Assert.DoesNotContain(r.Incidents, i => i.Title.StartsWith("Scheduled maintenance"));
    }

    [Fact]
    public void Feed_maintenance_under_way_is_in_progress()
    {
        var r = Feed("Database maintenance", "<strong>In progress</strong> - Upgrading now. <strong>Scheduled</strong> - Planned.");
        var m = r.Incidents.Single(i => i.Title == "Database maintenance");
        Assert.Equal(ServiceStatus.Maintenance, m.Status);
        Assert.Equal(IncidentStage.InProgress, m.Stage);
    }

    [Fact]
    public void Sources_without_stages_fall_back_to_a_leading_label_in_the_update()
    {
        var r = SourceResult.Of("x", [new Incident { Title = "Email delays", Summary = "Identified - Cause found." }, new Incident { Title = "Other", Summary = "We're on it." }]);
        Assert.Equal(IncidentStage.Identified, r.Incidents[0].Stage);
        Assert.Equal(IncidentStage.None, r.Incidents[1].Stage);
    }

    [Fact]
    public void Maintenance_without_a_stage_is_in_progress()
    {
        var r = SourceResult.Of("x", [new Incident { Title = "Window", Status = ServiceStatus.Maintenance }]);
        Assert.Equal(IncidentStage.InProgress, r.Incidents[0].Stage);
    }

    [Fact]
    public void Vendor_stage_is_the_least_progressed_of_its_worst_incidents()
    {
        Incident I(ServiceStatus s, IncidentStage st) => new() { Title = $"{s} {st}", Status = s, Stage = st };
        var v = new VendorStatus
        {
            VendorId = "v", VendorName = "V",
            Sources =
            [
                SourceResult.Of("a", [I(ServiceStatus.MajorOutage, IncidentStage.Monitoring), I(ServiceStatus.Degraded, IncidentStage.Investigating)]),
                SourceResult.Of("b", [I(ServiceStatus.MajorOutage, IncidentStage.Identified)]),
                SourceResult.Failed("c", "down"),
            ],
        };
        Assert.Equal(IncidentStage.Identified, v.Stage); // major outages only; Identified is earlier than Monitoring
    }

    [Fact]
    public void A_problem_from_components_never_borrows_a_maintenance_stage()
    {
        // Palo Alto, seen live: Degraded by one component while its only incidents are maintenance windows.
        var v = new VendorStatus
        {
            VendorId = "v", VendorName = "V",
            Sources = [SourceResult.Of("a",
                [new Incident { Title = "Window", Status = ServiceStatus.Maintenance, Stage = IncidentStage.InProgress }],
                [new ComponentStatus("API", ServiceStatus.Degraded)], ServiceStatus.Degraded)], // providers pass the overall status
        };
        Assert.Equal(ServiceStatus.Degraded, v.Status);
        Assert.Equal(IncidentStage.None, v.Stage);
    }

    [Fact]
    public void A_problem_from_components_uses_a_lesser_problem_incidents_stage()
    {
        var v = new VendorStatus
        {
            VendorId = "v", VendorName = "V",
            Sources = [SourceResult.Of("a",
                [new Incident { Title = "Slow", Status = ServiceStatus.Degraded, Stage = IncidentStage.Identified },
                 new Incident { Title = "Window", Status = ServiceStatus.Maintenance, Stage = IncidentStage.InProgress }],
                [new ComponentStatus("API", ServiceStatus.MajorOutage)], ServiceStatus.MajorOutage)],
        };
        Assert.Equal(IncidentStage.Identified, v.Stage);
    }

    [Fact]
    public void Statuspage_affected_component_names_are_not_repeated()
    {
        var json = Samples.Text("statuspage-fortiztp-summary.json");
        var r = StatuspageProvider.Parse(json, "FortiZTP");
        Assert.All(r.Incidents, i => Assert.Equal(i.AffectedComponents.Count, i.AffectedComponents.Distinct().Count()));
    }

    [Fact]
    public void Vendor_with_no_incidents_has_no_stage()
    {
        var v = new VendorStatus { VendorId = "v", VendorName = "V", Sources = [SourceResult.Of("a", [])] };
        Assert.Equal(IncidentStage.None, v.Stage);
    }

    [Fact]
    public void Ticker_carries_the_stage()
    {
        var v = new VendorStatus
        {
            VendorId = "v", VendorName = "V",
            Sources = [SourceResult.Of("a", [new Incident { Title = "Down", Status = ServiceStatus.MajorOutage, Stage = IncidentStage.Investigating }])],
        };
        Assert.Equal(IncidentStage.Investigating, NewsTicker.Build([v]).Single().Stage);
    }

    [Fact]
    public void Display_names_and_recovery()
    {
        Assert.Equal("In progress", IncidentStage.InProgress.ToDisplay());
        Assert.Null(IncidentStage.None.ToDisplay());
        Assert.True(IncidentStage.Monitoring.IsRecovering());
        Assert.True(IncidentStage.Verifying.IsRecovering());
        Assert.False(IncidentStage.Investigating.IsRecovering());
        Assert.All(Enum.GetValues<IncidentStage>().Where(s => s != IncidentStage.None), s => Assert.NotNull(s.ToDisplay()));
    }
}
