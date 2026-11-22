using System.Text.Json;

namespace StatusBoard.Core;

/// <summary>
/// Salesforce Trust (status.salesforce.com). Two shapes:
/// <list type="bullet">
/// <item>https://api.status.salesforce.com/v1/incidents/active — incidents per instance (EU45, NA224…).</item>
/// <item>https://api.status.salesforce.com/v1/products/{key} — one product (Tableau, MuleSoft…): each instance (pod) is a component.
/// One pod in trouble is a partial outage of the product; every pod down is a major outage.</item>
/// </list>
/// Option <c>instances</c>: comma-separated keys to watch; blank = all (noisy: Salesforce runs hundreds).
/// </summary>
public sealed class SalesforceProvider : SimpleProvider
{
    public const string ActiveIncidentsUrl = "https://api.status.salesforce.com/v1/incidents/active";
    public const string ProductApiBase = "https://api.status.salesforce.com/v1/products/";
    public const string TrustUrl = "https://status.salesforce.com/";

    public override string Type => "salesforce";
    protected override string UrlFor(SourceConfig s) => string.IsNullOrWhiteSpace(s.Url) ? ActiveIncidentsUrl : s.Url;
    protected override SourceResult Read(string text, SourceConfig s) => ProductKey(UrlFor(s)) is not null
        ? ParseProduct(text, s.Label, ParseInstances(s.GetOption("instances")))
        : Parse(text, s.Label, ParseInstances(s.GetOption("instances")));

    /// <summary>Null = all instances.</summary>
    public static HashSet<string>? ParseInstances(string? option)
    {
        var set = (option ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()).ToHashSet();
        return set.Count == 0 ? null : set;
    }

    /// <summary>"Tableau" for …/v1/products/Tableau or the human page status.salesforce.com/products/Tableau.</summary>
    public static string? ProductKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Host.EndsWith("status.salesforce.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals("products", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? Uri.UnescapeDataString(parts[i + 1]) : null;
    }

    public static SourceResult Parse(string json, string label, ISet<string>? instances)
    {
        using var doc = Json.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ParseException("Expected a JSON array of incidents");

        var incidents = new List<Incident>();
        var components = new Dictionary<string, ServiceStatus>();
        foreach (var i in doc.RootElement.EnumerateArray())
        {
            if (string.Equals(i.Str("status"), "Resolved", StringComparison.OrdinalIgnoreCase)) continue;
            var keys = i.Names("instanceKeys");
            var affectsAll = i.Bool("affectsAll");
            if (instances is not null && !affectsAll && !keys.Any(k => instances.Contains(k.ToUpperInvariant()))) continue;

            // Status from impacts that haven't ended; fall back to the incident type.
            var impacts = i.Arr("IncidentImpacts").ToList();
            var open = impacts.Where(x => x.Str("endTime") is null).ToList();
            var status = (open.Count > 0 ? open : impacts).Select(MapImpact).Worst(ServiceStatus.Unknown);
            if (status is ServiceStatus.Unknown or ServiceStatus.Operational)
                status = (i.Str("type") ?? "").Contains("Disruption", StringComparison.OrdinalIgnoreCase) ? ServiceStatus.PartialOutage : ServiceStatus.Degraded;

            List<string> shown = affectsAll ? ["All instances"] : instances is null ? keys : keys.Where(k => instances.Contains(k.ToUpperInvariant())).ToList();
            foreach (var key in shown) components.Bump(key, status);
            var services = i.Names("serviceKeys").Select(k => k == "coreService" ? "Core service" : k).ToList();
            var latest = i.Latest("IncidentEvents", "createdAt");

            incidents.Add(new Incident
            {
                Title = $"{i.Str("type") ?? "Incident"} on {Join(shown)}" + (services.Count > 0 ? $" ({string.Join(", ", services)})" : ""),
                Summary = latest.Has() ? TextUtil.Clean(latest.Str("message")) : null,
                Url = i.Str("id") is { } id ? $"{TrustUrl}incidents/{id}" : TrustUrl,
                Status = status,
                Stage = latest.Has() ? MapEvent(latest.Str("type")) : IncidentStage.Investigating,
                Started = impacts.Select(x => x.Date("startTime")).Where(d => d is not null).Min() ?? i.Date("createdAt"),
                LastUpdated = i.Date("updatedAt"),
                AffectedComponents = shown,
            });
        }
        return SourceResult.Of(label, incidents, components.ToComponents());
    }

