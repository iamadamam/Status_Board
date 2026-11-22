using System.Text.Json;
using System.Text.RegularExpressions;

namespace StatusBoard.Core;

/// <summary>
/// Status.io-hosted pages (GitLab, Mimecast, ConnectWise…) via https://api.status.io/1.0/status/{page id}; Url = that, or just the id.
/// Codes: 100 operational, 200 maintenance, 300 degraded, 400 partial disruption, 500 disruption, 600 security event.
/// </summary>
public sealed partial class StatusIoProvider : SimpleProvider
{
    public const string ApiBase = "https://api.status.io/1.0/status/";
    public override string Type => "statusio";
    protected override string UrlFor(SourceConfig s) => ApiUrl(s.Url);
    protected override SourceResult Read(string text, SourceConfig s) => Parse(text, s.Label, s.GetOption("link"));

    public static string ApiUrl(string urlOrId) => PageId().IsMatch(urlOrId.Trim()) ? ApiBase + urlOrId.Trim() : urlOrId.Trim();

    public static SourceResult Parse(string json, string label, string? pageUrl = null)
    {
        using var doc = Json.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("status_overall", out var overall))
            throw new ParseException("Not a Status.io status response");

        var components = result.Arr("status").SelectMany(c =>
        {
            var containers = c.Arr("containers").ToList();
            var name = c.Str("name") ?? "(unnamed)";
            return containers.Count <= 1
                ? [new ComponentStatus(name, Map(c.Str("status_code")))]
                : containers.Select(k => new ComponentStatus($"{name} / {k.Str("name")}", Map(k.Str("status_code"))));
        }).ToList();

        var incidents = result.Arr("incidents").Select(i =>
        {
            var latest = i.Latest("messages", "datetime");
            var status = latest.Has() ? Map(latest.Str("status")) : ServiceStatus.Degraded;
            // An open incident whose latest update says "operational" is being monitored: still worth showing.
            return Item(i, latest, pageUrl, status is ServiceStatus.Operational or ServiceStatus.Unknown or ServiceStatus.Maintenance ? ServiceStatus.Degraded : status,
                "Incident", i.Date("datetime_open"), latest.Has() ? MapState(latest.Str("state")) : IncidentStage.Investigating);
        }).ToList();
        if (result.TryGetProperty("maintenance", out var maint))
            incidents.AddRange(maint.Arr("active").Select(m =>
                Item(m, m.Latest("messages", "datetime"), pageUrl, ServiceStatus.Maintenance, "Maintenance", m.Date("datetime_planned_start") ?? m.Date("datetime_open"),
                    IncidentStage.InProgress)));

        var worst = Map(overall.Str("status_code")).Worst(components.Select(c => c.Status).Worst()).Worst(incidents.Select(i => i.Status).Worst());
        return SourceResult.Of(label, incidents, components, worst);
    }

    private static Incident Item(JsonElement e, JsonElement latest, string? pageUrl, ServiceStatus status, string fallback, DateTimeOffset? started, IncidentStage stage) => new()
    {
        Title = e.Str("name") ?? fallback,
        Stage = stage,
        Summary = latest.Has() ? TextUtil.Clean(latest.Str("details")) : null,
        Url = pageUrl,
        Status = status,
        Started = started,
        LastUpdated = latest.Has() ? latest.Date("datetime") : null,
        AffectedComponents = e.Names("components_affected", distinct: true),
    };

    internal static ServiceStatus Map(string? code) => code switch
    {
        "100" => ServiceStatus.Operational, "200" => ServiceStatus.Maintenance, "300" => ServiceStatus.Degraded,
        "400" or "600" => ServiceStatus.PartialOutage, "500" => ServiceStatus.MajorOutage, _ => ServiceStatus.Unknown,
    };

    /// <summary>Incident message state: 100 investigating, 200 identified, 300 monitoring; other codes are plain updates.</summary>
    internal static IncidentStage MapState(string? code) => code switch
    {
        "100" => IncidentStage.Investigating, "200" => IncidentStage.Identified, "300" => IncidentStage.Monitoring,
        null or "" => IncidentStage.None, _ => IncidentStage.Update,
    };

    [GeneratedRegex("^[0-9a-f]{24}$")] internal static partial Regex PageId();
}
