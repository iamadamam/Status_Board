using System.Text.Json;
using System.Text.Json.Serialization;

namespace StatusBoard.Core;

/// <summary>When a vendor's change is worth showing: a glow on its tile, or a notification.</summary>
public static class StatusChanges
{
    /// <summary>The tile glows only when the status moved between two known states. First load (no previous result) and a
    /// feed that couldn't be reached are not vendor incidents.</summary>
    public static bool ShouldGlow(ServiceStatus? previous, ServiceStatus current) =>
        previous is { } p && p != ServiceStatus.Unknown && current != ServiceStatus.Unknown && p != current;

    /// <summary>A result checked before the one already shown (a slow website check landing after a newer refresh) is out of date.</summary>
    public static bool IsStale(DateTimeOffset? shownCheckedAt, DateTimeOffset incomingCheckedAt) =>
        shownCheckedAt is { } shown && incomingCheckedAt < shown;

    /// <summary>A notification also covers an incident moving on at the same status (Investigating → Monitoring).</summary>
    public static bool ShouldNotify(ServiceStatus? previous, IncidentStage previousStage, ServiceStatus current, IncidentStage currentStage) =>
        previous is { } p && p != ServiceStatus.Unknown && current != ServiceStatus.Unknown
        && (p != current
            || (current != ServiceStatus.Operational && previousStage != IncidentStage.None && currentStage != IncidentStage.None && previousStage != currentStage));
}

/// <summary>Collects items that arrive in several batches during one refresh, so they can be handed over once at the end.</summary>
public sealed class ChangeCollector<T>
{
    private readonly List<T> _items = [];
    public void Add(T item) => _items.Add(item);

    /// <summary>Everything collected so far; empties the collector.</summary>
    public IReadOnlyList<T> Drain()
    {
        var all = _items.ToList();
        _items.Clear();
        return all;
    }
}

/// <summary>How long a vendor's current problem has lasted ("for 2h 14m").</summary>
public static class IncidentDuration
{
    /// <summary>The earliest start of the vendor's active problem incidents; null when healthy or no start time is given.</summary>
    public static DateTimeOffset? ProblemSince(VendorStatus? vendor) =>
        vendor is null || !vendor.Status.IsProblem() ? null
        : vendor.Sources.Where(s => s.Succeeded).SelectMany(s => s.Incidents)
            .Where(i => i.Status.IsProblem() && i.Started is not null).Select(i => i.Started).Min();

    public static string? Text(VendorStatus? vendor, DateTimeOffset now) =>
        ProblemSince(vendor) is { } since ? "for " + Format(now - since) : null;

    /// <summary>"&lt;1m", "45m", "2h 14m", "2h", "3d 4h".</summary>
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return "<1m";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes}m";
        if (span < TimeSpan.FromDays(1)) return span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        return span.Hours == 0 ? $"{(int)span.TotalDays}d" : $"{(int)span.TotalDays}d {span.Hours}h";
    }
}

/// <summary>
/// The last 24 hours of each vendor's status, for the strip along the bottom of its tile. Stored compactly as runs of the same
/// status (a run grows while refreshes keep reporting it), so a day of five-minute refreshes is a handful of entries per vendor.
/// Kept in <see cref="FileName"/> next to the app.
/// </summary>
public sealed class StatusHistory
{
    public const string FileName = "history.json";
    public const int Buckets = 48;
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public static readonly TimeSpan BucketLength = Window / Buckets;
    /// <summary>Two reports further apart than this are separate runs: the time between them is shown as "no data".</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(20);

    /// <summary>The gap to allow at a refresh interval: twice the interval, never under <see cref="MaxGap"/>, so hourly refreshes
    /// still read as one unbroken run while a long time with the app closed still shows as a gap.</summary>
    public static TimeSpan GapFor(int refreshMinutes) =>
        refreshMinutes > 0 && TimeSpan.FromMinutes(2 * refreshMinutes) > MaxGap ? TimeSpan.FromMinutes(2 * refreshMinutes) : MaxGap;

    /// <summary>One run of the same status from <see cref="From"/> to <see cref="To"/>.</summary>
    public sealed record Run([property: JsonPropertyName("f")] DateTimeOffset From, [property: JsonPropertyName("t")] DateTimeOffset To,
        [property: JsonPropertyName("s")] ServiceStatus Status);

