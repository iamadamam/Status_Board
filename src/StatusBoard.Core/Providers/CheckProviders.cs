using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;

namespace StatusBoard.Core;

/// <summary>
/// Your own checks ("http": a website answers with an expected code; "ping": a host answers ping). To avoid flapping on a brief blip a
/// check is only marked down (or slow) after <c>failuresBeforeDown</c> bad results in a row (default 3). Options: <c>slowMs</c>
/// (default 2000 for websites, 300 for ping), <c>expect</c> ("200-399", "200,301", "401"), <c>timeoutSeconds</c> (default 10),
/// <c>allowInvalidCertificate</c>.
/// </summary>
public abstract class CheckProvider : IStatusProvider
{
    public const int DefaultFailuresBeforeDown = 3;
    private readonly ConcurrentDictionary<string, CheckState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;

    protected CheckProvider(TimeProvider? clock) => _clock = clock ?? TimeProvider.System;

    public abstract string Type { get; }
    protected abstract int DefaultSlowMs { get; }

    /// <summary>One probe: success, how long it took, and what went wrong if it failed.</summary>
    protected abstract Task<(bool Up, TimeSpan Elapsed, string? Problem)> ProbeAsync(SourceConfig source, IHttpFetcher http, CancellationToken ct);

    private sealed class CheckState { public int Fails, Slows; public DateTimeOffset? DownSince, SlowSince; }

