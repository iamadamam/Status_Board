using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StatusBoard.Core;

/// <summary>Root of statusboard.json: everything the user can change.</summary>
public sealed class AppConfig
{
    /// <summary>Choices offered in Settings; 0 = manual refresh only.</summary>
    public static readonly int[] RefreshChoices = [1, 3, 5, 15, 30, 60, 0];

    public int RefreshIntervalMinutes { get; set; } = 5;
    /// <summary>"System", "Light", "Dark", "AMOLED" or "Hacker".</summary>
    public string Theme { get; set; } = "System";
    public bool TrayIcon { get; set; }
    public double DetailsPanelWidth { get; set; } = 440;
    /// <summary>Tile size multiplier (Ctrl +/− in the app).</summary>
    public double CardScale { get; set; } = 1.0;
    public bool NewsTicker { get; set; } = true;
    /// <summary>TV mode scrolls the board by itself (toggle in TV mode, or press S).</summary>
    public bool TvAutoScroll { get; set; } = true;
    /// <summary>Built-in catalogue version this file has seen (0 = unknown); newer built-ins are added on load.</summary>
    public int DefaultsVersion { get; set; }
    /// <summary>"Tiles" or "List".</summary>
    public string BoardView { get; set; } = "Tiles";
    /// <summary>The user's board sections, in order.</summary>
    public List<GroupConfig> Groups { get; set; } = [];
    public List<VendorConfig> Vendors { get; set; } = [];

    public static AppConfig CreateDefault() => new() { DefaultsVersion = DefaultVendors.Version, Vendors = DefaultVendors.Create() };

    public static string DescribeInterval(int minutes) => minutes switch
    {
        <= 0 => "Manual",
        60 => "1 hour",
        _ when minutes % 60 == 0 => $"{minutes / 60} hours",
        1 => "1 minute",
        _ => $"{minutes} minutes",
    };
}

/// <summary>A vendor card; rolls up one or more sources.</summary>
public sealed class VendorConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Added by the user in Settings (removable there).</summary>
    public bool Custom { get; set; }
    public string? Category { get; set; }
    /// <summary>The human-facing status page.</summary>
    public string? StatusPageUrl { get; set; }
    /// <summary>Logo URL or file path; empty = found automatically from the status page and cached in "logos".</summary>
    public string? Logo { get; set; }
    /// <summary>The user's group (<see cref="GroupConfig.Id"/>) this vendor is shown under; null = ungrouped.</summary>
    public string? GroupId { get; set; }
    public List<SourceConfig> Sources { get; set; } = [];
}

/// <summary>A user-made section of the board ("Critical", "Client-facing"…), shown above ungrouped vendors in list order.</summary>
public sealed class GroupConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>One machine-readable source; <see cref="Type"/> picks the adapter.</summary>
public sealed class SourceConfig
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Only these components count (status, incidents, alerts); null = all. See <see cref="ComponentFilter"/>.</summary>
    public List<string>? Components { get; set; }

    public string? GetOption(string key) => Options.TryGetValue(key, out var v) ? v : null;
    public bool GetFlag(string key) => bool.TryParse(GetOption(key), out var b) && b;
}

/// <summary>Sections, in order; unknown or empty goes to <see cref="Custom"/>.</summary>
public static class VendorCategories
{
    public const string Cloud = "Cloud & infrastructure", Productivity = "Productivity & collaboration", Security = "Security & identity",
        ItManagement = "IT management & remote access", Communications = "Communications", Developer = "Developer & AI",
        Business = "Business & finance", Education = "Education", Consumer = "Everyday & consumer", Custom = "Your vendors";

    public static readonly string[] All = [Cloud, Productivity, Security, ItManagement, Communications, Developer, Business, Education, Consumer, Custom];

    public static string Normalise(string? category) =>
        All.FirstOrDefault(c => string.Equals(c, category?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Custom;

    public static int Order(string? category) => Array.IndexOf(All, Normalise(category));
}

/// <summary>Reads and writes statusboard.json next to the executable (portable).</summary>
public sealed class ConfigStore(string path)
{
    public const string FileName = "statusboard.json";
    public const double MinCardScale = 0.7, MaxCardScale = 2.5;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // readable when hand-edited; never embedded in HTML
    };

    public string Path { get; } = path;

    /// <summary>Set when the existing file couldn't be read: defaults are used and the bad file is kept.</summary>
    public string? LoadWarning { get; private set; }

    public static ConfigStore NextToExecutable() => new(System.IO.Path.Combine(AppContext.BaseDirectory, FileName));

    public AppConfig Load()
    {
        LoadWarning = null;
        if (!File.Exists(Path))
        {
            var fresh = AppConfig.CreateDefault();
            TrySave(fresh);
            return fresh;
        }
        try
        {
            var cfg = Normalise(Deserialize(File.ReadAllText(Path)));
            if (DefaultVendors.Upgrade(cfg)) TrySave(cfg);
            return cfg;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var backup = Path + ".invalid";
            try { File.Copy(Path, backup, overwrite: true); } catch { /* best effort */ }
            LoadWarning = $"Couldn't read {FileName} ({ex.Message}). Using defaults; your old file was kept as {System.IO.Path.GetFileName(backup)}.";
            return AppConfig.CreateDefault();
        }
    }

    public static AppConfig Deserialize(string json) => JsonSerializer.Deserialize<AppConfig>(json, Options) ?? throw new JsonException("File is empty.");
    public static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, Options);

