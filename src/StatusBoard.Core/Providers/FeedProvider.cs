using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StatusBoard.Core;

/// <summary>
/// Generic RSS 2.0 / Atom 1.0 adapter. Feeds carry no machine-readable status, so items are read by options:
/// <c>mode</c> "active" (default) = every item is a current issue (Azure: empty when healthy); "recent" = current if updated within
/// <c>activeWindowHours</c> (default 24), for history feeds. <c>itemStatus</c> = status of a current item (default Degraded).
/// Resolved/completed items are ignored; items mentioning maintenance become Maintenance. Feeds that list every component with its
/// current state ("DNS - Operational") are read as components instead, whatever the mode.
/// </summary>
public sealed partial class FeedProvider : SimpleProvider
{
    public override string Type => "feed";
    protected override SourceResult Read(string text, SourceConfig s) => Parse(text, s.Label, FeedOptions.From(s), DateTimeOffset.Now);

    public sealed record FeedOptions(bool RecentMode, double WindowHours, ServiceStatus ItemStatus)
    {
        public static FeedOptions Default { get; } = new(false, 24, ServiceStatus.Degraded);

        public static FeedOptions From(SourceConfig s) => new(
            string.Equals(s.GetOption("mode"), "recent", StringComparison.OrdinalIgnoreCase),
            double.TryParse(s.GetOption("activeWindowHours"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 24,
            Enum.TryParse<ServiceStatus>(s.GetOption("itemStatus"), ignoreCase: true, out var st) && st != ServiceStatus.Unknown ? st : ServiceStatus.Degraded);
    }

    private sealed record Item(string Title, string? Link, string? Summary, DateTimeOffset? Published, DateTimeOffset? Updated);

    public static SourceResult Parse(string xml, string label, FeedOptions options, DateTimeOffset now)
    {
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            root = XDocument.Load(reader).Root ?? throw new ParseException("Empty feed");
        }
        catch (XmlException ex) { throw new ParseException("Not valid XML", ex); }

        var items = (root.Name.LocalName switch
        {
            "rss" or "RDF" => Rss(root),
            "feed" => Atom(root),
            _ => throw new ParseException($"Not an RSS or Atom feed (root <{root.Name.LocalName}>)"),
        }).ToList();

        // Component-status feeds (StatusIQ and similar): one item per component, titled "<component> - <state>". Read as components.
        if (items.Count > 0 && items.Select(i => ComponentTitle().Match(i.Title)).ToList() is var parts
            && parts.All(m => m.Success && ComponentState(m.Groups[2].Value) is not null))
        {
            var states = new Dictionary<string, ServiceStatus>();
            var problems = new List<Incident>();
            foreach (var (item, m) in items.Zip(parts))
            {
                var state = ComponentState(m.Groups[2].Value)!.Value;
                states.Bump(m.Groups[1].Value.Trim(), state);
                if (state != ServiceStatus.Operational)
                    problems.Add(new() { Title = item.Title, Summary = TextUtil.Clean(item.Summary), Url = item.Link, Status = state, LastUpdated = item.Published });
            }
            return SourceResult.Of(label, problems, states.ToComponents(), states.Values.Append(ServiceStatus.Operational).Worst(), now);
        }

        // When items link to their own incident page, several are updates of one incident: keep the newest per link, so an
        // old "Investigating" entry can't outlive its "Resolved". Feeds whose items share one link (Azure, AWS) are left alone.
        if (items.Select(i => i.Link).Where(l => !string.IsNullOrEmpty(l)).Distinct().Count() > 1)
            items = items.GroupBy(i => string.IsNullOrEmpty(i.Link) ? Guid.NewGuid().ToString() : i.Link)
                         .Select(g => g.OrderByDescending(i => i.Updated ?? i.Published ?? DateTimeOffset.MinValue).First()).ToList();

        var incidents = items
            .Where(i => !LooksResolved(i.Title, i.Summary) && !InformationalTitle().IsMatch(i.Title))
            .Where(i => !options.RecentMode || (i.Updated ?? i.Published) is { } when && now - when <= TimeSpan.FromHours(options.WindowHours))
            .Select(i =>
            {
                var maintenance = Maintenance().IsMatch(i.Title);
                var stage = StageOf(i.Title, i.Summary);
                if (maintenance && stage is IncidentStage.None or IncidentStage.Update) stage = IncidentStage.InProgress;
                return new Incident
                {
                    Title = i.Title,
                    Summary = TextUtil.Clean(i.Summary),
                    Url = i.Link,
                    Status = maintenance ? ServiceStatus.Maintenance : TagStatus(i.Title) ?? options.ItemStatus,
                    Stage = stage,
                    Started = i.Published,
                    LastUpdated = i.Updated ?? i.Published,
                };
            })
            // Announced but not started: like Statuspage's upcoming windows, not a current event.
            .Where(i => !(i.Status == ServiceStatus.Maintenance && i.Stage == IncidentStage.Scheduled))
            .ToList();
        return SourceResult.Of(label, incidents, checkedAt: now);
    }

    private static string? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value?.Trim();

    private static IEnumerable<Item> Rss(XElement root) => root.Descendants().Where(e => e.Name.LocalName == "item").Select(i =>
        new Item(Child(i, "title") ?? "(untitled)", Child(i, "link"), Child(i, "description") ?? Child(i, "encoded"), ParseDate(Child(i, "pubDate") ?? Child(i, "date")), null));

