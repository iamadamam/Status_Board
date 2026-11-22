using System.Text.Json;
using System.Text.RegularExpressions;

namespace StatusBoard.Core;

/// <summary>
/// Microsoft's status JSON behind https://status.cloud.microsoft/ (/api/posts/{azure|mac|ppac|m365Consumer}): undocumented, what the
/// page itself calls. One post, or an array of posts (one per service). Statuses: Available, Operational, Service degradation,
/// Service restored, Unknown.
/// </summary>
public sealed class MicrosoftStatusProvider : SimpleProvider
{
    public const string PageUrl = "https://status.cloud.microsoft/";
    public override string Type => "microsoft";
    protected override SourceResult Read(string text, SourceConfig s) => Parse(text, s.Label, s.GetOption("link") ?? PageUrl);

    public static SourceResult Parse(string json, string label, string link = PageUrl)
    {
        using var doc = Json.Parse(json);
        var root = doc.RootElement;
        var isList = root.ValueKind == JsonValueKind.Array;
        var posts = isList ? root.EnumerateArray().ToList() : root.Has() ? [root] : throw new ParseException("Unexpected JSON shape");
        if (posts.Any(p => p.Str("Status") is null)) throw new ParseException("Not a Microsoft status post (no 'Status')");

        var components = new List<ComponentStatus>();
        var incidents = new List<Incident>();
        foreach (var p in posts)
        {
            var name = p.Str("ServiceDisplayName") ?? label;
            var raw = p.Str("Status")!;
            var status = MapStatus(raw);
            if (isList) components.Add(new ComponentStatus(name, status));
            if (status == ServiceStatus.Operational) continue;
            incidents.Add(new Incident
            {
                Title = p.Str("Title") is { Length: > 0 } t && !string.IsNullOrWhiteSpace(t) ? t : $"{name}: {raw}",
                Summary = TextUtil.Clean(p.Str("Message")),
                Url = link,
                Status = status,
                Started = p.Date("AuthoredDateTime"),
                LastUpdated = p.Date("LastUpdatedTime"),
                AffectedComponents = isList ? [name] : [],
            });
        }
        return SourceResult.Of(label, incidents, components, posts.Select(p => MapStatus(p.Str("Status"))).Worst());
    }

    internal static ServiceStatus MapStatus(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "available" or "operational" or "service restored" => ServiceStatus.Operational,
        "service degradation" => ServiceStatus.Degraded,
        "unknown" or null or "" => ServiceStatus.Unknown,
        _ => ServiceStatus.Degraded, // other non-OK wording: show it rather than hide it
    };
}

/// <summary>
/// The AWS Health Dashboard's public JSON (https://health.aws.amazon.com/public/currentevents): UTF-16 BE with a BOM, open events
/// only. Option <c>regions</c>: comma-separated codes, "global" for events without a region, "*" for all.
/// Event status: 0 resolved, 1 informational/impact, 2 degradation, 3 disruption.
/// </summary>
public sealed partial class AwsHealthProvider : SimpleProvider
{
    public const string DashboardUrl = "https://health.aws.amazon.com/health/status";
    public override string Type => "aws-health";
    protected override SourceResult Read(string text, SourceConfig s) => Parse(text, s.Label, ParseRegions(s.GetOption("regions")));

    /// <summary>Null = all regions (missing or "*"); empty = none.</summary>
    public static HashSet<string>? ParseRegions(string? option) => option is null || option.Trim() == "*" ? null
        : option.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.ToLowerInvariant()).ToHashSet();

    public static SourceResult Parse(string json, string label, ISet<string>? regions)
    {
        using var doc = Json.Parse(json.TrimStart('﻿'));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ParseException("Expected a JSON array of events");

        var incidents = new List<Incident>();
        var components = new List<ComponentStatus>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var region = RegionOf(e);
            var status = MapStatus(e.Str("status"));
            if ((regions is not null && !regions.Contains(region)) || status == ServiceStatus.Operational) continue;

            var regionName = e.Str("region_name") ?? region;
            var log = e.Arr("event_log").OrderBy(x => x.Epoch("timestamp") ?? DateTimeOffset.MinValue).ToList();
            var latest = log.LastOrDefault();
            var affected = new List<string>();
            if (e.TryGetProperty("impacted_services", out var imp) && imp.ValueKind == JsonValueKind.Object)
                foreach (var svc in imp.EnumerateObject())
                {
                    var svcStatus = MapStatus(svc.Value.Str("current"));
                    if (svcStatus == ServiceStatus.Operational) continue;
                    var svcName = svc.Value.Str("service_name") ?? svc.Name;
                    affected.Add(svcName);
                    components.Add(new ComponentStatus($"{svcName} ({regionName})", svcStatus));
                }
            var service = e.Str("service_name") ?? e.Str("service") ?? "AWS";
            if (affected.Count == 0) components.Add(new ComponentStatus($"{service} ({regionName})", status));

            var summary = e.Str("summary");
            incidents.Add(new Incident
            {
                Title = $"{service} – {regionName}" + (string.IsNullOrWhiteSpace(summary) ? "" : $": {summary}"),
                Summary = TextUtil.Clean(latest.Has() ? latest.Str("message") : null),
                Url = DashboardUrl,
                Status = status,
                Started = e.Epoch("date") ?? (log.Count > 0 ? log[0].Epoch("timestamp") : null),
                LastUpdated = latest.Has() ? latest.Epoch("timestamp") : e.Epoch("date"),
                AffectedComponents = affected,
            });
        }
        return SourceResult.Of(label, incidents, components);
    }

    internal static ServiceStatus MapStatus(string? code) => code switch
    {
        "0" => ServiceStatus.Operational, "1" => ServiceStatus.Degraded, "2" => ServiceStatus.PartialOutage,
        "3" => ServiceStatus.MajorOutage, _ => ServiceStatus.Degraded,
    };

    /// <summary>Region from the event ARN (arn:aws:health:REGION::event/…), else the service key's suffix, else "global".</summary>
    internal static string RegionOf(JsonElement e)
    {
        if (e.Str("arn")?.Split(':') is { Length: > 3 } parts && RegionCode().IsMatch(parts[3])) return parts[3].ToLowerInvariant();
        var m = ServiceSuffix().Match(e.Str("service") ?? "");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "global";
    }

    [GeneratedRegex(@"^[a-z]{2}(-gov|-iso[a-z]?)?-[a-z]+-\d+$", RegexOptions.IgnoreCase)] private static partial Regex RegionCode();
    [GeneratedRegex(@"-([a-z]{2}(?:-gov|-iso[a-z]?)?-[a-z]+-\d+)$", RegexOptions.IgnoreCase)] private static partial Regex ServiceSuffix();
}

