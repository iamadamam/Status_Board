using System.Text.Json;

namespace StatusBoard.Core;

/// <summary>
/// Atlassian Statuspage (and compatible pages such as incident.io): Url = page root; reads /api/v2/summary.json, which has
/// components, unresolved incidents and scheduled maintenances in one request.
/// </summary>
public sealed class StatuspageProvider : SimpleProvider
{
    public override string Type => "statuspage";

    private static bool IsApi(string url) => url.TrimEnd('/').EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    protected override string UrlFor(SourceConfig s) => IsApi(s.Url) ? s.Url.TrimEnd('/') : s.Url.TrimEnd('/') + "/api/v2/summary.json";
    protected override SourceResult Read(string text, SourceConfig s) => Parse(text, s.Label, IsApi(s.Url) ? null : s.Url.TrimEnd('/'));

    public static SourceResult Parse(string json, string label, string? pageUrl = null)
    {
        using var doc = Json.Parse(json);
        var root = doc.RootElement;
        if (!root.Has() || !root.TryGetProperty("status", out var status)) throw new ParseException("Not a Statuspage summary (no 'status')");
        pageUrl ??= root.TryGetProperty("page", out var page) ? page.Str("url") : null;

        // Components: skip group containers, prefix children with their group's name.
        var groups = root.Arr("components").Where(c => c.Bool("group")).ToDictionary(c => c.Str("id") ?? "", c => c.Str("name") ?? "");
        var components = root.Arr("components").Where(c => !c.Bool("group")).Select(c =>
        {
            var name = c.Str("name") ?? "(unnamed)";
            if (c.Str("group_id") is { } gid && groups.TryGetValue(gid, out var g) && g.Length > 0) name = $"{g} / {name}";
            return new ComponentStatus(name, MapComponent(c.Str("status")));
        }).ToList();

        var incidents = new List<Incident>();
        foreach (var i in root.Arr("incidents").Where(i => i.Str("status") is not ("resolved" or "postmortem")))
        {
            var affected = i.Names("components", distinct: true); // same-named components in different groups ("Australia" ×5)
            // Pages often say impact "none" while marking components down: trust the components then.
            var fromComponents = components.Where(c => affected.Contains(c.Name) || affected.Any(a => c.Name.EndsWith("/ " + a))).Select(c => c.Status).Worst();
            var st = MapImpact(i.Str("impact")).Worst(fromComponents);
            incidents.Add(Item(i, pageUrl, st == ServiceStatus.Operational ? ServiceStatus.Degraded : st, i.Date("started_at") ?? i.Date("created_at"), affected,
                IncidentStages.FromWord(i.Str("status"))));
        }
        // Only maintenance happening now counts; upcoming windows are ignored.
        foreach (var m in root.Arr("scheduled_maintenances").Where(m => m.Str("status") is "in_progress" or "verifying"))
            incidents.Add(Item(m, pageUrl, ServiceStatus.Maintenance, m.Date("started_at") ?? m.Date("scheduled_for"), m.Names("components", distinct: true),
                m.Str("status") == "verifying" ? IncidentStage.Verifying : IncidentStage.InProgress));

        var overall = MapIndicator(status.Str("indicator")).Worst(components.Select(c => c.Status).Worst()).Worst(incidents.Select(i => i.Status).Worst());
        return SourceResult.Of(label, incidents, components, overall);
    }

    private static Incident Item(JsonElement e, string? pageUrl, ServiceStatus status, DateTimeOffset? started, List<string> affected, IncidentStage stage) => new()
    {
        Title = e.Str("name") ?? (status == ServiceStatus.Maintenance ? "Maintenance" : "Incident"),
        Stage = stage,
        Summary = TextUtil.Clean(e.Latest("incident_updates", "display_at", "created_at") is { ValueKind: JsonValueKind.Object } u ? u.Str("body") : null),
        Url = e.Str("shortlink") ?? pageUrl,
        Status = status,
        Started = started,
        LastUpdated = e.Date("updated_at"),
        AffectedComponents = affected,
    };

    internal static ServiceStatus MapIndicator(string? s) => s switch
    {
        "none" => ServiceStatus.Operational, "minor" => ServiceStatus.Degraded, "major" => ServiceStatus.PartialOutage,
        "critical" => ServiceStatus.MajorOutage, "maintenance" => ServiceStatus.Maintenance, _ => ServiceStatus.Unknown,
    };

    internal static ServiceStatus MapComponent(string? s) => s switch
    {
        "operational" => ServiceStatus.Operational, "degraded_performance" => ServiceStatus.Degraded, "partial_outage" => ServiceStatus.PartialOutage,
        "major_outage" => ServiceStatus.MajorOutage, "under_maintenance" => ServiceStatus.Maintenance, _ => ServiceStatus.Unknown,
    };

    internal static ServiceStatus MapImpact(string? s) => s switch
    {
        "minor" => ServiceStatus.Degraded, "major" => ServiceStatus.PartialOutage, "critical" => ServiceStatus.MajorOutage,
        "maintenance" => ServiceStatus.Maintenance, _ => ServiceStatus.Operational, // "none": decided by components
    };
}
