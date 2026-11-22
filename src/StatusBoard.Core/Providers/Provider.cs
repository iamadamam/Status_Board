namespace StatusBoard.Core;

/// <summary>
/// Turns one configured source into a <see cref="SourceResult"/>. Fetching and parsing are separate so parsing is tested
/// from saved samples; throwing is fine (<see cref="StatusService"/> turns it into "couldn't reach").
/// </summary>
public interface IStatusProvider
{
    /// <summary>The <see cref="SourceConfig.Type"/> this adapter handles.</summary>
    string Type { get; }
    Task<SourceResult> FetchAsync(SourceConfig source, IHttpFetcher http, CancellationToken ct);
}

/// <summary>Adapters that make one GET and parse the text: they only say where to fetch and how to read it.</summary>
public abstract class SimpleProvider : IStatusProvider
{
    public abstract string Type { get; }
    protected virtual string UrlFor(SourceConfig source) => source.Url;
    protected abstract SourceResult Read(string text, SourceConfig source);

    public async Task<SourceResult> FetchAsync(SourceConfig source, IHttpFetcher http, CancellationToken ct) =>
        Read(await http.GetTextAsync(UrlFor(source), ct).ConfigureAwait(false), source);
}

/// <summary>Maps <see cref="SourceConfig.Type"/> to an adapter.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IStatusProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry Add(IStatusProvider p) { _providers[p.Type] = p; return this; }
    public IStatusProvider? Find(string type) => _providers.GetValueOrDefault(type);

    public static ProviderRegistry CreateDefault()
    {
        var r = new ProviderRegistry();
        foreach (var p in new IStatusProvider[]
                 {
                     new StatuspageProvider(), new FeedProvider(), new MicrosoftStatusProvider(), new AwsHealthProvider(), new LibreNmsProvider(),
                     new StatusIoProvider(), new SalesforceProvider(), new GoogleStatusProvider(), new SlackStatusProvider(),
                     new AppleStatusProvider(), new StripeStatusProvider(), new PayPalStatusProvider(),
                     new HttpCheckProvider(), new PingCheckProvider(),
                 })
            r.Add(p);
        return r;
    }
}