/// <summary>
/// Google Workspace and Google Cloud dashboards share <c>incidents.json</c> (the history). An incident without an end (or ending
/// later) is current. Option <c>link</c>: the dashboard base that incident URIs are relative to.
/// </summary>
public sealed partial class GoogleStatusProvider : SimpleProvider
{
    public override string Type => "google";
    protected override SourceResult Read(string text, SourceConfig s) =>
        Parse(text, s.Label, s.GetOption("link") ?? new Uri(new Uri(s.Url), ".").AbsoluteUri, DateTimeOffset.Now);

    public static SourceResult Parse(string json, string label, string link, DateTimeOffset now)
    {
        using var doc = Json.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ParseException("Expected a JSON array of incidents");
        var incidents = new List<Incident>();
        var components = new Dictionary<string, ServiceStatus>();
        foreach (var i in doc.RootElement.EnumerateArray())
        {
            if (i.Date("end") is { } end && end <= now || i.Date("begin") is { } begin && begin > now) continue;
            var latest = i.TryGetProperty("most_recent_update", out var u) ? u : default;
            if (string.Equals(latest.Str("status"), "AVAILABLE", StringComparison.OrdinalIgnoreCase)) continue;

            var status = Map(i.Str("status_impact"), i.Str("severity"));
            var products = i.Names("affected_products", "title");
            foreach (var p in products) components.Bump(p, status);
            incidents.Add(new Incident
            {
                Title = FirstLine(i.Str("external_desc")) ?? (products.Count > 0 ? string.Join(", ", products) : "Incident"),
                Summary = TextUtil.Clean(StripMarkdown(latest.Str("text"))),
                Url = i.Str("uri") is { } uri ? new Uri(new Uri(link), uri).AbsoluteUri : link,
                Status = status,
                // SERVICE_INFORMATION is Google's "known issue / advisory" level.
                Stage = string.Equals(i.Str("status_impact"), "SERVICE_INFORMATION", StringComparison.OrdinalIgnoreCase) ? IncidentStage.Notice : IncidentStage.None,
                Started = i.Date("begin"),
                LastUpdated = i.Date("modified") ?? latest.Date("when"),
                AffectedComponents = products,
            });
        }
        return SourceResult.Of(label, incidents, components.ToComponents(), checkedAt: now);
    }

    private static ServiceStatus Map(string? impact, string? severity)
    {
        var high = string.Equals(severity, "high", StringComparison.OrdinalIgnoreCase);
        return impact?.ToUpperInvariant() switch
        {
            "SERVICE_OUTAGE" => high ? ServiceStatus.MajorOutage : ServiceStatus.PartialOutage,
            "SERVICE_DISRUPTION" => high ? ServiceStatus.PartialOutage : ServiceStatus.Degraded,
            _ => ServiceStatus.Degraded, // SERVICE_INFORMATION: a known issue being worked on
        };
    }

    private static string? FirstLine(string? md)
    {
        var line = StripMarkdown(md)?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.TrimEnd(':').Equals("Summary", StringComparison.OrdinalIgnoreCase)); // often starts with "Summary:"
        return string.IsNullOrWhiteSpace(line) ? null : line.Length > 160 ? line[..160].TrimEnd() + "…" : line;
    }

    private static string? StripMarkdown(string? md) => md is null ? null : Markdown().Replace(md, "");
    [GeneratedRegex(@"\*\*|__|^#+\s*", RegexOptions.Multiline)] private static partial Regex Markdown();
}
