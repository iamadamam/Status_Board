using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace StatusBoard.Core;

/// <summary>
/// A LibreNMS instance via its REST API. Url = base URL. Options: <c>token</c> (DPAPI-encrypted when entered in the app),
/// <c>allowInvalidCertificate</c>, <c>includeAcknowledged</c>. Reads active alerts, down devices and (optionally) rule names.
/// Critical alerts and down devices are partial outages; warnings and acknowledged alerts are degraded.
/// </summary>
public sealed class LibreNmsProvider : IStatusProvider
{
    // Hostnames by device id, so alerts are labelled without fetching every device each refresh.
    private readonly ConcurrentDictionary<string, string> _hostnames = new();

    public string Type => "librenms";

    public static FetchOptions OptionsFor(SourceConfig source)
    {
        var token = Secrets.Unprotect(source.GetOption("token"));
        if (string.IsNullOrWhiteSpace(token)) throw new FetchException("No API token configured");
        // Newer LibreNMS uses Bearer, older X-Auth-Token; sending both works with either.
        return new FetchOptions
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token, ["X-Auth-Token"] = token },
            AllowInvalidCertificate = source.GetFlag("allowInvalidCertificate"),
        };
    }

    public async Task<SourceResult> FetchAsync(SourceConfig source, IHttpFetcher http, CancellationToken ct)
    {
        FetchOptions opts;
        try { opts = OptionsFor(source); }
        catch (InvalidOperationException ex) { throw new FetchException(ex.Message); }

        var root = source.Url.TrimEnd('/');
        var api = root + "/api/v0";
        var includeAck = source.GetFlag("includeAcknowledged");
        async Task<string?> TryGet(string url) { try { return await http.GetTextAsync(url, ct, opts).ConfigureAwait(false); } catch (FetchException) { return null; } }

        var alertsTask = http.GetTextAsync(api + "/alerts?state=1", ct, opts);
        var ackTask = includeAck ? http.GetTextAsync(api + "/alerts?state=2", ct, opts) : Task.FromResult("");
        var downTask = http.GetTextAsync(api + "/devices?type=down", ct, opts);
        var rulesTask = TryGet(api + "/rules"); // optional: nicer titles only
        var (alerts, acked, down, rules) = (await alertsTask.ConfigureAwait(false), await ackTask.ConfigureAwait(false),
            await downTask.ConfigureAwait(false), await rulesTask.ConfigureAwait(false));

        // Learn hostnames from the down list, then look up (a bounded number of) unknown alerting devices.
        foreach (var (id, name) in DeviceNames(down)) _hostnames[root + "#" + id] = name;
        var missing = AlertDevicesWithoutHostname(alerts).Concat(AlertDevicesWithoutHostname(acked)).Distinct()
            .Where(id => !_hostnames.ContainsKey(root + "#" + id)).Take(25);
        await Task.WhenAll(missing.Select(async id =>
        {
            foreach (var (did, name) in DeviceNames(await TryGet($"{api}/devices/{Uri.EscapeDataString(id)}").ConfigureAwait(false)))
                _hostnames[root + "#" + did] = name;
        })).ConfigureAwait(false);

        var names = _hostnames.Where(kv => kv.Key.StartsWith(root + "#", StringComparison.Ordinal)).ToDictionary(kv => kv.Key[(root.Length + 1)..], kv => kv.Value);
        return Parse(alerts, down, rules, names, root, source.Label, includeAck ? acked : null);
    }

    public static SourceResult Parse(string alertsJson, string downDevicesJson, string? rulesJson,
        IReadOnlyDictionary<string, string> hostnames, string baseUrl, string label, string? acknowledgedJson = null)
    {
        var rules = new Dictionary<string, (string Name, string? Severity)>();
        using (var r = Json.TryParse(rulesJson))
            if (r is not null)
                foreach (var rule in r.RootElement.Arr("rules"))
                    if (rule.Str("id") is { } id) rules[id] = (rule.Str("name") ?? $"Rule {id}", rule.Str("severity"));

        var incidents = new List<Incident>();
        var components = new Dictionary<string, ServiceStatus>();

        using (var d = ParseApi(downDevicesJson, "devices"))
            foreach (var dev in d.RootElement.Arr("devices"))
            {
                var id = dev.Str("device_id") ?? "?";
                var name = DisplayName(dev) ?? hostnames.GetValueOrDefault(id) ?? $"Device {id}";
                var reason = dev.Str("status_reason");
                components.Bump(name, ServiceStatus.PartialOutage);
                incidents.Add(new Incident
                {
                    Title = $"{name} is down",
                    Summary = string.IsNullOrWhiteSpace(reason) ? null : $"Reason: {reason}",
                    Url = $"{baseUrl}/device/device={id}/",
                    Status = ServiceStatus.PartialOutage,
                    LastUpdated = LocalTime(dev.Str("last_polled")),
                    AffectedComponents = [name],
                });
            }

        foreach (var (json, acked) in new[] { (alertsJson, false), (acknowledgedJson, true) })
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            using var a = ParseApi(json, "alerts");
            foreach (var alert in a.RootElement.Arr("alerts"))
            {
                var deviceId = alert.Str("device_id") ?? "?";
                var host = alert.Str("hostname") is { Length: > 0 } h ? h : hostnames.GetValueOrDefault(deviceId) ?? $"Device {deviceId}";
                var ruleId = alert.Str("rule_id");
                var rule = ruleId is not null && rules.TryGetValue(ruleId, out var ri) ? ri : default;
                var ruleName = alert.Str("name") ?? rule.Name ?? (ruleId is null ? "Alert" : $"Rule {ruleId}");
                var severity = (alert.Str("severity") ?? rule.Severity ?? "critical").ToLowerInvariant();
                var status = !acked && severity == "critical" ? ServiceStatus.PartialOutage : ServiceStatus.Degraded;
                components.Bump(host, status);
                incidents.Add(new Incident
                {
                    Title = $"{ruleName} – {host}" + (acked ? " (acknowledged)" : ""),
                    Summary = TextUtil.Clean(alert.Str("note")) ?? $"Severity: {severity}",
                    Url = $"{baseUrl}/device/device={deviceId}/tab=alerts/",
                    Status = status,
                    Started = LocalTime(alert.Str("timestamp")),
                    AffectedComponents = [host],
                });
            }
        }
        return SourceResult.Of(label, incidents, components.ToComponents());
    }

    private static JsonDocument ParseApi(string json, string listName)
    {
        var doc = Json.Parse(json, "Not valid JSON (is the URL the LibreNMS base address?)");
        var root = doc.RootElement;
        var st = root.Str("status");
        // Some versions answer "error" + "no X found" for an empty list: treat that as empty.
        var error = st is null ? "Not a LibreNMS API response"
            : st != "ok" && !(root.Str("message") ?? "").Contains("no ", StringComparison.OrdinalIgnoreCase) && !root.TryGetProperty(listName, out _)
                ? "LibreNMS error: " + (root.Str("message") ?? st) : null;
        if (error is null) return doc;
        doc.Dispose();
        throw new ParseException(error);
    }

    private static string? DisplayName(JsonElement dev) =>
        new[] { dev.Str("display"), dev.Str("sysName"), dev.Str("hostname") }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    private static List<(string Id, string Name)> DeviceNames(string? json)
    {
        using var doc = Json.TryParse(json);
        return doc is null ? [] : [.. doc.RootElement.Arr("devices").Where(d => d.Str("device_id") is not null && DisplayName(d) is not null)
            .Select(d => (d.Str("device_id")!, DisplayName(d)!))];
    }

    private static List<string> AlertDevicesWithoutHostname(string? json)
    {
        using var doc = Json.TryParse(json);
        return doc is null ? [] : [.. doc.RootElement.Arr("alerts").Where(a => string.IsNullOrEmpty(a.Str("hostname"))).Select(a => a.Str("device_id")).OfType<string>()];
    }

    /// <summary>"yyyy-MM-dd HH:mm:ss" in the server's local time (assumed to match this PC's).</summary>
    private static DateTimeOffset? LocalTime(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? new DateTimeOffset(d) : null;
}
