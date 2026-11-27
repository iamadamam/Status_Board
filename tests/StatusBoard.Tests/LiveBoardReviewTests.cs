using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

/// <summary>Cases found in review of the live-board change: slow refresh intervals and out-of-order results.</summary>
public class StatusHistoryGapTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(5, 20)]
    [InlineData(15, 30)]
    [InlineData(60, 120)]
    [InlineData(0, 20)] // manual refresh
    public void Merge_gap_follows_the_refresh_interval(int refreshMinutes, int gapMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(gapMinutes), StatusHistory.GapFor(refreshMinutes));

    [Fact]
    public void Hourly_refreshes_leave_no_holes_in_the_strip()
    {
        var h = new StatusHistory();
        for (var t = Now.AddHours(-23).AddMinutes(-50); t <= Now; t = t.AddHours(1))
            h.Record("a", ServiceStatus.Operational, t, StatusHistory.GapFor(60));
        var b = h.BucketsFor("a", Now);
        var (first, last) = (b.ToList().FindIndex(x => x is not null), b.ToList().FindLastIndex(x => x is not null));
        Assert.True(last - first >= 44, $"{first}..{last}");
        Assert.All(b.Skip(first).Take(last - first + 1), x => Assert.Equal(ServiceStatus.Operational, x)); // no holes between reports
    }

    [Fact]
    public void Without_the_wider_gap_hourly_refreshes_do_leave_holes() // the default stays strict
    {
        var h = new StatusHistory();
        for (var t = Now.AddHours(-23).AddMinutes(-50); t <= Now; t = t.AddHours(1)) h.Record("a", ServiceStatus.Operational, t);
        Assert.Contains(h.BucketsFor("a", Now).Skip(4), x => x is null);
    }
}

public class StaleResultTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_older_result_than_the_one_shown_is_stale() => Assert.True(StatusChanges.IsStale(Now, Now.AddSeconds(-30)));

    [Fact]
    public void A_newer_result_is_not_stale() => Assert.False(StatusChanges.IsStale(Now, Now.AddSeconds(30)));

    [Fact]
    public void Nothing_shown_yet_is_never_stale() => Assert.False(StatusChanges.IsStale(null, Now));
}
