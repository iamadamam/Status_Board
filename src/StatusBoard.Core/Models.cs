namespace StatusBoard.Core;

/// <summary>Normalised status. Numeric order is severity order for "worst of"; <see cref="Unknown"/> means "couldn't tell".</summary>
public enum ServiceStatus { Operational = 0, Maintenance = 1, Degraded = 2, PartialOutage = 3, MajorOutage = 4, Unknown = 99 }

public static class ServiceStatusExtensions
{
    /// <summary>The more severe of two statuses; Unknown never wins over a known one.</summary>
    public static ServiceStatus Worst(this ServiceStatus a, ServiceStatus b) =>
        a == ServiceStatus.Unknown ? b : b == ServiceStatus.Unknown ? a : (ServiceStatus)Math.Max((int)a, (int)b);

    public static ServiceStatus Worst(this IEnumerable<ServiceStatus> statuses, ServiceStatus seed = ServiceStatus.Operational) =>
        statuses.Aggregate(seed, Worst);

    public static bool IsProblem(this ServiceStatus s) => s is ServiceStatus.Degraded or ServiceStatus.PartialOutage or ServiceStatus.MajorOutage;

    public static string ToDisplay(this ServiceStatus s) => s switch
    {
        ServiceStatus.Operational => "Operational",
        ServiceStatus.Maintenance => "Maintenance",
        ServiceStatus.Degraded => "Degraded",
        ServiceStatus.PartialOutage => "Partial outage",
        ServiceStatus.MajorOutage => "Major outage",
        _ => "Unknown",
    };
}

/// <summary>
/// Where an incident or maintenance is in its life, as vendors word it. Separate from <see cref="ServiceStatus"/> (how bad):
/// "Major outage · Investigating" and "Major outage · Monitoring" are very different moments.
/// </summary>
public enum IncidentStage
{
    /// <summary>The source doesn't say.</summary>
    None = 0,
    /// <summary>Problem reported, cause not yet known.</summary>
    Investigating,
    /// <summary>Cause found, fix being worked on.</summary>
    Identified,
    /// <summary>A new update without a change of stage.</summary>
    Update,
    /// <summary>Fix applied, being watched.</summary>
    Monitoring,
    /// <summary>A notice or advisory rather than an outage.</summary>
    Notice,
    /// <summary>Planned work not started yet.</summary>
    Scheduled,
    /// <summary>Planned work happening now.</summary>
    InProgress,
    /// <summary>Planned work done, being checked.</summary>
    Verifying,
}

public static class IncidentStages
{
    public static string? ToDisplay(this IncidentStage s) => s switch
    {
        IncidentStage.Investigating => "Investigating", IncidentStage.Identified => "Identified", IncidentStage.Update => "Update",
        IncidentStage.Monitoring => "Monitoring", IncidentStage.Notice => "Notice", IncidentStage.Scheduled => "Scheduled",
        IncidentStage.InProgress => "In progress", IncidentStage.Verifying => "Verifying", _ => null,
    };

    /// <summary>A fix or the planned work is done and being checked: the end is in sight.</summary>
    public static bool IsRecovering(this IncidentStage s) => s is IncidentStage.Monitoring or IncidentStage.Verifying;

    /// <summary>How early in the incident's life: the least-progressed stage is the one worth showing for a vendor.</summary>
    public static int Urgency(this IncidentStage s) => s switch
    {
        IncidentStage.Investigating => 0, IncidentStage.Identified => 1, IncidentStage.Update => 2, IncidentStage.InProgress => 3,
        IncidentStage.Monitoring => 4, IncidentStage.Verifying => 5, IncidentStage.Notice => 6, IncidentStage.Scheduled => 7, _ => 9,
    };

    /// <summary>A vendor's status word ("investigating", "in_progress", "In Progress", "verifying"…); None if it isn't one.</summary>
    public static IncidentStage FromWord(string? word)
    {
        var w = new string((word ?? "").Trim().ToLowerInvariant().Where(char.IsLetter).ToArray());
        return w switch
        {
            "investigating" or "investigation" or "open" or "new" or "reported" => IncidentStage.Investigating,
            "identified" or "acknowledged" or "fixing" or "fixinprogress" or "mitigating" or "mitigation" => IncidentStage.Identified,
            "update" or "updated" or "inprogressupdate" => IncidentStage.Update,
            "monitoring" or "recovering" or "recovery" or "restoring" or "mitigated" or "fixdeployed" => IncidentStage.Monitoring,
            "notice" or "informational" or "information" or "info" or "advisory" => IncidentStage.Notice,
            "scheduled" or "planned" or "upcoming" => IncidentStage.Scheduled,
            "inprogress" or "ongoing" or "started" or "underway" or "active" => IncidentStage.InProgress,
            "verifying" or "verification" => IncidentStage.Verifying,
            _ => IncidentStage.None,
        };
    }

