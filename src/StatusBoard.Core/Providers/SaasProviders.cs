using System.Text.Json;

namespace StatusBoard.Core;

/// <summary>Slack: https://slack-status.com/api/v2.0.0/current → {status: ok|active, active_incidents: [{title, type, status, url, services, notes}]}.</summary>
public sealed class SlackStatusProvider : SimpleProvider
{
    public const string CurrentUrl = "https://slack-status.com/api/v2.0.0/current";
    public override string Type => "slack";
    protected override string UrlFor(SourceConfig s) => string.IsNullOrWhiteSpace(s.Url) ? CurrentUrl : s.Url;
    protected override SourceResult Read(string text, SourceConfig s) => Parse(text, s.Label);

    public static SourceResult Parse(string json, string label)
    {
        using var doc = Json.Parse(json);
        var root = doc.RootElement;
        if (root.Str("status") is not { } overall) throw new ParseException("Not a Slack status response");

        var incidents = new List<Incident>();
        var components = new Dictionary<string, ServiceStatus>();
        foreach (var i in root.Arr("active_incidents"))
        {
            var state = i.Str("status") ?? "";
            if (state.Equals("resolved", StringComparison.OrdinalIgnoreCase) || state.Equals("completed", StringComparison.OrdinalIgnoreCase)) continue;
            var status = (i.Str("type") ?? "incident").ToLowerInvariant() == "outage" ? ServiceStatus.MajorOutage
                : state.Equals("scheduled", StringComparison.OrdinalIgnoreCase) ? ServiceStatus.Maintenance
                : ServiceStatus.Degraded; // "incident" and "notice"
            var services = i.Names("services");
            foreach (var name in services) components.Bump(name, status);
            var note = i.Latest("notes", "date_created");
            var kind = (i.Str("type") ?? "incident").ToLowerInvariant();
            incidents.Add(new Incident
            {
                Title = i.Str("title") ?? "Slack incident",
                // "active" for an incident means it's open (no finer stage given); for maintenance it means under way.
                Stage = state.Equals("scheduled", StringComparison.OrdinalIgnoreCase) ? IncidentStage.InProgress
                    : kind == "notice" ? IncidentStage.Notice
                    : IncidentStages.FromWord(state) is var st && st != IncidentStage.InProgress ? st : IncidentStage.None,
                Summary = note.Has() ? TextUtil.Clean(note.Str("body")) : null,
                Url = i.Str("url"),
                Status = status,
                Started = i.Date("date_created"),
                LastUpdated = i.Date("date_updated"),
                AffectedComponents = services,
            });
        }
        // "active" without details still means Slack considers something wrong.
        var worst = incidents.Count == 0 && !overall.Equals("ok", StringComparison.OrdinalIgnoreCase) ? ServiceStatus.Degraded : incidents.Select(x => x.Status).Worst();
        return SourceResult.Of(label, incidents, components.ToComponents(), worst);
    }
}

/// <summary>
/// Apple System Status (data file behind https://www.apple.com/support/systemstatus/, possibly JSONP-wrapped). Each service lists
/// events (Outage / Issue / Performance / Maintenance) with an eventStatus; resolved, completed, upcoming and ended ones are history.
/// </summary>
public sealed class AppleStatusProvider : SimpleProvider
{
    public const string PageUrl = "https://www.apple.com/support/systemstatus/";
    public override string Type => "apple";
    protected override SourceResult Read(string text, SourceConfig s) => Parse(text, s.Label, DateTimeOffset.Now, s.GetOption("link") ?? PageUrl);

    public static SourceResult Parse(string text, string label, DateTimeOffset now, string link = PageUrl)
    {
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new ParseException("Not an Apple system status file");
        using var doc = Json.Parse(text[start..(end + 1)]);
        if (!doc.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array)
            throw new ParseException("No services in Apple system status");

        var incidents = new List<Incident>();
        var components = new List<ComponentStatus>();
        foreach (var svc in services.EnumerateArray())
        {
            var name = svc.Str("serviceName") ?? "Apple service";
            var worst = ServiceStatus.Operational;
            foreach (var e in svc.Arr("events"))
            {
                var state = (e.Str("eventStatus") ?? "").ToLowerInvariant();
                DateTimeOffset? from = EpochMs(e, "epochStartDate"), to = EpochMs(e, "epochEndDate");
                if (state is "resolved" or "completed" or "cancelled" or "canceled" or "upcoming" || to <= now || from > now) continue;
                var status = (e.Str("statusType") ?? "").ToLowerInvariant() switch
                {
                    "outage" => ServiceStatus.PartialOutage, // one Apple service down, not Apple as a whole
                    "maintenance" => ServiceStatus.Maintenance,
                    _ => ServiceStatus.Degraded,             // Issue, Performance
                };
                worst = worst.Worst(status);
                var affected = e.TryGetProperty("affectedServices", out var a) && a.ValueKind == JsonValueKind.Array ? e.Names("affectedServices") : [name];
                incidents.Add(new Incident
                {
                    Title = $"{name}: {e.Str("statusType") ?? "Issue"}",
                    // "ongoing" is the only open state Apple uses: for maintenance that's "in progress"; for an issue it says nothing more.
                    Stage = status == ServiceStatus.Maintenance ? IncidentStage.InProgress
                        : IncidentStages.FromWord(state) is var st && st != IncidentStage.InProgress ? st : IncidentStage.None,
                    Summary = string.Join(" ", new[] { e.Str("message"), e.Str("usersAffected") }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    Url = link,
                    Status = status,
                    Started = from,
                    AffectedComponents = affected,
                });
            }
            components.Add(new ComponentStatus(name, worst));
        }
        return SourceResult.Of(label, incidents, components, checkedAt: now);
    }

    private static DateTimeOffset? EpochMs(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms) && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}

/// <summary>Stripe: https://status.stripe.com/current → {statuses: {api: up, webhooks: up, …}, largestatus, message}.</summary>
public sealed class StripeStatusProvider : SimpleProvider
{
    public const string CurrentUrl = "https://status.stripe.com/current", PageUrl = "https://status.stripe.com/";
    public override string Type => "stripe";
    protected override string UrlFor(SourceConfig s) => string.IsNullOrWhiteSpace(s.Url) ? CurrentUrl : s.Url;
    protected override SourceResult Read(string text, SourceConfig s) => Parse(text, s.Label);