    private readonly Dictionary<string, List<Run>> _runs;

    public StatusHistory() : this(new Dictionary<string, List<Run>>(StringComparer.OrdinalIgnoreCase)) { }
    private StatusHistory(Dictionary<string, List<Run>> runs) => _runs = runs;

    public void Record(string vendorId, ServiceStatus status, DateTimeOffset at, TimeSpan? maxGap = null)
    {
        if (!_runs.TryGetValue(vendorId, out var runs)) _runs[vendorId] = runs = [];
        if (runs.Count > 0 && at < runs[^1].To) return; // out of order (an older result arriving late): the newer one stands
        if (runs.Count > 0 && runs[^1] is var last && last.Status == status && at - last.To <= (maxGap ?? MaxGap))
            runs[^1] = last with { To = at };
        else runs.Add(new Run(at, at, status));
    }

    /// <summary>Forgets runs that ended more than 24 hours before <paramref name="now"/>.</summary>
    public void Prune(DateTimeOffset now)
    {
        var cutoff = now - Window;
        foreach (var key in _runs.Keys.ToList())
        {
            _runs[key].RemoveAll(r => r.To < cutoff);
            if (_runs[key].Count == 0) _runs.Remove(key);
        }
    }

    public int SegmentCount(string vendorId) => _runs.TryGetValue(vendorId, out var r) ? r.Count : 0;

    /// <summary><see cref="Buckets"/> half-hour blocks, oldest first: the worst status reported in each, or null when nothing was.</summary>
    public IReadOnlyList<ServiceStatus?> BucketsFor(string vendorId, DateTimeOffset now)
    {
        var result = new ServiceStatus?[Buckets];
        if (!_runs.TryGetValue(vendorId, out var runs)) return result;
        var start = now - Window;
        for (var i = 0; i < Buckets; i++)
        {
            var (from, to) = (start + BucketLength * i, start + BucketLength * (i + 1));
            var statuses = runs.Where(r => r.From < to && r.To >= from).Select(r => r.Status).ToList();
            if (statuses.Count == 0) continue;
            var known = statuses.Where(s => s != ServiceStatus.Unknown).ToList();
            result[i] = known.Count > 0 ? known.Worst() : ServiceStatus.Unknown;
        }
        return result;
    }

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The saved history, pruned to 24 hours; empty when the file is missing or can't be read (never an error).</summary>
    public static StatusHistory Load(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)) return new StatusHistory();
            var data = JsonSerializer.Deserialize<Dictionary<string, List<Run>>>(File.ReadAllText(path), Options);
            var clean = new Dictionary<string, List<Run>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, runs) in data ?? [])
                if (!string.IsNullOrWhiteSpace(id) && runs is not null) clean[id] = runs.Where(r => r is not null && r.To >= r.From).OrderBy(r => r.From).ToList();
            var history = new StatusHistory(clean);
            history.Prune(now);
            return history;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new StatusHistory();
        }
    }

    /// <summary>Atomic: temp file + replace, so a crash can't leave half a file.</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_runs, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public bool TrySave(string path)
    {
        try { Save(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>The header clock steps aside now and then for a bar showing how far through the year we are.</summary>
public static class YearProgress
{
    public static readonly int[] IntervalChoices = [1, 5, 10, 15, 30, 60];
    public const int DefaultIntervalMinutes = 5;
    public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(15);

    /// <summary>Share of the year elapsed at <paramref name="local"/>: 0 at midnight on 1 January, just under 1 at the end of December.</summary>
    public static double Fraction(DateTime local)
    {
        var start = new DateTime(local.Year, 1, 1);
        return (local - start).TotalSeconds / (start.AddYears(1) - start).TotalSeconds;
    }

    /// <summary>True during the first <see cref="ShowFor"/> of every interval (on the clock: 10:00, 10:05… for 5 minutes).
    /// An interval of 0 or less means off.</summary>
    public static bool IsShowing(DateTime local, int intervalMinutes) =>
        intervalMinutes > 0 && (local.Hour * 60 + local.Minute) % intervalMinutes == 0
        && local.TimeOfDay - new TimeSpan(local.Hour, local.Minute, 0) < ShowFor;

    public static int NormaliseInterval(int minutes) => IntervalChoices.Contains(minutes) ? minutes : DefaultIntervalMinutes;
}