    public async Task<SourceResult> FetchAsync(SourceConfig source, IHttpFetcher http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.Url)) throw new ParseException("No address to check");
        var (up, elapsed, problem) = await ProbeAsync(source, http, ct).ConfigureAwait(false);
        return Evaluate(source, up, elapsed, problem);
    }

    internal SourceResult Evaluate(SourceConfig s, bool up, TimeSpan elapsed, string? problem)
    {
        var now = _clock.GetUtcNow();
        var threshold = Math.Clamp(int.TryParse(s.GetOption("failuresBeforeDown"), out var t) ? t : DefaultFailuresBeforeDown, 1, 20);
        var slowMs = int.TryParse(s.GetOption("slowMs"), out var ms) && ms > 0 ? ms : DefaultSlowMs;
        var slow = up && elapsed.TotalMilliseconds > slowMs;
        var label = string.IsNullOrWhiteSpace(s.Label) ? s.Url : s.Label;

        var st = _state.GetOrAdd($"{Type}|{s.Url}", _ => new CheckState());
        lock (st)
        {
            st.Fails = up ? 0 : st.Fails + 1;
            st.Slows = slow ? st.Slows + 1 : 0;
            if (st.Fails == 1) st.DownSince = now;
            if (st.Slows == 1) st.SlowSince = now;

            var reading = up ? $"{Ms(elapsed)}" : problem ?? "No response";
            if (st.Fails >= threshold)
            {
                var incident = new Incident
                {
                    Title = $"{label} is not responding",
                    Summary = $"{problem ?? "No response"} — {st.Fails} failed checks in a row.",
                    Url = s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s.Url : null,
                    Status = ServiceStatus.MajorOutage, Stage = IncidentStage.Investigating,
                    Started = st.DownSince, LastUpdated = now, AffectedComponents = [label],
                };
                return SourceResult.Of(s.Label, [incident], [new ComponentStatus(label, ServiceStatus.MajorOutage)], ServiceStatus.MajorOutage, now)
                    with { Note = $"Down · {reading}" };
            }
            if (st.Slows >= threshold)
            {
                var incident = new Incident
                {
                    Title = $"{label} is responding slowly",
                    Summary = $"Responded in {Ms(elapsed)} (limit {slowMs.ToString(CultureInfo.InvariantCulture)} ms) — {st.Slows} slow checks in a row.",
                    Url = s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s.Url : null,
                    Status = ServiceStatus.Degraded, Stage = IncidentStage.Investigating,
                    Started = st.SlowSince, LastUpdated = now, AffectedComponents = [label],
                };
                return SourceResult.Of(s.Label, [incident], [new ComponentStatus(label, ServiceStatus.Degraded)], ServiceStatus.Degraded, now)
                    with { Note = $"Slow · {reading}" };
            }
            // Below the threshold: still up as far as the board is concerned, but say a check failed.
            var note = !up ? $"Check failed ({st.Fails} of {threshold}) · {reading}"
                : slow ? $"Up · {reading} (slow {st.Slows} of {threshold})"
                : $"Up · {reading}";
            return SourceResult.Of(s.Label, [], [new ComponentStatus(label, ServiceStatus.Operational)], ServiceStatus.Operational, now) with { Note = note };
        }
    }

    private static string Ms(TimeSpan t) => t.TotalMilliseconds >= 1000
        ? (t.TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : Math.Max(1, (int)Math.Round(t.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture) + " ms";

    protected static TimeSpan Timeout(SourceConfig s) =>
        TimeSpan.FromSeconds(Math.Clamp(int.TryParse(s.GetOption("timeoutSeconds"), out var t) ? t : 10, 1, 60));
}

/// <summary>A website answers with an expected HTTP status (default 200–399; redirects are followed).</summary>
public sealed class HttpCheckProvider(TimeProvider? clock = null) : CheckProvider(clock)
{
    public override string Type => "http";
    protected override int DefaultSlowMs => 2000;

    protected override async Task<(bool, TimeSpan, string?)> ProbeAsync(SourceConfig s, IHttpFetcher http, CancellationToken ct)
    {
        var expected = ParseExpected(s.GetOption("expect"));
        var sw = Stopwatch.StartNew();
        int code;
        string? problem = null;
        try
        {
            await http.GetAsync(s.Url, ct, new FetchOptions
            {
                Attempts = 1, Timeout = Timeout(s), MaxBytes = 5 * 1024 * 1024, AllowInvalidCertificate = s.GetFlag("allowInvalidCertificate"),
            }).ConfigureAwait(false);
            code = 200;
        }
        catch (FetchException ex) when (ex.StatusCode is { } c) { code = c; problem = ex.Message; }
        catch (FetchException ex) { return (false, sw.Elapsed, ex.Message); }
        sw.Stop();
        var ok = expected(code);
        return (ok, sw.Elapsed, ok ? null : problem ?? $"Unexpected HTTP {code}");
    }

    /// <summary>"200-399" (default), "200,301", "401": which status codes mean "up". A successful response counts as 200.</summary>
    public static Func<int, bool> ParseExpected(string? spec)
    {
        var ranges = new List<(int From, int To)>();
        foreach (var part in (spec ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (int.TryParse(bits[0], out var a) && (bits.Length == 1 || int.TryParse(bits[1], out _)))
                ranges.Add((a, bits.Length == 2 ? int.Parse(bits[1], CultureInfo.InvariantCulture) : a));
        }
        if (ranges.Count == 0) ranges.Add((200, 399));
        return code => ranges.Any(r => code >= Math.Min(r.From, r.To) && code <= Math.Max(r.From, r.To));
    }
}

/// <summary>Pings a host. The probe is injectable so tests needn't touch the network.</summary>
public sealed class PingCheckProvider(TimeProvider? clock = null, Func<string, TimeSpan, CancellationToken, Task<(bool, TimeSpan, string?)>>? pinger = null)
    : CheckProvider(clock)
{
    public override string Type => "ping";
    protected override int DefaultSlowMs => 300;

    protected override Task<(bool, TimeSpan, string?)> ProbeAsync(SourceConfig s, IHttpFetcher http, CancellationToken ct) =>
        (pinger ?? PingAsync)(HostOf(s.Url), Timeout(s), ct);

    /// <summary>A bare host from "host", "host:port" or a pasted URL.</summary>
    public static string HostOf(string address)
    {
        var a = address.Trim();
        if (Uri.TryCreate(a, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)) return u.Host;
        var slash = a.IndexOf('/');
        if (slash >= 0) a = a[..slash];
        // "host:port" (but not a bare IPv6 address)
        if (a.Count(c => c == ':') == 1) a = a[..a.IndexOf(':')];
        return a.Trim('[', ']');
    }

    private static async Task<(bool, TimeSpan, string?)> PingAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeout, cancellationToken: ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? (true, TimeSpan.FromMilliseconds(Math.Max(1, reply.RoundtripTime)), null)
                : (false, timeout, reply.Status switch
                {
                    IPStatus.TimedOut => $"No reply within {timeout.TotalSeconds:0}s",
                    IPStatus.DestinationHostUnreachable => "Host unreachable",
                    IPStatus.DestinationNetworkUnreachable => "Network unreachable",
                    _ => $"Ping failed ({reply.Status})",
                });
        }
        catch (PingException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            return (false, TimeSpan.Zero, "Couldn't resolve host");
        }
        catch (PingException ex) { return (false, TimeSpan.Zero, ex.Message); }
        catch (ArgumentException) { return (false, TimeSpan.Zero, "Not a valid host name"); }
    }
}