    public static SourceResult ParseProduct(string json, string label, ISet<string>? instances)
    {
        using var doc = Json.Parse(json);
        var root = doc.RootElement;
        if (!root.Has() || !root.TryGetProperty("Instances", out var list) || list.ValueKind != JsonValueKind.Array) throw new ParseException("Not a Salesforce Trust product");

        var pods = list.EnumerateArray()
            .Where(p => p.Has() && !(p.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.False))
            .Where(p => instances is null || instances.Contains((p.Str("key") ?? "").ToUpperInvariant())).ToList();
        var components = pods.Select(p => new ComponentStatus(p.Str("key") + (p.Str("location") is { Length: > 0 } loc ? $" ({loc})" : ""), MapInstance(p.Str("status")))).ToList();
        var status = components.Count > 0 && components.All(c => c.Status == ServiceStatus.PartialOutage) ? ServiceStatus.MajorOutage : components.Select(c => c.Status).Worst();

        // Incidents hang off the pods (the same one under several): reuse the incident parser on the distinct set.
        var seen = new HashSet<string>();
        var raw = pods.SelectMany(p => p.Arr("Incidents")).Where(i => seen.Add(i.Str("id") ?? i.GetRawText())).Select(i => i.GetRawText());
        var keys = pods.Select(p => (p.Str("key") ?? "").ToUpperInvariant()).Where(k => k.Length > 0).ToHashSet();
        var incidents = Parse("[" + string.Join(",", raw) + "]", label, keys).Incidents;
        return SourceResult.Of(label, incidents, components, status.Worst(incidents.Select(x => x.Status).Worst()));
    }

    /// <summary>Latest event type: investigatingCauseOfIssue, rootCause…, workaround/fix…, update, impactTypeCorrection…</summary>
    internal static IncidentStage MapEvent(string? type)
    {
        var t = (type ?? "").ToLowerInvariant();
        return t.Contains("investigat") ? IncidentStage.Investigating
            : t.Contains("rootcause") || t.Contains("identif") || t.Contains("cause") ? IncidentStage.Identified
            : t.Contains("monitor") || t.Contains("fix") || t.Contains("workaround") || t.Contains("rollback") || t.Contains("mitigat") ? IncidentStage.Monitoring
            : t.Length == 0 ? IncidentStage.None
            : IncidentStage.Update;
    }

    private static ServiceStatus MapImpact(JsonElement impact)
    {
        var major = string.Equals(impact.Str("severity"), "major", StringComparison.OrdinalIgnoreCase);
        return (impact.Str("type") ?? "").Contains("disruption", StringComparison.OrdinalIgnoreCase)
            ? major ? ServiceStatus.MajorOutage : ServiceStatus.PartialOutage
            : major ? ServiceStatus.PartialOutage : ServiceStatus.Degraded; // performanceDegradation, etc.
    }

    /// <summary>Pod status: OK, MAJOR_/MINOR_INCIDENT_CORE|NONCORE, MAINTENANCE_…, INFORMATIONAL_….</summary>
    private static ServiceStatus MapInstance(string? s)
    {
        var v = (s ?? "OK").ToUpperInvariant();
        return v == "OK" || v.StartsWith("INFORMATIONAL") ? ServiceStatus.Operational
            : v.StartsWith("MAJOR") ? ServiceStatus.PartialOutage
            : v.StartsWith("MAINTENANCE") ? ServiceStatus.Maintenance
            : ServiceStatus.Degraded; // MINOR_INCIDENT_* and anything new
    }

    private static string Join(List<string> keys) => keys.Count switch
    {
        0 => "Salesforce",
        <= 4 => string.Join(", ", keys),
        _ => $"{string.Join(", ", keys.Take(3))} and {keys.Count - 3} more",
    };
}