    public static SourceResult Parse(string json, string label)
    {
        using var doc = Json.Parse(json);
        var root = doc.RootElement;
        if (!root.Has() || !root.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Object)
            throw new ParseException("Not a Stripe status response");
        var components = statuses.EnumerateObject().Select(p => new ComponentStatus(Title(p.Name), Map(p.Value.GetString(), whole: false))).ToList();
        var overall = Map(root.Str("largestatus"), whole: true).Worst(components.Select(c => c.Status).Worst());
        List<Incident> incidents = overall == ServiceStatus.Operational ? [] :
        [
            new()
            {
                Title = root.Str("message") ?? "Stripe is reporting issues", Url = PageUrl, Status = overall,
                AffectedComponents = components.Where(c => c.Status != ServiceStatus.Operational).Select(c => c.Name).ToList(),
            },
        ];
        return SourceResult.Of(label, incidents, components, overall);
    }

    private static ServiceStatus Map(string? s, bool whole) => s?.ToLowerInvariant() switch
    {
        null or "" or "up" => ServiceStatus.Operational,
        "down" => whole ? ServiceStatus.MajorOutage : ServiceStatus.PartialOutage,
        _ => ServiceStatus.Degraded, // degraded, delayed, anything new
    };

    private static string Title(string key) => key switch
    {
        "api" => "API", "stripejs" => "Stripe.js", "supportsite" => "Support site", _ => char.ToUpperInvariant(key[0]) + key[1..],
    };
}

/// <summary>PayPal: https://www.paypal-status.com/api/v1/components → {result: [{name, parentName, status: {production, sandbox}}]}; production only.</summary>
public sealed class PayPalStatusProvider : SimpleProvider
{
    public const string ComponentsUrl = "https://www.paypal-status.com/api/v1/components", PageUrl = "https://www.paypal-status.com/product/production";
    public override string Type => "paypal";
    protected override string UrlFor(SourceConfig s) => string.IsNullOrWhiteSpace(s.Url) ? ComponentsUrl : s.Url;
    protected override SourceResult Read(string text, SourceConfig s) => Parse(text, s.Label);

    public static SourceResult Parse(string json, string label)
    {
        using var doc = Json.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var list) || list.ValueKind != JsonValueKind.Array) throw new ParseException("Not a PayPal status response");
        var components = list.EnumerateArray().Select(c =>
        {
            var name = c.Str("displayName") ?? c.Str("name") ?? "Component";
            if (c.Str("parentName") is { Length: > 0 } parent) name = $"{parent} · {name}";
            return new ComponentStatus(name, Map(c.TryGetProperty("status", out var s) ? s.Str("production") : null));
        }).ToList();
        var affected = components.Where(c => c.Status != ServiceStatus.Operational).ToList();
        var overall = components.Select(c => c.Status).Worst();
        List<Incident> incidents = affected.Count == 0 ? [] :
        [
            new()
            {
                Title = affected.Count == 1 ? $"{affected[0].Name}: {affected[0].Status.ToDisplay()}" : $"{affected.Count} PayPal services affected",
                Url = PageUrl, Status = overall, AffectedComponents = affected.Select(c => c.Name).ToList(),
            },
        ];
        return SourceResult.Of(label, incidents, components, overall);
    }

    private static ServiceStatus Map(string? s)
    {
        var v = (s ?? "").ToLowerInvariant();
        return v.Length == 0 || v.Contains("operational") ? ServiceStatus.Operational
            : v.Contains("maintenance") ? ServiceStatus.Maintenance
            : v.Contains("major") ? ServiceStatus.MajorOutage
            : v.Contains("partial") || v.Contains("outage") || v.Contains("down") ? ServiceStatus.PartialOutage
            : ServiceStatus.Degraded;
    }
}
