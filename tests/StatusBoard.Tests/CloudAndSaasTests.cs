using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

/// <summary>Google (Workspace / Cloud), Slack, Apple, Stripe and PayPal adapters.</summary>
public class CloudAndSaasTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);

    // ---- Google (Workspace / Cloud) ----
    [Fact]
    public void Google_history_with_only_ended_incidents_is_operational()
    {
        var r = GoogleStatusProvider.Parse(Samples.Text("google-workspace-incidents.json"), "Workspace", "https://www.google.com/appsstatus/dashboard/", Now);
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
    }

    [Fact]
    public void Google_open_incident_is_current_with_products_and_link()
    {
        const string json = """
        [{"id":"abc","begin":"2026-11-19T16:00:00+00:00","modified":"2026-11-19T17:00:00+00:00",
          "external_desc":"**Summary:**\nGmail users may see delays sending mail",
          "most_recent_update":{"when":"2026-11-19T17:00:00+00:00","text":"**Description:**\nWe are investigating.","status":"SERVICE_DISRUPTION"},
          "status_impact":"SERVICE_OUTAGE","severity":"high","affected_products":[{"title":"Gmail","id":"x"}],"uri":"incidents/abc"},
         {"id":"old","begin":"2026-11-01T10:00:00+00:00","end":"2026-11-01T11:00:00+00:00","status_impact":"SERVICE_OUTAGE","severity":"high"}]
        """;
        var r = GoogleStatusProvider.Parse(json, "Workspace", "https://www.google.com/appsstatus/dashboard/", Now);
        var i = Assert.Single(r.Incidents);
        Assert.Equal(ServiceStatus.MajorOutage, r.Status);
        Assert.Equal("Gmail users may see delays sending mail", i.Title);
        Assert.Equal("https://www.google.com/appsstatus/dashboard/incidents/abc", i.Url);
        Assert.Equal("Description:\nWe are investigating.", i.Summary);
        Assert.Contains(r.Components, c => c.Name == "Gmail" && c.Status == ServiceStatus.MajorOutage);
    }

    [Fact]
    public void Google_information_notice_is_degraded_not_an_outage()
    {
        const string json = """[{"begin":"2026-11-19T16:00:00+00:00","external_desc":"Known issue","status_impact":"SERVICE_INFORMATION","severity":"low","most_recent_update":{"status":"SERVICE_INFORMATION"}}]""";
        Assert.Equal(ServiceStatus.Degraded, GoogleStatusProvider.Parse(json, "x", "https://x.test/", Now).Status);
    }

    // ---- Slack ----
    [Fact]
    public void Slack_ok_is_operational() =>
        Assert.Equal(ServiceStatus.Operational, SlackStatusProvider.Parse(Samples.Text("slack-current.json"), "Slack").Status);

    [Fact]
    public void Slack_active_outage_maps_services_and_latest_note()
    {
        const string json = """
        {"status":"active","active_incidents":[{"id":1,"title":"Messages failing to send","type":"outage","status":"active",
          "url":"https://slack-status.com/2026-11/abc","date_created":"2026-11-19T10:00:00-08:00","date_updated":"2026-11-19T10:30:00-08:00",
          "services":["Messaging","Connectivity"],"notes":[{"date_created":"2026-11-19T10:05:00-08:00","body":"Investigating"},
          {"date_created":"2026-11-19T10:30:00-08:00","body":"<p>Fix rolling out</p>"}]}]}
        """;
        var r = SlackStatusProvider.Parse(json, "Slack");
        Assert.Equal(ServiceStatus.MajorOutage, r.Status);
        var i = Assert.Single(r.Incidents);
        Assert.Equal("Fix rolling out", i.Summary);
        Assert.Equal(2, r.Components.Count);
    }

    [Fact]
    public void Slack_rejects_unrelated_json() =>
        Assert.Throws<ParseException>(() => SlackStatusProvider.Parse("""{"page":{}}""", "Slack"));

    // ---- Apple ----
    [Fact]
    public void Apple_resolved_events_are_history()
    {
        var r = AppleStatusProvider.Parse(Samples.Text("apple-system-status.js"), "Apple", Now);
        Assert.Equal(ServiceStatus.Operational, r.Status);
        Assert.Empty(r.Incidents);
        Assert.True(r.Components.Count > 20, $"{r.Components.Count} services");
    }

    [Fact]
    public void Apple_ongoing_outage_in_jsonp_wrapper_is_partial_outage()
    {
        const string js = """
        jsonCallback({"services":[{"serviceName":"iCloud Mail","events":[{"statusType":"Outage","eventStatus":"ongoing",
          "message":"Users may be unable to send mail.","usersAffected":"Some users are affected","epochStartDate":1795100000000,"epochEndDate":null}]},
          {"serviceName":"App Store","events":[]}]});
        """;
        var r = AppleStatusProvider.Parse(js, "Apple", Now);
        Assert.Equal(ServiceStatus.PartialOutage, r.Status);
        var i = Assert.Single(r.Incidents);
        Assert.Equal("iCloud Mail: Outage", i.Title);
        Assert.Contains(r.Components, c => c.Name == "App Store" && c.Status == ServiceStatus.Operational);
    }

    // ---- Stripe ----
    [Fact]
    public void Stripe_all_up_is_operational() =>
        Assert.Equal(ServiceStatus.Operational, StripeStatusProvider.Parse(Samples.Text("stripe-current.json"), "Stripe").Status);

    [Fact]
    public void Stripe_degraded_component_raises_an_incident()
    {
        var r = StripeStatusProvider.Parse("""{"statuses":{"api":"up","webhooks":"degraded"},"largestatus":"degraded","message":"Webhooks are delayed."}""", "Stripe");
        Assert.Equal(ServiceStatus.Degraded, r.Status);
        Assert.Equal("Webhooks are delayed.", Assert.Single(r.Incidents).Title);
        Assert.Contains(r.Components, c => c.Name == "Webhooks" && c.Status == ServiceStatus.Degraded);
    }

    // ---- PayPal ----
    [Fact]
    public void PayPal_live_sample_parses_components()
    {
        var r = PayPalStatusProvider.Parse(Samples.Text("paypal-components.json"), "PayPal");
        Assert.True(r.Components.Count > 10, $"{r.Components.Count} components");
        Assert.NotEqual(ServiceStatus.Unknown, r.Status);
    }

    [Fact]
    public void PayPal_major_outage_in_production_only()
    {
        const string json = """
        {"result":[{"name":"Online Checkout","status":{"production":"Major Outage","sandbox":"Operational"}},
                   {"name":"REST APIs","status":{"production":"Operational","sandbox":"Major Outage"}}]}
        """;
        var r = PayPalStatusProvider.Parse(json, "PayPal");
        Assert.Equal(ServiceStatus.MajorOutage, r.Status);
        Assert.Equal("Online Checkout: Major outage", Assert.Single(r.Incidents).Title);
    }
}