    /// <summary>Atomic: temp file + replace, so a crash can't leave half a config.</summary>
    public void Save(AppConfig config)
    {
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, Serialize(Normalise(config)));
        File.Move(tmp, Path, overwrite: true);
    }

    public bool TrySave(AppConfig config)
    {
        try { Save(config); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static AppConfig Normalise(AppConfig c)
    {
        c.RefreshIntervalMinutes = c.RefreshIntervalMinutes <= 0 ? 0 : Math.Min(c.RefreshIntervalMinutes, 24 * 60);
        c.DetailsPanelWidth = double.IsFinite(c.DetailsPanelWidth) ? Math.Clamp(c.DetailsPanelWidth, 300, 1200) : 440;
        c.CardScale = double.IsFinite(c.CardScale) && c.CardScale > 0 ? Math.Clamp(Math.Round(c.CardScale, 2), MinCardScale, MaxCardScale) : 1.0;
        c.Theme = new[] { "Light", "Dark", "AMOLED", "Hacker" }.FirstOrDefault(t => string.Equals(t, c.Theme?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "System";
        c.BoardView = string.Equals(c.BoardView?.Trim(), "List", StringComparison.OrdinalIgnoreCase) ? "List" : "Tiles";
        // Groups: unique ids and names; vendors pointing at a missing group become ungrouped.
        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        c.Groups = (c.Groups ?? []).Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Id) && groupIds.Add(g.Id.Trim())).ToList();
        foreach (var g in c.Groups) (g.Id, g.Name) = (g.Id.Trim(), string.IsNullOrWhiteSpace(g.Name) ? "Group" : g.Name.Trim());

        // A hand-edited file can hold nulls, blank or repeated ids: repair rather than crash (the board keys cards by id).
        c.Vendors = (c.Vendors ?? []).Where(v => v is not null).ToList();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in c.Vendors)
        {
            v.Name = string.IsNullOrWhiteSpace(v.Name) ? "Unnamed vendor" : v.Name.Trim();
            var id = string.IsNullOrWhiteSpace(v.Id) ? "vendor" : v.Id.Trim();
            var unique = id;
            for (var n = 2; !ids.Add(unique); n++) unique = $"{id}-{n}";
            v.Id = unique;
            if (v.GroupId is not null && !groupIds.Contains(v.GroupId.Trim())) v.GroupId = null;
            else v.GroupId = c.Groups.FirstOrDefault(g => string.Equals(g.Id, v.GroupId?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
            v.Sources = (v.Sources ?? []).Where(s => s is not null).ToList();
            foreach (var s in v.Sources)
            {
                (s.Type, s.Label, s.Url) = (s.Type ?? "", s.Label ?? "", s.Url ?? "");
                s.Components = s.Components?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                s.Options = new Dictionary<string, string>((s.Options ?? []).Where(kv => kv.Value is not null), StringComparer.OrdinalIgnoreCase);
            }
        }
        return c;
    }
}

/// <summary>
/// Keeps API tokens out of plain text: stored as "dpapi:&lt;base64&gt;", encrypted with Windows DPAPI for the current user,
/// so a copied config is useless elsewhere. A plain value typed into the file by hand also works.
/// </summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "StatusBoard.v1"u8.ToArray();

    public static string Protect(string plain) =>
        string.IsNullOrEmpty(plain) ? plain : Prefix + Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(plain), protect: true)!);

    /// <summary>The plain secret; throws <see cref="InvalidOperationException"/> if it can't be decrypted here.</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored ?? "";
        byte[]? plain = null;
        try { plain = Dpapi(Convert.FromBase64String(stored[Prefix.Length..]), protect: false); } catch (FormatException) { }
        return plain is null
            ? throw new InvalidOperationException("The saved API token can't be decrypted by this Windows user/PC. Re-enter it in Settings.")
            : Encoding.UTF8.GetString(plain);
    }

    // CryptProtectData/CryptUnprotectData directly: same blob format as ProtectedData (CurrentUser scope), no package needed.
    private static unsafe byte[]? Dpapi(byte[] data, bool protect)
    {
        fixed (byte* d = data, e = Entropy)
        {
            var input = new Blob { Size = data.Length, Data = (nint)d };
            var entropy = new Blob { Size = Entropy.Length, Data = (nint)e };
            var ok = protect ? CryptProtectData(ref input, null, ref entropy, 0, 0, UiForbidden, out var output)
                             : CryptUnprotectData(ref input, 0, ref entropy, 0, 0, UiForbidden, out output);
            if (!ok) return null;
            try { return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray(); }
            finally { LocalFree(output.Data); }
        }
    }

    private const int UiForbidden = 1;
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob dataOut);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, nint description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob dataOut);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint mem);
}
