using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

/// <summary>Salesforce Trust active incidents, filtered by instance.</summary>
public class SalesforceTests
{
    private static string ActiveCopy() => Samples.Text("salesforce-incidents-recent.json").Replace("\"status\":\"Resolved\"", "\"status\":\"Active\"");

    [Fact]
    public void No_active_incidents_is_operational()
    {
        var r = SalesforceProvider.Parse(Samples.Text("salesforce-incidents-active.json"), "Salesforce", null);
        Assert.Equal(ServiceStatus.Operational, r.Status);
    }

    [Fact]
    public void Resolved_incidents_are_ignored() =>
        Assert.Empty(SalesforceProvider.Parse(Samples.Text("salesforce-incidents-recent.json"), "Salesforce", null).Incidents);

    [Fact]
    public void All_instances_shows_everything_with_impact_based_status()
    {
        var r = SalesforceProvider.Parse(ActiveCopy(), "Salesforce", null);
        Assert.Equal(3, r.Incidents.Count);
        Assert.Equal(ServiceStatus.MajorOutage, r.Status); // serviceDisruption + major severity
        var deg = r.Incidents.Single(i => i.AffectedComponents.SequenceEqual(["IND138"]));
        Assert.Equal(ServiceStatus.Degraded, deg.Status);
        Assert.Equal("Degradation on IND138 (Core service)", deg.Title);
        Assert.Equal("https://status.salesforce.com/incidents/20004538", deg.Url);
        Assert.StartsWith("We’re investigating", deg.Summary);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T17:27:00Z"), deg.Started);
    }

    [Fact]
    public void Instance_filter_keeps_only_your_instances()
    {
        var r = SalesforceProvider.Parse(ActiveCopy(), "Salesforce", SalesforceProvider.ParseInstances("gbr2s, nope1"));
        Assert.Equal(2, r.Incidents.Count);
        Assert.All(r.Incidents, i => Assert.Equal(["GBR2S"], i.AffectedComponents));
        Assert.Equal(["GBR2S"], r.Components.Select(c => c.Name));

        Assert.Empty(SalesforceProvider.Parse(ActiveCopy(), "Salesforce", SalesforceProvider.ParseInstances("EU45")).Incidents);
        Assert.Null(SalesforceProvider.ParseInstances("  "));
    }
}

/// <summary>Salesforce Trust per-product status (Tableau, MuleSoft, …) via /v1/products/{key}.</summary>
public class SalesforceProductTests
{
    [Fact]
    public void Live_Tableau_sample_lists_its_instances()
    {
        var r = SalesforceProvider.ParseProduct(Samples.Text("salesforce-product-tableau.json"), "Tableau", null);
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.True(r.Components.Count >= 10, $"{r.Components.Count} instances");
        Assert.Contains(r.Components, c => c.Name.StartsWith("10AYPD"));
    }

    private const string Incident = """
        {"id":42,"status":"Active","type":"Degradation","instanceKeys":["PDUKAA"],"serviceKeys":["coreService"],"affectsAll":false,
         "IncidentImpacts":[{"startTime":"2026-11-19T10:00:00.000Z","endTime":null,"type":"performanceDegradation","severity":"major"}],
         "IncidentEvents":[{"createdAt":"2026-11-19T10:05:00.000Z","message":"Dashboards are slow to load for some users."}],
         "createdAt":"2026-11-19T10:00:00.000Z","updatedAt":"2026-11-19T10:05:00.000Z"}
        """;

    private static string Product(string ukStatus, string usStatus, bool withIncident) => $$"""
        {"key":"Tableau","name":"Tableau","Instances":[
          {"key":"PDUKAA","location":"EMEA","isActive":true,"status":"{{ukStatus}}","Incidents":[{{(withIncident ? Incident : "")}}]},
          {"key":"PDUEAA","location":"NA","isActive":true,"status":"{{usStatus}}","Incidents":[{{(withIncident ? Incident : "")}}]},
          {"key":"OLDPOD","location":"NA","isActive":false,"status":"MAJOR_INCIDENT_CORE","Incidents":[]}]}
        """;

