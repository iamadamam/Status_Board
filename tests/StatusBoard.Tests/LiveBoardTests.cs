using System.Text;
using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

/// <summary>Serves a Statuspage summary for every URL, but holds back hosts listed in <see cref="Gates"/> until released.</summary>
internal sealed class GatedFetcher : IHttpFetcher
{
    public Dictionary<string, TaskCompletionSource> Gates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task<FetchedBytes> GetAsync(string url, CancellationToken ct, FetchOptions? options = null)
    {
        var uri = new Uri(url);
        if (Gates.TryGetValue(uri.Host, out var gate)) await gate.Task.WaitAsync(ct);
        return new FetchedBytes(Encoding.UTF8.GetBytes(Samples.Text("statuspage-forticloud-hub-summary.json")), "application/json", "utf-8", uri);
    }
}

public class ProgressiveRefreshTests
{
    private static AppConfig TwoVendors() => new()
    {
        Vendors =
        [
            new() { Id = "fast", Name = "Fast", Sources = [new() { Type = "statuspage", Url = "https://fast.test" }] },
            new() { Id = "slow", Name = "Slow", Sources = [new() { Type = "statuspage", Url = "https://slow.test" }] },
        ],
    };

    [Fact]
    public async Task A_slow_vendor_does_not_hold_back_a_fast_one()
    {
        var http = new GatedFetcher();
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http.Gates["slow.test"] = slowGate;
        var reported = new List<string>();
        var fastReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = new StatusService(ProviderRegistry.CreateDefault(), http);

        var refresh = svc.RefreshAsync(TwoVendors(), CancellationToken.None, v =>
        {
            lock (reported) reported.Add(v.VendorId);
            if (v.VendorId == "fast") fastReported.TrySetResult();
        });

        // The fast vendor is reported while the slow one is still waiting.
        await fastReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(refresh.IsCompleted);
        lock (reported) Assert.Equal(["fast"], reported);

        slowGate.SetResult();
        var all = await refresh;
        Assert.Equal(["fast", "slow"], reported);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Progressive_results_equal_all_at_once_results()
    {
        var svc = new StatusService(ProviderRegistry.CreateDefault(), new GatedFetcher());
        var plain = await svc.RefreshAsync(TwoVendors(), CancellationToken.None);
        var seen = new List<VendorStatus>();
        var progressive = await svc.RefreshAsync(TwoVendors(), CancellationToken.None, v => { lock (seen) seen.Add(v); });

        Assert.Equal(plain.Select(v => (v.VendorId, v.Status, v.Stage)), progressive.Select(v => (v.VendorId, v.Status, v.Stage)));
        Assert.Equal(progressive.Select(v => v.VendorId).Order(), seen.Select(v => v.VendorId).Order());
    }
}

public class StatusChangesTests
{
    [Theory]
    [InlineData(ServiceStatus.Operational, ServiceStatus.MajorOutage, true)]
    [InlineData(ServiceStatus.PartialOutage, ServiceStatus.Operational, true)]
    [InlineData(ServiceStatus.Degraded, ServiceStatus.Degraded, false)]
    [InlineData(ServiceStatus.Unknown, ServiceStatus.MajorOutage, false)]
    [InlineData(ServiceStatus.MajorOutage, ServiceStatus.Unknown, false)]
    public void Glow_only_between_two_known_different_statuses(ServiceStatus before, ServiceStatus after, bool glow) =>
        Assert.Equal(glow, StatusChanges.ShouldGlow(before, after));

    [Fact]
    public void No_glow_on_first_load() => Assert.False(StatusChanges.ShouldGlow(null, ServiceStatus.MajorOutage));

    [Fact]
    public void Stage_change_notifies_but_does_not_glow()
    {
        Assert.True(StatusChanges.ShouldNotify(ServiceStatus.MajorOutage, IncidentStage.Investigating, ServiceStatus.MajorOutage, IncidentStage.Monitoring));
        Assert.False(StatusChanges.ShouldGlow(ServiceStatus.MajorOutage, ServiceStatus.MajorOutage));
    }

