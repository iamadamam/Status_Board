using System.Text;
using StatusBoard.Core;

namespace StatusBoard.Tests;

/// <summary>Saved responses in Samples/ (copied next to the test assembly).</summary>
internal static class Samples
{
    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", name);
    public static string Text(string name) => File.ReadAllText(PathOf(name));
    public static byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));
}

/// <summary>Serves canned responses by URL; anything else is a 404. Records requests.</summary>
internal sealed class FakeFetcher : IHttpFetcher
{
    private readonly Dictionary<string, (byte[] Body, string? Type)> _map = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Url, FetchOptions? Options)> Requests { get; } = [];

    public FakeFetcher Add(string url, string body, string? type = "application/json") { _map[url] = (Encoding.UTF8.GetBytes(body), type); return this; }
    public FakeFetcher Add(string url, byte[] body, string? type = null) { _map[url] = (body, type); return this; }

    public Task<FetchedBytes> GetAsync(string url, CancellationToken ct, FetchOptions? options = null)
    {
        Requests.Add((url, options));
        return _map.TryGetValue(url, out var r)
            ? Task.FromResult(new FetchedBytes(r.Body, r.Type, "utf-8", new Uri(url)))
            : Task.FromException<FetchedBytes>(new FetchException("HTTP 404 Not Found") { StatusCode = 404 });
    }
}
