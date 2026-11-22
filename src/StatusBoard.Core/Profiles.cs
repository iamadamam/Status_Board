using System.Text.Json;

namespace StatusBoard.Core;

/// <summary>One profile: a name and its own settings file next to the app.</summary>
public sealed class ProfileEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>File name (no folder) of this profile's settings, e.g. "statusboard.json" or "profile-ops-1a2b3c.json".</summary>
    public string File { get; set; } = "";
}

public sealed class ProfileIndex
{
    public string? Active { get; set; }
    public List<ProfileEntry> Profiles { get; set; } = [];
}

/// <summary>
/// Profiles, each a complete set of settings (vendors, groups, theme…) in its own file, listed in profiles.json next to the exe.
/// The first profile uses statusboard.json, so a copy of the app from before profiles existed simply becomes a profile called
/// "Default". With no profiles at all, the app runs first-time setup.
/// </summary>
public sealed class ProfileStore(string folder)
{
    public const string IndexFile = "profiles.json", DefaultName = "Default";
    public const int MaxNameLength = 40;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private ProfileIndex _index = new();

    public string Folder { get; } = folder;
    public IReadOnlyList<ProfileEntry> Profiles => _index.Profiles;
    public ProfileEntry? Active => _index.Profiles.FirstOrDefault(p => p.Id == _index.Active) ?? _index.Profiles.FirstOrDefault();
    public bool NeedsSetup => _index.Profiles.Count == 0;

    public static ProfileStore NextToExecutable() => new ProfileStore(AppContext.BaseDirectory).Load();

    /// <summary>Reads profiles.json, adopting an existing statusboard.json as "Default"; drops entries whose files are gone.</summary>
    public ProfileStore Load()
    {
        var path = Path.Combine(Folder, IndexFile);
        try
        {
            _index = File.Exists(path) ? JsonSerializer.Deserialize<ProfileIndex>(File.ReadAllText(path), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { _index = new(); }
        _index.Profiles = (_index.Profiles ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.File) && IsPlainFileName(p.File)
            && File.Exists(Path.Combine(Folder, p.File))).GroupBy(p => p.File, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        foreach (var p in _index.Profiles)
        {
            p.Name = CleanName(p.Name);
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = NewId();
        }
        var legacy = Path.Combine(Folder, ConfigStore.FileName);
        if (File.Exists(legacy) && _index.Profiles.All(p => !p.File.Equals(ConfigStore.FileName, StringComparison.OrdinalIgnoreCase)))
            _index.Profiles.Insert(0, new ProfileEntry { Id = NewId(), Name = DefaultName, File = ConfigStore.FileName });
        if (Active is { } a) _index.Active = a.Id;
        if (_index.Profiles.Count > 0) TrySave();
        return this;
    }

    public ConfigStore StoreFor(ProfileEntry p) => new(Path.Combine(Folder, p.File));

    /// <summary>Adds a profile with the given settings and makes it active. The first one is stored as statusboard.json.</summary>
    public ProfileEntry Create(string name, AppConfig config)
    {
        var clean = CleanName(name);
        var file = _index.Profiles.Count == 0 && !File.Exists(Path.Combine(Folder, ConfigStore.FileName)) ? ConfigStore.FileName : UniqueFile(clean);
        var entry = new ProfileEntry { Id = NewId(), Name = UniqueName(clean), File = file };
        StoreFor(entry).Save(config); // throws IOException/UnauthorizedAccessException on a read-only folder
        _index.Profiles.Add(entry);
        _index.Active = entry.Id;
        Save();
        return entry;
    }

    public void SetActive(ProfileEntry p) { _index.Active = p.Id; TrySave(); }

    public void Rename(ProfileEntry p, string name)
    {
        var clean = CleanName(name);
        if (clean == p.Name) return;
        p.Name = UniqueName(clean, p);
        TrySave();
    }

    /// <summary>Removes a profile and its settings file. The last profile can't be deleted; deleting the active one activates another.</summary>
    public bool Delete(ProfileEntry p)
    {
        if (_index.Profiles.Count <= 1 || !_index.Profiles.Remove(p)) return false;
        try { File.Delete(Path.Combine(Folder, p.File)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (_index.Active == p.Id) _index.Active = _index.Profiles[0].Id;
        TrySave();
        return true;
    }

    public void Save()
    {
        var path = Path.Combine(Folder, IndexFile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_index, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public bool TrySave()
    {
        try { Save(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Trimmed, single line, at most 40 characters; "Default" if blank.</summary>
    public static string CleanName(string? name)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length > MaxNameLength) s = s[..MaxNameLength].TrimEnd();
        return s.Length == 0 ? DefaultName : s;
    }

    private string UniqueName(string name, ProfileEntry? except = null)
    {
        var taken = _index.Profiles.Where(p => p != except).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var i = 2; taken.Contains(candidate); i++) candidate = $"{name} ({i})";
        return candidate;
    }

    private string UniqueFile(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        if (slug.Length == 0) slug = "profile";
        string file;
        do file = $"profile-{slug}-{Guid.NewGuid().ToString("N")[..6]}.json"; while (File.Exists(Path.Combine(Folder, file)));
        return file;
    }

    /// <summary>Only a plain *.json name in the app folder (never a path or the index itself), so an edited profiles.json can't
    /// make Delete remove any other file.</summary>
    private static bool IsPlainFileName(string f) => f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && f == Path.GetFileName(f) && !f.StartsWith('.')
        && f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !f.Equals(IndexFile, StringComparison.OrdinalIgnoreCase);
    private static string NewId() => Guid.NewGuid().ToString("N")[..10];
}