    [Fact]
    public void Changes_collected_over_several_batches_are_handed_over_once()
    {
        var c = new ChangeCollector<string>();
        c.Add("a");
        c.Add("b");
        Assert.Equal(["a", "b"], c.Drain());
        Assert.Empty(c.Drain());
    }
}

public class IncidentDurationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);

    private static VendorStatus Vendor(ServiceStatus status, params DateTimeOffset?[] starts) => new()
    {
        VendorId = "v",
        VendorName = "V",
        Sources = [SourceResult.Of("s", starts.Select(s => new Incident { Title = "x", Status = status, Started = s }).ToList(), status: status)],
    };

    [Theory]
    [InlineData(0, "<1m")]
    [InlineData(45, "45m")]
    [InlineData(134, "2h 14m")]
    [InlineData(120, "2h")]
    [InlineData(4560, "3d 4h")]
    public void Formats_minutes_hours_days(int minutes, string expected) =>
        Assert.Equal(expected, IncidentDuration.Format(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Problem_tile_counts_from_the_earliest_problem_start() =>
        Assert.Equal("for 2h 14m", IncidentDuration.Text(Vendor(ServiceStatus.MajorOutage, Now.AddMinutes(-30), Now.AddMinutes(-134)), Now));

    [Fact]
    public void Healthy_vendor_has_no_duration() => Assert.Null(IncidentDuration.Text(Vendor(ServiceStatus.Operational), Now));

    [Fact]
    public void Unknown_start_has_no_duration() => Assert.Null(IncidentDuration.Text(Vendor(ServiceStatus.PartialOutage, [null]), Now));

    [Fact]
    public void A_start_in_the_future_reads_as_just_started() =>
        Assert.Equal("for <1m", IncidentDuration.Text(Vendor(ServiceStatus.Degraded, Now.AddMinutes(5)), Now));
}

public class StatusHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Records_each_refresh_per_vendor()
    {
        var h = new StatusHistory();
        h.Record("a", ServiceStatus.Operational, Now.AddMinutes(-10));
        h.Record("a", ServiceStatus.MajorOutage, Now.AddMinutes(-5));
        h.Record("b", ServiceStatus.Degraded, Now.AddMinutes(-5));
        var a = h.BucketsFor("a", Now);
        Assert.Equal(ServiceStatus.MajorOutage, a[^1]);
        Assert.Equal(ServiceStatus.Degraded, h.BucketsFor("b", Now)[^1]);
    }

    [Fact]
    public void Has_48_half_hour_buckets_with_worst_status_and_gaps()
    {
        var h = new StatusHistory();
        h.Record("a", ServiceStatus.Operational, Now.AddHours(-3).AddMinutes(-10));
        h.Record("a", ServiceStatus.PartialOutage, Now.AddHours(-3).AddMinutes(-5));
        h.Record("a", ServiceStatus.Operational, Now.AddMinutes(-5));
        var b = h.BucketsFor("a", Now);

        Assert.Equal(StatusHistory.Buckets, b.Count);
        Assert.Equal(48, b.Count);
        Assert.Equal(ServiceStatus.PartialOutage, b[41]); // 3h ago = bucket 48 - 7
        Assert.Null(b[0]);                                 // nothing recorded a day ago
        Assert.Equal(ServiceStatus.Operational, b[47]);
        Assert.Null(b[44]);                                // a gap between refreshes is not invented
    }

    [Fact]
    public void Unknown_never_beats_a_known_status_in_a_bucket()
    {
        var h = new StatusHistory();
        h.Record("a", ServiceStatus.Unknown, Now.AddMinutes(-20));
        h.Record("a", ServiceStatus.Degraded, Now.AddMinutes(-15));
        Assert.Equal(ServiceStatus.Degraded, h.BucketsFor("a", Now)[^1]);
        var only = new StatusHistory();
        only.Record("x", ServiceStatus.Unknown, Now.AddMinutes(-5));
        Assert.Equal(ServiceStatus.Unknown, only.BucketsFor("x", Now)[^1]);
    }

    [Fact]
    public void Reloads_from_its_file_after_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sb-history-{Guid.NewGuid():N}.json");
        try
        {
            var h = new StatusHistory();
            h.Record("a", ServiceStatus.MajorOutage, Now.AddMinutes(-40));
            h.Save(path);
            var back = StatusHistory.Load(path, Now);
            Assert.Equal(ServiceStatus.MajorOutage, back.BucketsFor("a", Now)[^2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Drops_entries_older_than_24_hours()
    {
        var h = new StatusHistory();
        h.Record("a", ServiceStatus.MajorOutage, Now.AddHours(-30));
        h.Record("a", ServiceStatus.Operational, Now.AddMinutes(-5));
        h.Prune(Now);
        Assert.Equal(1, h.SegmentCount("a"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public void An_unreadable_file_starts_an_empty_history(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sb-history-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, content);
            var h = StatusHistory.Load(path, Now);
            Assert.All(h.BucketsFor("a", Now), Assert.Null);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_missing_file_starts_an_empty_history() =>
        Assert.All(StatusHistory.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"), Now).BucketsFor("a", Now), Assert.Null);
}

public class YearProgressTests
{
    [Fact]
    public void Fraction_is_the_share_of_the_year_elapsed()
    {
        Assert.Equal(0.0, YearProgress.Fraction(new DateTime(2026, 1, 1)), 6);
        Assert.Equal(330.0 / 365, YearProgress.Fraction(new DateTime(2026, 11, 27)), 6);
        Assert.Equal(0.5, YearProgress.Fraction(new DateTime(2028, 7, 2)), 6); // leap year: 183 of 366 days
    }

    [Theory]
    [InlineData(10, 0, 5, true)]   // on the interval, first 15 s
    [InlineData(10, 14, 5, true)]
    [InlineData(10, 15, 5, false)] // after 15 s, back to the clock
    [InlineData(11, 0, 5, false)]  // between intervals
    [InlineData(15, 3, 5, true)]
    [InlineData(30, 3, 60, false)]
    [InlineData(0, 3, 60, true)]
    public void Shows_for_15_seconds_at_each_interval(int minute, int second, int interval, bool showing) =>
        Assert.Equal(showing, YearProgress.IsShowing(new DateTime(2026, 11, 27, 14, minute, second), interval));

    [Fact]
    public void Off_never_shows() => Assert.False(YearProgress.IsShowing(new DateTime(2026, 11, 27, 14, 0, 0), 0));

    [Fact]
    public void Cycle_settings_round_trip_and_bad_values_are_repaired()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sb-config-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            var c = AppConfig.CreateDefault();
            (c.YearProgressCycle, c.YearProgressIntervalMinutes) = (false, 15);
            store.Save(c);
            var back = store.Load();
            Assert.False(back.YearProgressCycle);
            Assert.Equal(15, back.YearProgressIntervalMinutes);

            c.YearProgressIntervalMinutes = 7;
            store.Save(c);
            Assert.Equal(YearProgress.DefaultIntervalMinutes, store.Load().YearProgressIntervalMinutes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void On_by_default_every_5_minutes()
    {
        var c = AppConfig.CreateDefault();
        Assert.True(c.YearProgressCycle);
        Assert.Equal(5, c.YearProgressIntervalMinutes);
    }
}

public class ObsidianiteThemeTests
{
    [Fact]
    public void Obsidianite_is_a_known_theme_and_survives_a_save()
    {
        Assert.Contains("Obsidianite", AppConfig.ThemeNames);
        var path = Path.Combine(Path.GetTempPath(), $"sb-config-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            var c = AppConfig.CreateDefault();
            c.Theme = "obsidianite";
            store.Save(c);
            Assert.Equal("Obsidianite", store.Load().Theme);
        }
        finally { File.Delete(path); }
    }
}