    /// <summary>
    /// The stage from an update's leading word, as many status pages write them ("Monitoring - A fix has been…",
    /// "[Identified] …", "Update: …"). Only a word at the very start followed by a separator counts, so ordinary prose
    /// ("We are investigating…") isn't misread.
    /// </summary>
    public static IncidentStage FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return IncidentStage.None;
        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"^\s*\[?\s*(Investigating|Identified|Update|Monitoring|Scheduled|In progress|Verifying)\s*\]?\s*(?:[-–—:|]|\]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? FromWord(m.Groups[1].Value) : IncidentStage.None;
    }
}

/// <summary>A component or sub-service reported by a source.</summary>
public sealed record ComponentStatus(string Name, ServiceStatus Status);

/// <summary>An active incident or in-progress maintenance, from any source.</summary>
public sealed record Incident
{
    public required string Title { get; init; }
    public string? Summary { get; init; }
    public string? Url { get; init; }
    public ServiceStatus Status { get; init; } = ServiceStatus.Degraded;
    /// <summary>Investigating, Monitoring, In progress… (None when the source doesn't say).</summary>
    public IncidentStage Stage { get; init; }
    public DateTimeOffset? Started { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
    public IReadOnlyList<string> AffectedComponents { get; init; } = [];
}

/// <summary>What one source said. Never thrown: failures become <see cref="Error"/>.</summary>
public sealed record SourceResult
{
    public required string SourceLabel { get; init; }
    public ServiceStatus Status { get; init; } = ServiceStatus.Unknown;
    public IReadOnlyList<ComponentStatus> Components { get; init; } = [];
    public IReadOnlyList<Incident> Incidents { get; init; } = [];
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>Null when the source was reached and understood; otherwise a short reason.</summary>
    public string? Error { get; init; }
    /// <summary>A one-line reading to show when nothing is wrong (website/ping checks: "Up · 182 ms").</summary>
    public string? Note { get; init; }
    /// <summary>Components were filtered to the user's selection (<see cref="SourceConfig.Components"/>).</summary>
    public bool Filtered { get; init; }
    public bool Succeeded => Error is null;

    public static SourceResult Failed(string label, string error) => new() { SourceLabel = label, Error = error };

    /// <summary>A successful result; status defaults to the worst incident (adapters with an overall indicator pass it in).</summary>
    public static SourceResult Of(string label, IReadOnlyList<Incident> incidents, IReadOnlyList<ComponentStatus>? components = null,
        ServiceStatus? status = null, DateTimeOffset? checkedAt = null) => new()
    {
        SourceLabel = label,
        // Sources that don't give a stage often start their updates with one ("Monitoring - …"): use it when present.
        Incidents = incidents.Select(i => i.Stage != IncidentStage.None ? i
            : IncidentStages.FromText(i.Summary) is var s && s != IncidentStage.None ? i with { Stage = s }
            : IncidentStages.FromText(i.Title) is var t && t != IncidentStage.None ? i with { Stage = t }
            : i.Status == ServiceStatus.Maintenance ? i with { Stage = IncidentStage.InProgress } : i).ToList(),
        Components = components ?? [],
        Status = status ?? incidents.Select(i => i.Status).Worst(),
        CheckedAt = checkedAt ?? DateTimeOffset.Now,
    };
}

/// <summary>One vendor rolled up across its enabled sources.</summary>
public sealed record VendorStatus
{
    public required string VendorId { get; init; }
    public required string VendorName { get; init; }
    public string? StatusPageUrl { get; init; }
    public required IReadOnlyList<SourceResult> Sources { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Worst status among sources that succeeded; Unknown if none did.</summary>
    public ServiceStatus Status => Sources.Any(s => s.Succeeded) ? Sources.Where(s => s.Succeeded).Select(s => s.Status).Worst() : ServiceStatus.Unknown;

    public int FailedSourceCount => Sources.Count(s => !s.Succeeded);

    public IEnumerable<Incident> Incidents => Sources.SelectMany(s => s.Incidents);

    /// <summary>
    /// The stage to show beside the vendor's status: the least progressed of the incidents at that status. A problem raised by
    /// components alone borrows from other problem incidents, never from maintenance ("Degraded · In progress" would be misleading).
    /// </summary>
    public IncidentStage Stage
    {
        get
        {
            var all = Sources.Where(s => s.Succeeded).SelectMany(s => s.Incidents).Where(i => i.Stage != IncidentStage.None).ToList();
            var status = Status;
            var match = all.Where(i => i.Status == status).ToList();
            if (match.Count == 0 && status.IsProblem()) match = all.Where(i => i.Status.IsProblem()).ToList();
            return match.Select(i => i.Stage).OrderBy(IncidentStages.Urgency).FirstOrDefault();
        }
    }

    /// <summary>Non-operational components, labelled with their source when the vendor has several.</summary>
    public IEnumerable<ComponentStatus> AffectedComponents => Sources.SelectMany(s =>
        s.Components.Where(c => c.Status != ServiceStatus.Operational)
                    .Select(c => Sources.Count > 1 ? c with { Name = $"{s.SourceLabel} · {c.Name}" } : c));
}