    private static IEnumerable<Item> Atom(XElement root) => root.Elements().Where(e => e.Name.LocalName == "entry").Select(e =>
    {
        var links = e.Elements().Where(x => x.Name.LocalName == "link").ToList();
        var link = (links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate") ?? links.FirstOrDefault())?.Attribute("href")?.Value;
        return new Item(Child(e, "title") ?? "(untitled)", link, Child(e, "content") ?? Child(e, "summary"), ParseDate(Child(e, "published")), ParseDate(Child(e, "updated")));
    });

    /// <summary>Resolved if the title says so, or the first status word in the body (newest update first) is Resolved/Completed.</summary>
    internal static bool LooksResolved(string title, string? body)
    {
        if (ResolvedTitle().IsMatch(title)) return true;
        var m = TextUtil.Clean(body, int.MaxValue) is { } text ? StatusWord().Match(text) : Match.Empty;
        return m.Success && m.Groups[1].Value.ToLowerInvariant() is "resolved" or "completed";
    }

    /// <summary>Stage from a leading title tag ("[Monitoring] …"), else the first status word of the body (newest update first).</summary>
    internal static IncidentStage StageOf(string title, string? body)
    {
        if (IncidentStages.FromText(title) is var t && t != IncidentStage.None) return t;
        // Only a label ("Monitoring - …", "Update: …"), not prose such as "the scheduled maintenance".
        var m = TextUtil.Clean(body, int.MaxValue) is { } text ? StageLabel().Match(text) : Match.Empty;
        return m.Success ? IncidentStages.FromWord(m.Groups[1].Value) : IncidentStage.None;
    }

    [GeneratedRegex(@"\b(Monitoring|Investigating|Identified|Update|Scheduled|In progress|Verifying)\s*[-–—:]", RegexOptions.IgnoreCase)] private static partial Regex StageLabel();

    private static readonly Dictionary<string, string> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Z"] = "+0000", ["UT"] = "+0000", ["UTC"] = "+0000", ["GMT"] = "+0000", ["EST"] = "-0500", ["EDT"] = "-0400",
        ["CST"] = "-0600", ["CDT"] = "-0500", ["MST"] = "-0700", ["MDT"] = "-0600", ["PST"] = "-0800", ["PDT"] = "-0700",
    };

    /// <summary>RFC 822 (RSS) and ISO 8601 (Atom) dates, including named US zones and a bare "Z".</summary>
    internal static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (TrailingZone().Match(s) is { Success: true } m && Zones.TryGetValue(m.Groups[1].Value, out var off)) s = s[..m.Index] + " " + off;
        var norm = Regex.Replace(s, @"([+-]\d{2})(\d{2})$", "$1:$2");
        string[] formats = ["ddd, d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm zzz"];
        if (DateTimeOffset.TryParseExact(norm, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d) ? d : null;
    }

    /// <summary>Status from a leading "[Tag]" in the title, e.g. "[Major Outage] …".</summary>
    internal static ServiceStatus? TagStatus(string title)
    {
        if (LeadingTag().Match(title) is not { Success: true } m) return null;
        var tag = m.Groups[1].Value.ToLowerInvariant();
        if (tag.Contains("major") || tag is "outage" or "down") return ServiceStatus.MajorOutage;
        if (tag.Contains("partial") || tag.Contains("outage") || tag.Contains("disruption")) return ServiceStatus.PartialOutage;
        return tag.Contains("degrad") ? ServiceStatus.Degraded : null;
    }

    /// <summary>A component state word from a component-status feed title; null if it isn't one.</summary>
    internal static ServiceStatus? ComponentState(string state) => state.Trim().ToLowerInvariant() switch
    {
        "operational" or "informational" or "up" or "available" => ServiceStatus.Operational,
        var s when s.Contains("maintenance") => ServiceStatus.Maintenance,
        var s when s.Contains("major") || s is "outage" or "down" => ServiceStatus.MajorOutage,
        var s when s.Contains("partial") || s.Contains("outage") => ServiceStatus.PartialOutage,
        var s when s.Contains("degrad") => ServiceStatus.Degraded,
        _ => null,
    };

    [GeneratedRegex(@"^(.+)\s[-–]\s([A-Za-z ]{2,30})$")] private static partial Regex ComponentTitle();
    [GeneratedRegex(@"\s([A-Za-z]{1,4})$")] private static partial Regex TrailingZone();
    [GeneratedRegex(@"^\s*\[([^\]]{1,40})\]")] private static partial Regex LeadingTag();
    [GeneratedRegex(@"^\s*\[?(resolved|completed|ended|cancell?ed|closed)\b|service is operating normally", RegexOptions.IgnoreCase)] private static partial Regex ResolvedTitle();
    [GeneratedRegex(@"^\s*\[(information|informational|info|notice|announcement)\]", RegexOptions.IgnoreCase)] private static partial Regex InformationalTitle();
    [GeneratedRegex(@"\b(Resolved|Completed|Monitoring|Investigating|Identified|Update|Scheduled|In progress|Verifying)\b", RegexOptions.IgnoreCase)] private static partial Regex StatusWord();
    [GeneratedRegex(@"\bmaintenance\b", RegexOptions.IgnoreCase)] private static partial Regex Maintenance();
}
