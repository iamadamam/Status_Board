using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

/// <summary>The news ticker follows status changes and incident updates, worst first.</summary>
public class NewsTickerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 14, 0, 0, TimeSpan.Zero);

    private static VendorStatus Vendor(string id, params Incident[] incidents) => Vendor(id, null, incidents);

    private static VendorStatus Vendor(string id, ServiceStatus? status, Incident[] incidents, params ComponentStatus[] components) => new()
    {
        VendorId = id,
        VendorName = id.ToUpperInvariant(),
        Sources =
        [
            new SourceResult
            {
                SourceLabel = id,
                Status = status ?? incidents.Select(i => i.Status).Worst(),
                Incidents = incidents,
                Components = components,
            },
        ],
    };

    private static Incident Inc(string title, ServiceStatus s, string? summary = null, int minutes = 0) =>
        new() { Title = title, Status = s, Summary = summary, LastUpdated = T0.AddMinutes(minutes) };

    [Fact]
    public void Healthy_and_unreachable_vendors_are_not_news()
    {
        var unreachable = new VendorStatus { VendorId = "x", VendorName = "X", Sources = [SourceResult.Failed("x", "timeout")] };
        Assert.Empty(NewsTicker.Build([Vendor("ok"), unreachable]));
    }

    [Fact]
    public void Worst_first_then_most_recently_updated()
    {
        var list = NewsTicker.Build(
        [
            Vendor("a", Inc("Slow API", ServiceStatus.Degraded, minutes: 30)),
            Vendor("b", Inc("Login down", ServiceStatus.MajorOutage, minutes: 5)),
            Vendor("c", Inc("Old degradation", ServiceStatus.Degraded, minutes: 1), Inc("Patch window", ServiceStatus.Maintenance, minutes: 50)),
        ]);
        Assert.Equal(["Login down", "Slow API", "Old degradation", "Patch window"], list.Select(e => e.Headline));
        Assert.Equal("B", list[0].VendorName);
    }

    [Fact]
    public void An_incident_update_changes_the_entry_but_keeps_its_identity()
    {
        var before = NewsTicker.Build([Vendor("a", Inc("Email delays", ServiceStatus.PartialOutage, "We are investigating.", 0))]).Single();
        var after = NewsTicker.Build([Vendor("a", Inc("Email delays", ServiceStatus.Degraded, "A fix has been deployed; monitoring.", 20))]).Single();
        Assert.Equal(before.Key, after.Key);
        Assert.NotEqual(before, after); // so the UI replaces it
        Assert.Equal("A fix has been deployed; monitoring.", after.Detail);
        Assert.Equal(ServiceStatus.Degraded, after.Status);
        Assert.Equal(T0.AddMinutes(20), after.Updated);
    }

    [Fact]
    public void Resolution_removes_the_entry_and_a_new_problem_adds_one()
    {
        var first = NewsTicker.Build([Vendor("a", Inc("Outage", ServiceStatus.MajorOutage)), Vendor("b")]);
        var second = NewsTicker.Build([Vendor("a"), Vendor("b", Inc("New issue", ServiceStatus.Degraded))]);
        Assert.Equal(["A"], first.Select(e => e.VendorName));
        Assert.Equal(["B"], second.Select(e => e.VendorName));
    }

    [Fact]
    public void Problem_without_incidents_names_the_affected_components()
    {
        var v = Vendor("a", ServiceStatus.PartialOutage, [],
            new ComponentStatus("EU West", ServiceStatus.PartialOutage), new ComponentStatus("US East", ServiceStatus.Degraded), new ComponentStatus("Asia", ServiceStatus.Operational));
        var e = NewsTicker.Build([v]).Single();
        Assert.Equal("2 components affected", e.Headline);
        Assert.Equal("EU West, US East", e.Detail);
        Assert.Equal(ServiceStatus.PartialOutage, e.Status);
    }

    [Fact]
    public void Detail_is_one_clean_line_cut_at_a_word()
    {
        var html = "<p>We are <b>investigating</b> reports of errors.</p><p>" + string.Join(" ", Enumerable.Repeat("More detail follows here.", 20)) + "</p>";
        var e = NewsTicker.Build([Vendor("a", Inc("Errors", ServiceStatus.Degraded, html))]).Single();
        Assert.StartsWith("We are investigating reports of errors. More detail", e.Detail);
        Assert.DoesNotContain("\n", e.Detail);
        Assert.DoesNotContain("<", e.Detail);
        Assert.EndsWith("…", e.Detail);
        Assert.True(e.Detail!.Length <= NewsTicker.MaxDetailLength + 1);
    }

    [Fact]
    public void Same_incident_from_two_sources_appears_once_and_list_is_capped()
    {
        var twice = new VendorStatus
        {
            VendorId = "a", VendorName = "A",
            Sources =
            [
                new SourceResult { SourceLabel = "1", Status = ServiceStatus.Degraded, Incidents = [Inc("Shared incident", ServiceStatus.Degraded)] },
                new SourceResult { SourceLabel = "2", Status = ServiceStatus.Degraded, Incidents = [Inc("Shared incident", ServiceStatus.Degraded)] },
            ],
        };
        Assert.Single(NewsTicker.Build([twice]));

        var many = Vendor("m", Enumerable.Range(0, 50).Select(i => Inc($"Incident {i}", ServiceStatus.Degraded, minutes: i)).ToArray());
        Assert.Equal(NewsTicker.MaxEntries, NewsTicker.Build([many]).Count);
    }
}
