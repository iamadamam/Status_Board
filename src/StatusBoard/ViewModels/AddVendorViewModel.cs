using System.Windows.Input;
using StatusBoard.Core;

namespace StatusBoard.ViewModels;

/// <summary>
/// "Add a vendor": a status page/feed link (<see cref="VendorDetector"/> works out how to read it), or a LibreNMS server
/// (base URL + API token). Nothing is added until "Check" succeeds.
/// </summary>
public sealed class AddVendorViewModel : ObservableObject
{
    public const string KindStatusPage = "Status page or feed", KindLibreNms = "LibreNMS", KindWebsite = "Website check", KindPing = "Ping check";

    private readonly IHttpFetcher _http;
    private readonly Action<VendorConfig> _add;
    private string _kind = KindStatusPage, _url = "", _name = "", _token = "";
    private string? _message;
    private bool _allowInvalidCert, _includeAck, _feedEveryItemActive, _isChecking, _checkOk;
    private DetectionResult? _detected;
    private CancellationTokenSource? _cts;

    public AddVendorViewModel(IHttpFetcher http, Action<VendorConfig> add, Action cancel)
    {
        (_http, _add) = (http, add);
        CheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !IsChecking && !string.IsNullOrWhiteSpace(Url));
        AddCommand = new RelayCommand(DoAdd, () => _checkOk && !IsChecking);
        CancelCommand = new RelayCommand(() => { _cts?.Cancel(); cancel(); });
    }

    public string[] Kinds { get; } = [KindStatusPage, KindLibreNms, KindWebsite, KindPing];
    public string Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            Invalidate();
            SlowMs = IsPing ? "300" : "2000";
            Notify(nameof(IsLibreNms), nameof(UrlHint), nameof(IsCheck), nameof(IsWebsite), nameof(IsPing), nameof(CheckButtonText));
        }
    }
    public bool IsLibreNms => _kind == KindLibreNms;
    public bool IsWebsite => _kind == KindWebsite;
    public bool IsPing => _kind == KindPing;
    /// <summary>Your own website or ping check rather than a vendor's status page.</summary>
    public bool IsCheck => IsWebsite || IsPing;
    public string UrlHint => _kind switch
    {
        KindLibreNms => "LibreNMS address, e.g. https://librenms.example.local",
        KindWebsite => "Web address to check, e.g. https://intranet.example.com/login",
        KindPing => "Host name or IP address, e.g. 10.0.0.1 or fw01.example.local",
        _ => "Status page link, e.g. https://www.githubstatus.com — or its RSS/Atom feed",
    };
    public string CheckButtonText => IsCheck ? "Test now" : "Check";

    // ---- Website / ping check options ----
    private string _slowMs = "2000", _failures = "3", _expect = "200-399";
    public string SlowMs { get => _slowMs; set => Set(ref _slowMs, value); }
    public string FailuresBeforeDown { get => _failures; set => Set(ref _failures, value); }
    public string ExpectedCodes { get => _expect; set => Set(ref _expect, value); }
    public string Url { get => _url; set { if (Set(ref _url, value)) Invalidate(); } }
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>Set from the PasswordBox in code-behind (it can't be data-bound).</summary>
    public string Token { get => _token; set { if (Set(ref _token, value)) Invalidate(); } }
    public bool AllowInvalidCertificate { get => _allowInvalidCert; set { if (Set(ref _allowInvalidCert, value)) Invalidate(); } }
    public bool IncludeAcknowledged { get => _includeAck; set => Set(ref _includeAck, value); }
    /// <summary>Feeds: false = only items from the last 24 h are current (history feeds); true = every item is current.</summary>
    public bool FeedEveryItemActive { get => _feedEveryItemActive; set => Set(ref _feedEveryItemActive, value); }
    public bool IsFeed => _detected?.Type == "feed";
    public bool IsChecking { get => _isChecking; private set { if (Set(ref _isChecking, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string? Message { get => _message; private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public bool CheckSucceeded { get => _checkOk; private set { if (Set(ref _checkOk, value)) CommandManager.InvalidateRequerySuggested(); } }

    public ICommand CheckCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand CancelCommand { get; }

    private void Invalidate()
    {
        CheckSucceeded = false;
        _detected = null;
        OnPropertyChanged(nameof(IsFeed));
        Message = null;
    }

    private async Task CheckAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        IsChecking = true;
        Message = IsLibreNms ? "Connecting…" : "Looking for a status API or feed…";
        CheckSucceeded = false;
        try
        {
            if (IsLibreNms) await CheckLibreNmsAsync(_cts.Token);
            else if (IsCheck) await TestCheckAsync(_cts.Token);
            else
            {
                var r = await VendorDetector.DetectAsync(Url, _http, _cts.Token);
                _detected = r.Success ? r : null;
                OnPropertyChanged(nameof(IsFeed));
                if (r.Success && string.IsNullOrWhiteSpace(Name)) Name = r.SuggestedName ?? "";
                Message = (r.Success ? "✓ " : "") + r.Message;
                CheckSucceeded = r.Success;
            }
        }
        catch (OperationCanceledException) { Message = "Cancelled or timed out."; }
        finally { IsChecking = false; }
    }

    private async Task CheckLibreNmsAsync(CancellationToken ct)
    {
        if (VendorDetector.Normalise(Url) is not { } uri) { Message = "That doesn't look like a web address."; return; }
        if (string.IsNullOrWhiteSpace(Token)) { Message = "Enter an API token (LibreNMS: Settings › API › API Settings)."; return; }
        Url = uri.AbsoluteUri.TrimEnd('/');
        try
        {
            var r = await new LibreNmsProvider().FetchAsync(LibreNmsSource(), _http, ct);
            var down = r.Incidents.Count(i => i.Title.EndsWith(" is down", StringComparison.Ordinal));
            var alerts = r.Incidents.Count - down;
            Message = $"✓ Connected. {alerts} active alert{(alerts == 1 ? "" : "s")}, {down} device{(down == 1 ? "" : "s")} down.";
            if (string.IsNullOrWhiteSpace(Name)) Name = "LibreNMS";
            CheckSucceeded = true;
        }
        catch (FetchException ex) { Message = ex.Message + (ex.Message.Contains("TLS") ? " – tick “Allow self-signed certificate” if your server uses one." : ""); }
        catch (ParseException ex) { Message = ex.Message; }
    }

    /// <summary>One probe now, so you see whether it answers; a check can still be added while its target is down.</summary>
    private async Task TestCheckAsync(CancellationToken ct)
    {
        if (CheckSource() is not { } source) return;
        Message = IsPing ? "Pinging…" : "Requesting…";
        var probe = source.Type == "ping" ? (IStatusProvider)new PingCheckProvider() : new HttpCheckProvider(); // fresh: no shared failure count
        var test = new SourceConfig { Type = source.Type, Label = source.Label, Url = source.Url, Options = new(source.Options, StringComparer.OrdinalIgnoreCase) { ["failuresBeforeDown"] = "1" } };
        try
        {
            var r = await probe.FetchAsync(test, _http, ct);
            Message = r.Status == ServiceStatus.Operational
                ? $"✓ {r.Note}. It will be checked every minute."
                : $"{(r.Status == ServiceStatus.Degraded ? "Responding slowly" : "Not responding")}: {r.Incidents.FirstOrDefault()?.Summary?.Split(" — ")[0] ?? r.Note}. You can still add it.";
        }
        catch (Exception ex) when (ex is ParseException or FetchException) { Message = ex.Message + ". You can still add it."; }
        if (string.IsNullOrWhiteSpace(Name)) Name = source.Label;
        CheckSucceeded = true;
    }

    /// <summary>The source for a website/ping check, or null (with a message) if the address can't be used.</summary>
    private SourceConfig? CheckSource()
    {
        var raw = Url.Trim();
        string url, label;
        if (IsWebsite)
        {
            if (VendorDetector.Normalise(raw) is not { } uri) { Message = "That doesn't look like a web address."; return null; }
            (url, label) = (uri.AbsoluteUri, uri.Host);
        }
        else
        {
            var host = PingCheckProvider.HostOf(raw);
            if (host.Length == 0 || host.Any(char.IsWhiteSpace) || host.Length > 253) { Message = "Enter a host name or IP address."; return null; }
            (url, label) = (host, host);
        }
        if (!int.TryParse(SlowMs.Trim(), out var slow) || slow is < 1 or > 120_000) { Message = "“Slow if over” must be a number of milliseconds (1–120000)."; return null; }
        if (!int.TryParse(FailuresBeforeDown.Trim(), out var fails) || fails is < 1 or > 20) { Message = "“Down after” must be between 1 and 20 failed checks."; return null; }
        var source = new SourceConfig { Type = IsPing ? "ping" : "http", Label = label, Url = url };
        source.Options["slowMs"] = slow.ToString(System.Globalization.CultureInfo.InvariantCulture);
        source.Options["failuresBeforeDown"] = fails.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (IsWebsite && !string.IsNullOrWhiteSpace(ExpectedCodes)) source.Options["expect"] = ExpectedCodes.Trim();
        return source;
    }

    private SourceConfig LibreNmsSource() => new()
    {
        Type = "librenms",
        Label = "LibreNMS",
        Url = Url.TrimEnd('/'),
        Options = new(StringComparer.OrdinalIgnoreCase)
        {
            ["token"] = Secrets.Protect(Token.Trim()),
            ["allowInvalidCertificate"] = AllowInvalidCertificate ? "true" : "false",
            ["includeAcknowledged"] = IncludeAcknowledged ? "true" : "false",
        },
    };

    private void DoAdd()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? (IsLibreNms ? "LibreNMS" : IsCheck ? PingCheckProvider.HostOf(Url) : _detected?.SuggestedName ?? "Custom vendor") : Name.Trim();
        SourceConfig source;
        if (IsLibreNms) source = LibreNmsSource();
        else if (IsCheck)
        {
            if (CheckSource() is not { } check) return;
            (source = check).Label = name;
        }
        else if (_detected is null) return;
        else
        {
            source = new SourceConfig { Type = _detected.Type!, Label = name, Url = _detected.SourceUrl! };
            if (_detected.Type == "feed") (source.Options["mode"], source.Options["activeWindowHours"]) = (FeedEveryItemActive ? "active" : "recent", "24");
        }
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        _add(new VendorConfig
        {
            Id = $"custom-{(slug.Length > 24 ? slug[..24] : slug)}-{Guid.NewGuid().ToString("N")[..6]}",
            Name = name,
            Custom = true,
            StatusPageUrl = IsLibreNms || IsWebsite ? source.Url : IsPing ? null : _detected!.StatusPageUrl,
            Sources = [source],
        });
    }
}
