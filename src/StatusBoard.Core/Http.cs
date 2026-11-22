using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace StatusBoard.Core;

/// <summary>Per-request extras: auth headers, opting in to self-signed certificates, and a size cap.</summary>
public sealed record FetchOptions
{
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public bool AllowInvalidCertificate { get; init; }
    public long MaxBytes { get; init; } = 20 * 1024 * 1024;
    /// <summary>Overrides the fetcher's retry count (website checks use 1 so timings and failures are real).</summary>
    public int? Attempts { get; init; }
    /// <summary>Overrides the per-attempt timeout.</summary>
    public TimeSpan? Timeout { get; init; }
}

public sealed record FetchedBytes(byte[] Body, string? MediaType, string? CharSet, Uri FinalUri);

public sealed class FetchException(string message, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; init; }
}

/// <summary>HTTP access, abstracted so adapters can be tested offline. Failures throw <see cref="FetchException"/> with a short reason.</summary>
public interface IHttpFetcher
{
    Task<FetchedBytes> GetAsync(string url, CancellationToken ct, FetchOptions? options = null);

    async Task<string> GetTextAsync(string url, CancellationToken ct, FetchOptions? options = null)
    {
        var r = await GetAsync(url, ct, options).ConfigureAwait(false);
        return HttpFetcher.Decode(r.Body, r.CharSet);
    }
}

/// <summary>HttpClient with a per-attempt timeout and retries with jittered exponential backoff.</summary>
public sealed class HttpFetcher : IHttpFetcher, IDisposable
{
    private readonly HttpClient _http;
    // Created only when needed: [0] self-signed certificates allowed (opt-in per source, e.g. an internal LibreNMS); [1] requests
    // carrying credentials, which never follow redirects so a token can't be forwarded to another host; [2] both.
    private readonly Lazy<HttpClient>[] _special;
    private readonly int _attempts;
    private readonly TimeSpan _timeout, _baseDelay;

    public HttpFetcher(int maxAttempts = 3, TimeSpan? attemptTimeout = null, TimeSpan? baseDelay = null, HttpMessageHandler? handler = null)
    {
        _attempts = Math.Max(1, maxAttempts);
        _timeout = attemptTimeout ?? TimeSpan.FromSeconds(15);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _http = Client(handler ?? Handler(validate: true, redirects: true));
        _special = [.. new (bool Validate, bool Redirects)[] { (false, true), (true, false), (false, false) }
            .Select(o => new Lazy<HttpClient>(() => handler is null ? Client(Handler(o.Validate, o.Redirects)) : _http))];
    }

    private static SocketsHttpHandler Handler(bool validate, bool redirects)
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = redirects, MaxAutomaticRedirections = 5, // and never from HTTPS down to HTTP (.NET's rule)
            // Short-lived, few connections: idle ones close a minute after a refresh instead of lingering through firewalls.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60), MaxConnectionsPerServer = 4,
        };
        if (!validate) h.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return h;
    }

    private static HttpClient Client(HttpMessageHandler handler)
    {
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StatusBoard", typeof(HttpFetcher).Assembly.GetName().Version!.ToString(2)));
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/rss+xml, application/atom+xml, application/xml, text/xml;q=0.9, */*;q=0.5");
        return c;
    }

    public async Task<FetchedBytes> GetAsync(string url, CancellationToken ct, FetchOptions? options = null)
    {
        options ??= new FetchOptions();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new FetchException("Invalid URL");
        var client = (options.AllowInvalidCertificate, Credentials: options.Headers.Count > 0) switch
        {
            (false, false) => _http,
            (true, false) => _special[0].Value,
            (false, true) => _special[1].Value,
            _ => _special[2].Value,
        };

        FetchException? last = null;
        var attempts = Math.Max(1, options.Attempts ?? _attempts);
        var timeout = options.Timeout ?? _timeout;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                foreach (var (k, v) in options.Headers) req.Headers.TryAddWithoutValidation(k, v);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    if (resp.Content.Headers.ContentLength > options.MaxBytes) throw new FetchException("Response too large");
                    var body = await ReadLimitedAsync(resp.Content, options.MaxBytes, cts.Token).ConfigureAwait(false);
                    var type = resp.Content.Headers.ContentType;
                    return new FetchedBytes(body, type?.MediaType, type?.CharSet, resp.RequestMessage?.RequestUri ?? uri);
                }
                var code = (int)resp.StatusCode;
                if (code is >= 300 and < 400) // only requests with credentials get here: they don't follow redirects
                {
                    var to = resp.Headers.Location is { } l && Uri.TryCreate(uri, l, out var abs) ? abs.GetLeftPart(UriPartial.Path) : "another address";
                    throw new FetchException($"HTTP {code} – redirected to {to}; use that address instead") { StatusCode = code };
                }
                last = new FetchException(code is 401 or 403 ? $"HTTP {code} – access denied (check the API token)" : $"HTTP {code} {resp.ReasonPhrase}".Trim()) { StatusCode = code };
                if (code is >= 400 and < 500 and not 408 and not 429) throw last; // won't fix itself on retry
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new FetchException($"Timed out after {timeout.TotalSeconds:0}s");
            }
            catch (HttpRequestException ex)
            {
                last = new FetchException(ex.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => "Couldn't resolve host (offline?)",
                    HttpRequestError.ConnectionError => "Couldn't connect",
                    HttpRequestError.SecureConnectionError => "TLS/certificate error",
                    _ => "Network error",
                }, ex);
                if (ex.HttpRequestError == HttpRequestError.SecureConnectionError) throw last; // nor will a bad certificate
            }
            if (attempt < attempts)
                await Task.Delay(_baseDelay * (Math.Pow(2, attempt - 1) * (0.8 + Random.Shared.NextDouble() * 0.4)), ct).ConfigureAwait(false);
        }
        throw last ?? new FetchException("Unknown error");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, long max, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > max) throw new FetchException("Response too large");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>Decodes honouring a byte-order mark first (AWS serves UTF-16 BE), then the charset, then UTF-8.</summary>
    public static string Decode(byte[] b, string? charset)
    {
        if (b is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        Encoding enc;
        try
        {
            enc = charset?.Trim('"').ToLowerInvariant() switch
            {
                null or "" => Encoding.UTF8,
                "utf-16" or "utf-16be" => Encoding.BigEndianUnicode, // no BOM: RFC 2781 says big-endian
                "utf-16le" => Encoding.Unicode,
                var c => Encoding.GetEncoding(c),
            };
        }
        catch (ArgumentException) { enc = Encoding.UTF8; }
        return enc.GetString(b);
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var c in _special) if (c.IsValueCreated && !ReferenceEquals(c.Value, _http)) c.Value.Dispose();
    }
}
