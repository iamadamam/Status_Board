using System.Text.RegularExpressions;

namespace StatusBoard.Core;

/// <summary>
/// Fetches every enabled source of every enabled vendor in parallel and rolls results up per vendor. Sources are isolated:
/// an error or timeout in one becomes an Unknown result for that source only.
/// </summary>
public sealed class StatusService(ProviderRegistry registry, IHttpFetcher http)
{
    /// <summary>Cap for one source including retries, so one slow vendor can't stall a refresh.</summary>
    public TimeSpan SourceDeadline { get; init; } = TimeSpan.FromSeconds(60);

    /// <param name="onVendor">Called with each vendor's result the moment that vendor finishes (on a background thread), so the
    /// board can show it without waiting for the slowest vendor. The returned list still holds every result.</param>
    public async Task<IReadOnlyList<VendorStatus>> RefreshAsync(AppConfig config, CancellationToken ct, Action<VendorStatus>? onVendor = null) =>
        await Task.WhenAll(config.Vendors.Where(v => v.Enabled).Select(async v =>
        {
            var result = await RefreshVendorAsync(v, ct).ConfigureAwait(false);
            onVendor?.Invoke(result);
            return result;
        })).ConfigureAwait(false);

    /// <param name="applyFilters">False when listing every component for the picker.</param>
    /// <param name="sourcesFilter">Which sources to read (default: the enabled ones).</param>
    public async Task<VendorStatus> RefreshVendorAsync(VendorConfig vendor, CancellationToken ct, bool applyFilters = true, Func<SourceConfig, bool>? sourcesFilter = null) => new()
    {
        VendorId = vendor.Id,
        VendorName = vendor.Name,
        StatusPageUrl = vendor.StatusPageUrl,
        Sources = await Task.WhenAll(vendor.Sources.Where(sourcesFilter ?? (s => s.Enabled)).Select(s => FetchAsync(s, ct, applyFilters))).ConfigureAwait(false),
        CheckedAt = DateTimeOffset.Now,
    };

    /// <summary>Vendors made only of your own website/ping checks (refreshed every minute, apart from the rest).</summary>
    public static bool IsCheck(VendorConfig v) => v.Sources.Count > 0 && v.Sources.All(s => IsCheckType(s.Type));
    public static bool IsCheckType(string type) => type.Equals("http", StringComparison.OrdinalIgnoreCase) || type.Equals("ping", StringComparison.OrdinalIgnoreCase);

    private async Task<SourceResult> FetchAsync(SourceConfig source, CancellationToken ct, bool applyFilters = true)
    {
        var label = string.IsNullOrWhiteSpace(source.Label) ? source.Url : source.Label;
        if (registry.Find(source.Type) is not { } provider) return SourceResult.Failed(label, $"No adapter for type '{source.Type}'");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SourceDeadline);
        try
        {
            var result = await provider.FetchAsync(source, http, cts.Token).ConfigureAwait(false) with { SourceLabel = label };
            return applyFilters ? ComponentFilter.Apply(result, source.Components) : result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SourceResult.Failed(label, "Timed out"); }
        catch (FetchException ex) { return SourceResult.Failed(label, ex.Message); }
        catch (ParseException ex) { return SourceResult.Failed(label, "Unexpected response: " + ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return SourceResult.Failed(label, "Error: " + ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>At most this many sources are fetched at once: a refresh is a gentle trickle of requests, not a burst that
    /// firewalls or intrusion detection might flag.</summary>
    public const int MaxParallel = 12;
    private readonly SemaphoreSlim _gate = new(MaxParallel);
}

/// <summary>One ticker item: an active incident (or an unexplained problem) at one vendor.</summary>
public sealed record TickerEntry(string VendorId, string VendorName, ServiceStatus Status, string Headline, string? Detail, DateTimeOffset? Updated,
    IncidentStage Stage = IncidentStage.None)
{
    /// <summary>Identity across refreshes: the same incident keeps its key while its text and time change.</summary>
    public string Key => $"{VendorId}|{Headline}";
}

/// <summary>Builds the news ticker: one entry per active incident at vendors with a problem or maintenance, worst first, then newest.</summary>
public static partial class NewsTicker
{
    public const int MaxEntries = 30, MaxDetailLength = 220;

    public static IReadOnlyList<TickerEntry> Build(IEnumerable<VendorStatus> vendors) => vendors
        .Where(v => v.Status.IsProblem() || v.Status == ServiceStatus.Maintenance)
        .SelectMany(Entries)
        .GroupBy(e => e.Key).Select(g => g.First()) // the same incident from two sources once
        .OrderByDescending(e => (int)e.Status).ThenByDescending(e => e.Updated ?? DateTimeOffset.MinValue)
        .Take(MaxEntries).ToList();

    private static IEnumerable<TickerEntry> Entries(VendorStatus v)
    {
        var incidents = v.Sources.Where(s => s.Succeeded).SelectMany(s => s.Incidents)
            .Where(i => i.Status.IsProblem() || i.Status == ServiceStatus.Maintenance).ToList();
        if (incidents.Count > 0)
            return incidents.Select(i => new TickerEntry(v.VendorId, v.VendorName, i.Status, OneLine(i.Title) ?? "Incident", Shorten(i.Summary), i.LastUpdated ?? i.Started, i.Stage));

        // A problem reported only through component states: say which.
        var comps = v.AffectedComponents.OrderByDescending(c => (int)c.Status).Select(c => c.Name).ToList();
        var headline = comps.Count switch { 0 => v.Status.ToDisplay(), 1 => $"{comps[0]} affected", _ => $"{comps.Count} components affected" };
        var detail = comps.Count > 1 ? Shorten(string.Join(", ", comps.Take(6)) + (comps.Count > 6 ? $" and {comps.Count - 6} more" : "")) : null;
        return [new TickerEntry(v.VendorId, v.VendorName, v.Status, headline, detail, v.CheckedAt)];
    }

    /// <summary>First sentences of an update on one line, cut at a word boundary.</summary>
    public static string? Shorten(string? text, int max = MaxDetailLength)
    {
        var s = OneLine(text);
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        var cut = s.LastIndexOf(' ', max);
        return s[..(cut > max / 2 ? cut : max)].TrimEnd(',', ';', ':', ' ') + "…";
    }

    private static string? OneLine(string? text) => TextUtil.Clean(text, 2000) is { } s && !string.IsNullOrWhiteSpace(s) ? Spaces().Replace(s, " ").Trim() : null;
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
