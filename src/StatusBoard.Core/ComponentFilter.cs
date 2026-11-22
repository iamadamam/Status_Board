namespace StatusBoard.Core;

/// <summary>
/// "Only the components we use": narrows a source's result to the selected components. Components outside the selection are
/// dropped; incidents count only if they affect a selected component, or don't say which components they affect (those are kept,
/// since they may well matter). The status is recomputed from what's left, so a page-wide "major outage" in a product you don't
/// use no longer turns the tile red.
/// </summary>
public static class ComponentFilter
{
    /// <summary>
    /// Whether a selected name refers to a component name as a source reports it. Pages are inconsistent: Statuspage incidents name
    /// "Australia" while components are "Cortex Cloud / Australia"; AWS incidents name "Amazon EC2" while components are
    /// "Amazon EC2 (London)". Both directions are accepted.
    /// </summary>
    public static bool Matches(string selected, string name)
    {
        if (string.Equals(selected, name, StringComparison.OrdinalIgnoreCase)) return true;
        static bool Under(string full, string part) =>
            full.EndsWith(" / " + part, StringComparison.OrdinalIgnoreCase) || full.StartsWith(part + " (", StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(part + " / ", StringComparison.OrdinalIgnoreCase);
        return Under(selected, name) || Under(name, selected);
    }

    public static SourceResult Apply(SourceResult result, IReadOnlyCollection<string>? selected)
    {
        if (selected is null || !result.Succeeded) return result;
        bool In(string name) => selected.Any(s => Matches(s, name));
        var components = result.Components.Where(c => In(c.Name)).ToList();
        var incidents = result.Incidents
            .Where(i => i.AffectedComponents.Count == 0 || i.AffectedComponents.Any(In))
            .Select(i => i.AffectedComponents.Count == 0 ? i : i with { AffectedComponents = i.AffectedComponents.Where(In).ToList() })
            .ToList();
        var status = components.Select(c => c.Status).Concat(incidents.Select(i => i.Status)).Worst();
        return result with { Components = components, Incidents = incidents, Status = status, Filtered = true };
    }
}
