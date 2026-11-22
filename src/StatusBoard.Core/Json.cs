using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StatusBoard.Core;

/// <summary>Raised by a parser when a response isn't in the expected shape.</summary>
public sealed class ParseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Small, forgiving accessors for the loosely-typed JSON that status pages return.</summary>
internal static class Json
{
    /// <summary>Parses or throws <see cref="ParseException"/> (so adapters don't each repeat the try/catch).</summary>
    public static JsonDocument Parse(string text, string error = "Not valid JSON")
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new ParseException(error, ex); }
    }

    /// <summary>Parses, or null for anything that isn't JSON (for optional extras).</summary>
    public static JsonDocument? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonDocument.Parse(text); } catch (JsonException) { return null; }
    }

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static bool Has(this JsonElement e) => e.ValueKind == JsonValueKind.Object;

    /// <summary>The array property <paramref name="name"/>, or nothing.</summary>
    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    /// <summary>Names (strings, or objects with a name field) in an array property.</summary>
    public static List<string> Names(this JsonElement e, string name, string field = "name", bool distinct = false)
    {
        var names = e.Arr(name).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.Str(field)).OfType<string>();
        return distinct ? names.Distinct().ToList() : names.ToList();
    }

    public static DateTimeOffset? Date(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (string.IsNullOrWhiteSpace(s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return null;
        return d.Year <= 1 ? null : d; // Microsoft uses 0001-01-01 for "none"
    }

    /// <summary>Unix epoch seconds given as a string or number.</summary>
    public static DateTimeOffset? Epoch(this JsonElement e, string name) =>
        long.TryParse(e.Str(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) && secs > 0 ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;

    /// <summary>The newest element of an array property, dated by the first of <paramref name="dateFields"/> present.</summary>
    public static JsonElement Latest(this JsonElement e, string array, params string[] dateFields) =>
        e.Arr(array).OrderBy(x => dateFields.Select(f => x.Date(f)).FirstOrDefault(d => d is not null) ?? DateTimeOffset.MinValue).LastOrDefault();

    /// <summary>Adds a component, keeping the worst status if it's already there.</summary>
    public static void Bump(this Dictionary<string, ServiceStatus> d, string name, ServiceStatus s) =>
        d[name] = d.TryGetValue(name, out var cur) ? cur.Worst(s) : s;

    public static List<ComponentStatus> ToComponents(this Dictionary<string, ServiceStatus> d) =>
        d.Select(kv => new ComponentStatus(kv.Key, kv.Value)).ToList();
}

internal static partial class TextUtil
{
    /// <summary>Strips HTML tags/entities and collapses whitespace, capping length for display.</summary>
    public static string? Clean(string? html, int max = 800)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var s = Tags().Replace(BreakTags().Replace(html, "\n"), "");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        s = BlankLines().Replace(Spaces().Replace(s, " "), "\n").Trim();
        if (s.Length == 0) return null;
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li)\s*/?\s*>", RegexOptions.IgnoreCase)] private static partial Regex BreakTags();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"[ \t\r\f\v]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\s*\n\s*")] private static partial Regex BlankLines();
}