    [Fact]
    public void One_instance_in_trouble_is_a_partial_outage_with_its_incident()
    {
        var r = SalesforceProvider.ParseProduct(Product("MAJOR_INCIDENT_CORE", "OK", withIncident: true), "Tableau", null);
        Assert.Equal(ServiceStatus.PartialOutage, r.Status);
        Assert.Equal(2, r.Components.Count); // inactive pod ignored
        Assert.Contains(r.Components, c => c.Name == "PDUKAA (EMEA)" && c.Status == ServiceStatus.PartialOutage);
        var i = Assert.Single(r.Incidents); // listed under two instances, reported once
        Assert.Equal("Dashboards are slow to load for some users.", i.Summary);
        Assert.Contains("PDUKAA", i.Title);
        Assert.Equal("https://status.salesforce.com/incidents/42", i.Url);
    }

    [Fact]
    public void Every_instance_down_is_a_major_outage_and_maintenance_is_maintenance()
    {
        Assert.Equal(ServiceStatus.MajorOutage, SalesforceProvider.ParseProduct(Product("MAJOR_INCIDENT_CORE", "MAJOR_INCIDENT_NONCORE", false), "T", null).Status);
        Assert.Equal(ServiceStatus.Maintenance, SalesforceProvider.ParseProduct(Product("MAINTENANCE_CORE", "OK", false), "T", null).Status);
        Assert.Equal(ServiceStatus.Degraded, SalesforceProvider.ParseProduct(Product("MINOR_INCIDENT_CORE", "OK", false), "T", null).Status);
    }

    [Fact]
    public void Instance_filter_limits_to_your_pods()
    {
        var r = SalesforceProvider.ParseProduct(Product("MAJOR_INCIDENT_CORE", "OK", true), "Tableau", SalesforceProvider.ParseInstances("pduea"));
        Assert.Empty(r.Components); // "pduea" isn't a full key
        r = SalesforceProvider.ParseProduct(Product("MAJOR_INCIDENT_CORE", "OK", true), "Tableau", SalesforceProvider.ParseInstances("pdueaa"));
        Assert.Equal(ServiceStatus.Operational, Assert.Single(r.Components).Status);
    }

    [Theory]
    [InlineData("https://status.salesforce.com/products/Tableau", "Tableau")]
    [InlineData("https://api.status.salesforce.com/v1/products/Mulesoft", "Mulesoft")]
    [InlineData("https://status.salesforce.com/products/Marketing_Cloud?x=1", "Marketing_Cloud")]
    [InlineData("https://api.status.salesforce.com/v1/incidents/active", null)]
    [InlineData("https://example.com/products/Tableau", null)]
    public void Recognises_product_links(string url, string? key) => Assert.Equal(key, SalesforceProvider.ProductKey(url));

    [Fact]
    public void Garbage_is_a_parse_error() =>
        Assert.Throws<ParseException>(() => SalesforceProvider.ParseProduct("[]", "T", null));

    [Fact]
    public void Tableau_is_built_in()
    {
        var t = DefaultVendors.Create().Single(v => v.Id == "tableau");
        Assert.Equal("https://status.salesforce.com/products/Tableau", t.StatusPageUrl);
        Assert.Equal(SalesforceProvider.ProductApiBase + "Tableau", Assert.Single(t.Sources).Url);
        Assert.True(t.Enabled);
    }
}

public class SalesforceProductLiveTests
{
    /// <summary>Opt-in (STATUSBOARD_LIVE=1): pasting a product link into "Add vendor" finds it.</summary>
    [Theory]
    [InlineData("https://status.salesforce.com/products/Tableau", "Tableau")]
    [InlineData("status.salesforce.com/products/Mulesoft", "MuleSoft")]
    public async Task Add_vendor_detects_a_Salesforce_product(string link, string name)
    {
        if (Environment.GetEnvironmentVariable("STATUSBOARD_LIVE") != "1") return;
        using var http = new HttpFetcher();
        var r = await VendorDetector.DetectAsync(link, http, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal("salesforce", r.Type);
        Assert.Equal(name, r.SuggestedName);
        Assert.StartsWith(SalesforceProvider.ProductApiBase, r.SourceUrl);
    }
}
