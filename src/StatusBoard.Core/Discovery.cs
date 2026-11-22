using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StatusBoard.Core;

public sealed record LogoImage(byte[] Bytes, string Extension);

public sealed record DetectionResult
{
    public bool Success { get; init; }
    public required string Message { get; init; }
    /// <summary>Adapter type: "statuspage", "feed", "statusio" or "salesforce".</summary>
    public string? Type { get; init; }
    public string? SourceUrl { get; init; }
    public string? SuggestedName { get; init; }
    public string? StatusPageUrl { get; init; }

    public static DetectionResult Fail(string message) => new() { Message = message };
}

/// <summary>Tiny HTML helpers (no HTML parser dependency).</summary>
internal static partial class Html
{
    public static IEnumerable<string> LinkTags(string html) => LinkTag().Matches(html).Select(m => m.Value);

    public static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
    }

    public static string? Title(string html) => TitleTag().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex LinkTag();
    [GeneratedRegex(@"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)] private static partial Regex TitleTag();
}

/// <summary>
/// A vendor's logo: the configured logo URL, else the best icon the status page advertises (apple-touch-icon, then PNG, largest
/// first), else its /favicon.ico, else the same from the vendor's main site. Only PNG, ICO, JPEG, GIF (what WPF draws without
/// extra codecs). Always fetched from the vendor itself.
/// </summary>
public static class LogoFinder
{
    public static async Task<LogoImage?> FindAsync(VendorConfig vendor, IHttpFetcher http, CancellationToken ct)
    {
        var opts = new FetchOptions { AllowInvalidCertificate = vendor.Sources.Any(s => s.GetFlag("allowInvalidCertificate")), MaxBytes = 1024 * 1024 };
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task<LogoImage?> TryAsync(IEnumerable<string> urls)
        {
            foreach (var url in urls.Where(tried.Add))
            {
                try
                {
                    var r = await http.GetAsync(url, ct, opts).ConfigureAwait(false);
                    if (Sniff(r.Body) is { } ext) return new LogoImage(r.Body, ext);
                }
                catch (FetchException) { }
            }
            return null;
        }

        if (Uri.TryCreate(vendor.Logo, UriKind.Absolute, out var direct) && direct.Scheme is "http" or "https"
            && await TryAsync([direct.AbsoluteUri]).ConfigureAwait(false) is { } configured) return configured;

        // The status page first, then the vendor's own site (status.example.com → example.com), checked only if the page has none.
        var pages = new[] { vendor.StatusPageUrl }.Concat(vendor.Sources.Where(s => s.Type is "statuspage" or "librenms").Select(s => s.Url))
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" ? abs : null).OfType<Uri>()
            .DistinctBy(u => u.AbsoluteUri).Take(2).ToList();
        pages.AddRange(pages.Select(MainSite).OfType<Uri>().Where(m => pages.All(p => p.Host != m.Host)).DistinctBy(m => m.Host).ToList());
        foreach (var page in pages)
        {
            var urls = new List<string>();
            try { urls.AddRange(CandidatesFromHtml(await http.GetTextAsync(page.AbsoluteUri, ct, opts with { MaxBytes = 4 * 1024 * 1024 }).ConfigureAwait(false), page)); }
            catch (FetchException) { }
            urls.Add(page.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
            if (await TryAsync(urls).ConfigureAwait(false) is { } found) return found;
        }
        return null;
    }

    /// <summary>The vendor's main site for a status subdomain (status.example.com → https://example.com/); null otherwise.</summary>
    internal static Uri? MainSite(Uri page)
    {
        var labels = page.Host.Split('.');
        return labels.Length >= 3 && labels[0].Contains("status", StringComparison.OrdinalIgnoreCase)
            ? new Uri($"https://{string.Join('.', labels[1..])}/") : null;
    }

    /// <summary>Icon links, best first: apple-touch-icon, PNG, other; larger first. SVG and data: URLs skipped.</summary>
    public static IReadOnlyList<string> CandidatesFromHtml(string html, Uri pageUri)
    {
        var found = new List<(string Url, int Rank, int Size)>();
        foreach (var tag in Html.LinkTags(html))
        {
            var rel = Html.Attr(tag, "rel")?.ToLowerInvariant() ?? "";
            var href = Html.Attr(tag, "href");
            if (!rel.Contains("icon") || string.IsNullOrWhiteSpace(href) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            href = WebUtility.HtmlDecode(href);
            if (href.StartsWith("//", StringComparison.Ordinal)) href = pageUri.Scheme + ":" + href;
            var type = Html.Attr(tag, "type") ?? "";
            if (!Uri.TryCreate(pageUri, href, out var abs) || abs.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || type.Contains("svg")) continue;
            if (abs.Query.Contains("default=1", StringComparison.OrdinalIgnoreCase)) continue; // the hosting platform's placeholder (StatusIQ)
            var rank = rel.Contains("apple-touch-icon") ? 0 : type.Contains("png") || abs.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            var size = int.TryParse((Html.Attr(tag, "sizes") ?? "").Split('x', 'X')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            found.Add((abs.AbsoluteUri, rank, size));
        }
        return found.OrderBy(f => f.Rank).ThenByDescending(f => f.Size).Select(f => f.Url).Distinct().ToList();
    }

    /// <summary>File extension for a drawable image, by magic number (Content-Type is often wrong).</summary>
    public static string? Sniff(byte[] b) => b.Length < 8 ? null : b switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0x00, 0x00, 0x01, 0x00, ..] => ".ico",
        [0xFF, 0xD8, 0xFF, ..] => ".jpg",
        [0x47, 0x49, 0x46, 0x38, ..] => ".gif",
        _ => null,
    };
}

/// <summary>
/// Works out how to read a status page from a pasted link. In order: Salesforce Trust product page; Statuspage API at the link
/// and the site root; the link being a feed; a Status.io page; a feed the page advertises; common feed paths.
/// </summary>
public static partial class VendorDetector
{
    private static readonly string[] CommonFeedPaths =
        ["/history.atom", "/history.rss", "/feed.rss", "/feed.atom", "/rss", "/feed", "/rss.xml", "/feed.xml", "/incidents.rss", "/state_feed/feed"];

    public static Uri? Normalise(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
        return Uri.TryCreate(input, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? new UriBuilder(u) { Query = "", Fragment = "" }.Uri : null;
    }

    public static async Task<DetectionResult> DetectAsync(string input, IHttpFetcher http, CancellationToken ct)
    {
        if (Normalise(input) is not { } uri) return DetectionResult.Fail("That doesn't look like a web address.");
        if (SalesforceProvider.ProductKey(uri.AbsoluteUri) is { } product) return await SalesforceProductAsync(product, http, ct).ConfigureAwait(false);

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath.TrimEnd('/');
        var apiIdx = path.IndexOf("/api/v2", StringComparison.OrdinalIgnoreCase);
        var roots = new List<string>();
        if (apiIdx >= 0) roots.Add(origin + path[..apiIdx]);
        else if (!FileExt().IsMatch(path)) roots.Add(origin + path);
        if (!roots.Contains(origin)) roots.Add(origin);

        foreach (var root in roots)
            if (await StatuspageAsync(root, http, ct).ConfigureAwait(false) is { } sp) return sp;

        FetchedBytes? page = null;
        string? pageError = null, pageTitle = null;
        try { page = await http.GetAsync(uri.AbsoluteUri, ct).ConfigureAwait(false); }
        catch (FetchException ex) { pageError = ex.Message; }

        if (page is not null)
        {
            var text = HttpFetcher.Decode(page.Body, page.CharSet);
            if (Feed(text, uri.AbsoluteUri, null) is { } direct) return direct;
            pageTitle = Html.Title(text);
            // Status.io-hosted page: its JSON API (components + incidents) beats the RSS feed.
            if (text.Contains("status.io", StringComparison.OrdinalIgnoreCase) && StatusIoId(text) is { } id)
            {
                try
                {
                    var r = StatusIoProvider.Parse(await http.GetTextAsync(StatusIoProvider.ApiBase + id, ct).ConfigureAwait(false), "x");
                    return Found("statusio", StatusIoProvider.ApiBase + id, CleanName(pageTitle), uri.AbsoluteUri, $"Found a Status.io status page with {r.Components.Count} components.");
                }
                catch (Exception ex) when (ex is FetchException or ParseException) { }
            }
            foreach (var href in AlternateFeeds(text, page.FinalUri))
                if (await FeedAtAsync(href, http, ct, uri.AbsoluteUri, pageTitle).ConfigureAwait(false) is { } r) return r;
        }

        foreach (var root in roots)
            foreach (var p in CommonFeedPaths)
                if (await FeedAtAsync(root + p, http, ct, uri.AbsoluteUri, pageTitle).ConfigureAwait(false) is { } r) return r;

        return DetectionResult.Fail(page is null
            ? $"Couldn't open that address ({pageError})."
            : "No Statuspage API or RSS/Atom feed found there. If the status page has an RSS or Atom link, paste that link instead.");
    }

    private static DetectionResult Found(string type, string source, string? name, string page, string message) =>
        new() { Success = true, Type = type, SourceUrl = source, SuggestedName = name, StatusPageUrl = page, Message = message };

    private static async Task<DetectionResult> SalesforceProductAsync(string product, IHttpFetcher http, CancellationToken ct)
    {
        var api = SalesforceProvider.ProductApiBase + Uri.EscapeDataString(product);
        try
        {
            var json = await http.GetTextAsync(api, ct).ConfigureAwait(false);
            var r = SalesforceProvider.ParseProduct(json, "x", null);
            using var doc = Json.TryParse(json);
            var name = doc?.RootElement.Str("name") ?? product.Replace('_', ' ');
            return Found("salesforce", api, name, SalesforceProvider.TrustUrl + "products/" + product, $"Found Salesforce Trust status for {name} ({r.Components.Count} instances).");
        }
        catch (Exception ex) when (ex is FetchException or ParseException)
        {
            return DetectionResult.Fail($"Salesforce Trust has no product “{product}” ({ex.Message}).");
        }
    }

    private static async Task<DetectionResult?> StatuspageAsync(string root, IHttpFetcher http, CancellationToken ct)
    {
        string json;
        try { json = await http.GetTextAsync(root + "/api/v2/summary.json", ct).ConfigureAwait(false); }
        catch (FetchException) { return null; }
        using var doc = Json.TryParse(json);
        var r = doc?.RootElement ?? default;
        if (!r.Has() || !r.TryGetProperty("page", out var page) || !r.TryGetProperty("status", out _)) return null;
        var comps = r.Arr("components").Count();
        var name = page.Str("name");
        return Found("statuspage", root, CleanName(name), page.Str("url") ?? root,
            "Found an Atlassian Statuspage API" + (name is null ? "" : $" for “{name}”") + (comps > 0 ? $" with {comps} components." : "."));
    }

    private static async Task<DetectionResult?> FeedAtAsync(string url, IHttpFetcher http, CancellationToken ct, string statusPage, string? fallbackName)
    {
        try
        {
            var r = Feed(await http.GetTextAsync(url, ct).ConfigureAwait(false), url, statusPage);
            return r is null ? null : r with { SuggestedName = r.SuggestedName ?? CleanName(fallbackName) };
        }
        catch (FetchException) { return null; }
    }

    private static DetectionResult? Feed(string text, string url, string? statusPage)
    {
        var t = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!t.StartsWith('<')) return null;
        XElement root;
        try
        {
            // No DTDs or external resources: a hostile page can't expand entities or make the app fetch anything.
            using var reader = System.Xml.XmlReader.Create(new StringReader(t), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
            root = XDocument.Load(reader).Root!;
        }
        catch (System.Xml.XmlException) { return null; }
        var kind = root.Name.LocalName switch { "rss" or "RDF" => "RSS", "feed" => "Atom", _ => null };
        if (kind is null) return null;
        var title = (root.Name.LocalName == "feed" ? root.Elements() : root.Descendants()).FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
        var items = root.Descendants().Count(e => e.Name.LocalName is "item" or "entry");
        var link = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "link") is { } l ? (l.Attribute("href")?.Value ?? l.Value).Trim() : null;
        return Found("feed", url, CleanName(title), statusPage ?? (Uri.TryCreate(link, UriKind.Absolute, out _) ? link! : url),
            $"Found an {kind} feed" + (title is { Length: > 0 } ? $" “{title}”" : "") + $" with {items} item{(items == 1 ? "" : "s")}.");
    }

    private static IEnumerable<string> AlternateFeeds(string html, Uri baseUri) => Html.LinkTags(html)
        .Where(t => Html.Attr(t, "rel")?.Contains("alternate", StringComparison.OrdinalIgnoreCase) == true)
        .Where(t => Html.Attr(t, "type") is { } type && (type.Contains("rss", StringComparison.OrdinalIgnoreCase) || type.Contains("atom", StringComparison.OrdinalIgnoreCase)))
        .Select(t => Html.Attr(t, "href") is { } href && Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href), out var abs) ? abs.AbsoluteUri : null)
        .OfType<string>();

    /// <summary>The Status.io page id: from a "/pages/{id}" link, else the most frequent 24-hex token on the page.</summary>
    internal static string? StatusIoId(string html) => StatusIoPageId().Match(html) is { Success: true } m ? m.Groups[1].Value
        : HexId().Matches(html).GroupBy(x => x.Value).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

    /// <summary>"GitHub Status - Incident History" → "GitHub".</summary>
    internal static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        for (var prev = ""; prev != n && n.Length > 0;)
        {
            prev = n;
            var next = NameNoise().Replace(n, "").Trim(' ', '-', '–', '|', ':');
            if (next.Length > 0) n = next;
        }
        return n;
    }

    [GeneratedRegex(@"/pages/([0-9a-f]{24})\b")] private static partial Regex StatusIoPageId();
    [GeneratedRegex(@"\b[0-9a-f]{24}\b")] private static partial Regex HexId();
    [GeneratedRegex(@"\.(json|xml|rss|atom|html?)$", RegexOptions.IgnoreCase)] private static partial Regex FileExt();
    [GeneratedRegex(@"(\s*[-–|:]\s*)?(incident history|status page|service status|system status|status hub|status)(\s*[-–|:].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NameNoise();
}
