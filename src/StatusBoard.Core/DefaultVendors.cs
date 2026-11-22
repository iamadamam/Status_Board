using static StatusBoard.Core.VendorCategories;

namespace StatusBoard.Core;

/// <summary>
/// The built-in vendors. Every endpoint was checked live (2026-11-19). The most widely used are on by default;
/// the rest are listed in Settings, switched off. Order matters: it's the order of statusboard.json and of the grid.
/// </summary>
public static class DefaultVendors
{
    /// <summary>Bump when built-ins are added in a release; existing configs gain the new ones on load (see <see cref="Upgrade"/>).</summary>
    public const int Version = 3;

    /// <summary>AWS regions shown by default: global services, UK (London) and the four US commercial regions.</summary>
    public const string DefaultAwsRegions = "global,eu-west-2,us-east-1,us-east-2,us-west-1,us-west-2";

    /// <summary>The default selection ("Default" in setup and the Vendors pane): the built-ins that are on in a new board.</summary>
    public static IReadOnlySet<string> DefaultIds => _defaultIds ??= Create().Where(v => v.Enabled).Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string>? _defaultIds;

    public static List<VendorConfig> Create() =>
    [
        // ---- Cloud, productivity and IT essentials ----
        V("azure", "Microsoft Azure", Cloud, "https://azure.status.microsoft/en-us/status/", "https://azure.microsoft.com/favicon.ico",
            // Every item in this feed is a current incident; the feed is empty when all is well.
            S("feed", "Azure incidents", "https://rssfeed.azure.status.microsoft/en-us/status/feed/", ("mode", "active"), ("itemStatus", "Degraded")),
            S("microsoft", "Azure overall", "https://status.cloud.microsoft/api/posts/azure")),
        V("m365", "Microsoft 365", Productivity, "https://status.cloud.microsoft/", "https://www.microsoft.com/favicon.ico",
            S("microsoft", "Admin center", "https://status.cloud.microsoft/api/posts/mac"),
            S("microsoft", "Consumer services", "https://status.cloud.microsoft/api/posts/m365Consumer"),
            S("microsoft", "Power Platform admin center", "https://status.cloud.microsoft/api/posts/ppac")),
        V("aws", "Amazon Web Services", Cloud, "https://health.aws.amazon.com/health/status", "https://a0.awsstatic.com/libra-css/images/site/touch-icon-ipad-144-smile.png",
            S("aws-health", "AWS Health Dashboard", "https://health.aws.amazon.com/public/currentevents", ("regions", DefaultAwsRegions))),
        // FortiCloud's hub has no components of its own; each product has its own Statuspage.
        V("forticloud", "FortiCloud", Security, "https://status.forticloud.com/", null, Pages(
            "Status Hub|status.forticloud.com", "FortiAnalyzer Cloud|status.fortianalyzer.forticloud.com", "FortiCare Elite|status.forticareelite.forticloud.com",
            "FortiClient Cloud|status.forticlient.forticloud.com", "FortiDeceptor DaaS|status.daas.forticloud.com", "FortiEdge Cloud|status.fortiedge.forticloud.com",
            "FortiManagement Cloud|status.management.forticloud.com", "FortiManager Cloud|status.fortimanager.forticloud.com",
            "FortiSandbox Cloud|status.fortisandboxcloud.forticloud.com", "FortiSIEM Cloud|status.fortisiem.forticloud.com", "FortiSOAR Cloud|status.fortisoar.forticloud.com",
            "FortiSOC Cloud|status.fortisoc.forticloud.com", "FortiTIP Cloud|status.fortitip.forticloud.com", "FortiZTP|status.fortiztp.forticloud.com",
            "MFGS Cloud|status.mfgs.forticloud.com", "SOCaaS|status.socaas.forticloud.com")),
        Sp("cloudflare", "Cloudflare", "https://www.cloudflarestatus.com", Cloud),
        Sio("mimecast", "Mimecast", "https://status.mimecast.com/", "5d849b1c02e65b3ec45369d4", Security),
        Sio("connectwise", "ConnectWise", "https://status.connectwise.com/", "619cf82551fec9053d612f09", ItManagement),
        Sp("github", "GitHub", "https://www.githubstatus.com", Developer),
        Sp("twilio", "Twilio", "https://status.twilio.com", Communications),
        // Atlassian's hub has no components; each product has its own Statuspage ("-" = off by default). Jira is separate.
        V("atlassian", "Atlassian", Productivity, "https://status.atlassian.com/", null, Pages(
            "Status Hub|status.atlassian.com", "Confluence|confluence.status.atlassian.com", "Bitbucket|status.bitbucket.org", "Trello|www.trellostatus.com",
            "Loom|loom.status.atlassian.com", "Rovo|rovo.status.atlassian.com", "Guard (Access)|guard.status.atlassian.com", "Administration|admin.status.atlassian.com",
            "Opsgenie|status.opsgenie.com", "Statuspage|metastatuspage.com", "Atlassian Support|support.status.atlassian.com",
            "-Compass|compass.status.atlassian.com", "-Atlas|atlas.status.atlassian.com", "-Atlassian Analytics|analytics.status.atlassian.com",
            "-Rovo Dev|rovodev.status.atlassian.com", "-Customer Service Management|customer-service-management.status.atlassian.com",
            "-Focus|focus.status.atlassian.com", "-Talent|talent.status.atlassian.com", "-Feedback|feedback.status.atlassian.com",
            "-Migrations|migrations.status.atlassian.com", "-Developer|status.developer.atlassian.com", "-Partners|partners.status.atlassian.com")),
        V("jira", "Jira", Productivity, "https://jira-software.status.atlassian.com/", null, Pages(
            "Jira|jira-software.status.atlassian.com", "Jira Service Management|jira-service-management.status.atlassian.com",
            "Jira Work Management|jira-work-management.status.atlassian.com", "Jira Product Discovery|jira-product-discovery.status.atlassian.com",
            "Jira Align|jira-align.status.atlassian.com")),
        // Blank instances = all; set yours (e.g. "EU45") in Settings to ignore other customers' instances.
        V("salesforce", "Salesforce", Business, "https://status.salesforce.com/", null,
            S("salesforce", "Salesforce Trust", SalesforceProvider.ActiveIncidentsUrl, ("instances", ""))),
        Sio("gitlab", "GitLab", "https://status.gitlab.com/", "5b36dc6502d06804c08349f7", Developer),
        Sp("docker", "Docker", "https://www.dockerstatus.com", Developer),
        // Datadog blocks its Statuspage JSON (HTTP 403), so each site's history feed is used; US1 and EU1 on by default.
        V("datadog", "Datadog", Developer, "https://status.datadoghq.com/", "https://www.datadoghq.com/favicon.ico",
            [.. new[] { ("US1", "status.datadoghq.com", true), ("EU1", "status.datadoghq.eu", true), ("US3", "status.us3.datadoghq.com", false),
                        ("US5", "status.us5.datadoghq.com", false), ("AP1", "status.ap1.datadoghq.com", false) }
                .Select(d => Recent(S("feed", d.Item1, $"https://{d.Item2}/history.atom"), on: d.Item3))]),
        Sp("anthropic", "Anthropic", "https://status.claude.com", Developer),
        Sp("openai", "OpenAI", "https://status.openai.com", Developer),
        Sp("rippling", "Rippling", "https://status.rippling.com", Business),
        // Hund.io page: no JSON API without a key, but a public state feed.
        V("bitwarden", "Bitwarden", Security, "https://status.bitwarden.com/", null, Recent(S("feed", "Bitwarden status feed", "https://status.bitwarden.com/state_feed/feed"))),
        Sp("figma", "Figma", "https://status.figma.com", Productivity),

        // ---- More cloud, SaaS and consumer services ----
        V("googlecloud", "Google Cloud", Cloud, "https://status.cloud.google.com/", "https://www.gstatic.com/images/branding/product/2x/google_cloud_64dp.png",
            S("google", "Google Cloud", "https://status.cloud.google.com/incidents.json", ("link", "https://status.cloud.google.com/"))),
        Sp("akamai", "Akamai", "https://www.akamaistatus.com", Cloud),
        Sp("digitalocean", "DigitalOcean", "https://status.digitalocean.com", Cloud),
        Sio("letsencrypt", "Let's Encrypt", "https://letsencrypt.status.io/", "55957a99e800baa4470002da", Cloud),
        Sp("godaddy", "GoDaddy", "https://status.godaddy.com", Cloud),
        Sp("linode", "Akamai Cloud (Linode)", "https://status.linode.com", Cloud, on: false),
        Sp("vercel", "Vercel", "https://www.vercel-status.com", Cloud, on: false),
        Sp("netlify", "Netlify", "https://www.netlifystatus.com", Cloud, on: false),
        Sp("flyio", "Fly.io", "https://status.flyio.net", Cloud, on: false),
        Sp("render", "Render", "https://status.render.com", Cloud, on: false),
        Sp("supabase", "Supabase", "https://status.supabase.com", Cloud, on: false),
        Sp("snowflake", "Snowflake", "https://status.snowflake.com", Cloud, on: false),
        Sp("mongodb", "MongoDB Atlas", "https://status.mongodb.com", Cloud, on: false),
        Sp("elastic", "Elastic Cloud", "https://status.elastic.co", Cloud, on: false),
        Sp("wasabi", "Wasabi", "https://status.wasabi.com", Cloud, on: false),
        Sp("scaleway", "Scaleway", "https://status.scaleway.com", Cloud, on: false),
        Sp("digicert", "DigiCert", "https://status.digicert.com", Cloud, on: false),
        Sp("globalsign", "GlobalSign", "https://status.globalsign.com", Cloud, on: false),

        V("googleworkspace", "Google Workspace", Productivity, "https://www.google.com/appsstatus/dashboard/", "https://www.google.com/favicon.ico",
            S("google", "Google Workspace", "https://www.google.com/appsstatus/dashboard/incidents.json", ("link", "https://www.google.com/appsstatus/dashboard/"))),
        V("slack", "Slack", Productivity, "https://slack-status.com/", "https://a.slack-edge.com/80588/marketing/img/meta/app-256.png",
            S("slack", "Slack", SlackStatusProvider.CurrentUrl)),
        Sp("zoom", "Zoom", "https://www.zoomstatus.com", Productivity),
        Sp("dropbox", "Dropbox", "https://status.dropbox.com", Productivity),
        Sp("box", "Box", "https://status.box.com", Productivity),
        Sp("notion", "Notion", "https://www.notion-status.com", Productivity),
        Sp("miro", "Miro", "https://status.miro.com", Productivity),
        Sp("canva", "Canva", "https://www.canvastatus.com", Productivity, logo: "https://static.canva.com/static/images/apple-touch-120x120-1.png"),
        Sp("egnyte", "Egnyte", "https://status.egnyte.com", Productivity, on: false),
        Sp("asana", "Asana", "https://status.asana.com", Productivity, on: false),
        Sp("monday", "monday.com", "https://status.monday.com", Productivity, on: false),
        Sp("clickup", "ClickUp", "https://status.clickup.com", Productivity, on: false),
        Sp("smartsheet", "Smartsheet", "https://status.smartsheet.com", Productivity, on: false),
        Sp("airtable", "Airtable", "https://status.airtable.com", Productivity, on: false),
        Sp("lucid", "Lucid", "https://status.lucid.co", Productivity, on: false),
        Sp("mural", "Mural", "https://status.mural.co", Productivity, on: false),
        Sp("calendly", "Calendly", "https://calendlystatus.com", Productivity, on: false),
        Sp("grammarly", "Grammarly", "https://status.grammarly.com", Productivity, on: false),
        Sp("linear", "Linear", "https://linearstatus.com", Productivity, on: false),
        Sp("webflow", "Webflow", "https://status.webflow.com", Productivity, on: false),
        Sp("typeform", "Typeform", "https://status.typeform.com", Productivity, on: false),
        Sp("surveymonkey", "SurveyMonkey", "https://status.surveymonkey.com", Productivity, on: false),
        Sp("pandadoc", "PandaDoc", "https://status.pandadoc.com", Productivity, on: false),

        Sp("duo", "Cisco Duo", "https://status.duo.com", Security),
        Sp("1password", "1Password", "https://status.1password.com", Security),
        Sp("paloalto", "Palo Alto Networks", "https://status.paloaltonetworks.com", Security),
        Sp("lastpass", "LastPass", "https://status.lastpass.com", Security, on: false, logo: "https://www.lastpass.com/favicon.ico"),
        Sp("pingidentity", "Ping Identity", "https://status.pingidentity.com", Security, on: false),
        Sp("jumpcloud", "JumpCloud", "https://status.jumpcloud.com", Security, on: false),
        Sp("yubico", "Yubico", "https://status.yubico.com", Security, on: false),
        Sp("barracuda", "Barracuda", "https://status.barracuda.com", Security, on: false),
        Sp("knowbe4", "KnowBe4", "https://status.knowbe4.com", Security, on: false),
        Sp("tailscale", "Tailscale", "https://status.tailscale.com", Security, on: false),
        // No JSON API; the incident feed is large (~1 MB), so off by default.
        Off(V("zscaler", "Zscaler", Security, "https://trust.zscaler.com/", null, Recent(S("feed", "Zscaler Trust", "https://trust.zscaler.com/rss-feed"), hours: "12"))),

        Sp("kaseya", "Kaseya & Datto", "https://status.kaseya.com", ItManagement),
        Sp("ninjaone", "NinjaOne", "https://status.ninjaone.com", ItManagement),
        Sp("teamviewer", "TeamViewer", "https://status.teamviewer.com", ItManagement),
        Sp("meraki", "Cisco Meraki", "https://status.meraki.net", ItManagement),
        Sp("splashtop", "Splashtop", "https://status.splashtop.com", ItManagement, on: false),
        Sp("goto", "GoTo", "https://status.goto.com", ItManagement, on: false),
        Sp("jamf", "Jamf", "https://status.jamf.com", ItManagement, on: false),
        Sp("kandji", "Kandji", "https://status.kandji.io", ItManagement, on: false),
        Sp("ubiquiti", "Ubiquiti", "https://status.ui.com", ItManagement, on: false),

        Sp("dialpad", "Dialpad", "https://status.dialpad.com", Communications, on: false),
        Sp("aircall", "Aircall", "https://status.aircall.com", Communications, on: false),
        Sp("talkdesk", "Talkdesk", "https://status.talkdesk.com", Communications, on: false),
        Sp("bandwidth", "Bandwidth", "https://status.bandwidth.com", Communications, on: false),
        Sp("telnyx", "Telnyx", "https://status.telnyx.com", Communications, on: false),
        Sp("sinch", "Sinch", "https://status.sinch.com", Communications, on: false),
        Sp("mailgun", "Mailgun", "https://status.mailgun.com", Communications, on: false),

        Sp("newrelic", "New Relic", "https://status.newrelic.com", Developer, on: false),
        Sp("sentry", "Sentry", "https://status.sentry.io", Developer, on: false),
        Sp("circleci", "CircleCI", "https://status.circleci.com", Developer, on: false),
        Sp("npm", "npm", "https://status.npmjs.org", Developer, on: false),
        Sp("hashicorp", "HashiCorp", "https://status.hashicorp.com", Developer, on: false),

        Sp("hubspot", "HubSpot", "https://status.hubspot.com", Business),
        V("stripe", "Stripe", Business, StripeStatusProvider.PageUrl, "https://stripe.com/favicon.ico", S("stripe", "Stripe", StripeStatusProvider.CurrentUrl)),
        V("paypal", "PayPal", Business, PayPalStatusProvider.PageUrl, "https://www.paypalobjects.com/webstatic/icon/pp258.png",
            S("paypal", "PayPal", PayPalStatusProvider.ComponentsUrl)),
        Sp("shopify", "Shopify", "https://www.shopifystatus.com", Business),
        Sp("xero", "Xero", "https://status.xero.com", Business),
        Sp("quickbooks", "QuickBooks", "https://status.quickbooks.intuit.com", Business),
        Sp("zapier", "Zapier", "https://status.zapier.com", Business),
        Sp("square", "Square", "https://www.issquareup.com", Business, on: false),
        Sp("netsuite", "Oracle NetSuite", "https://status.netsuite.com", Business, on: false),
        Sp("gusto", "Gusto", "https://status.gusto.com", Business, on: false),
        Sp("gocardless", "GoCardless", "https://www.gocardless-status.com", Business, on: false),
        Sp("klarna", "Klarna", "https://status.klarna.com", Business, on: false),
        Sp("wise", "Wise", "https://status.wise.com", Business, on: false),
        Sp("plaid", "Plaid", "https://status.plaid.com", Business, on: false),
        Sp("make", "Make", "https://status.make.com", Business, on: false),
        Sp("gong", "Gong", "https://status.gong.io", Business, on: false),
        Sp("salesloft", "Salesloft", "https://status.salesloft.com", Business, on: false),
        Sp("qualtrics", "Qualtrics", "https://status.qualtrics.com", Business, on: false),
        Sp("squarespace", "Squarespace", "https://status.squarespace.com", Business, on: false),
        Sp("wix", "Wix", "https://status.wix.com", Business, on: false),

        V("apple", "Apple", Consumer, AppleStatusProvider.PageUrl, "https://www.apple.com/favicon.ico",
            S("apple", "Apple services", "https://www.apple.com/support/systemstatus/data/system_status_en_GB.js")),
        Sp("discord", "Discord", "https://discordstatus.com", Consumer),
        Sp("reddit", "Reddit", "https://www.redditstatus.com", Consumer),
        Sp("twitch", "Twitch", "https://status.twitch.com", Consumer),
        Sp("epicgames", "Epic Games", "https://status.epicgames.com", Consumer, on: false),
        Sp("duolingo", "Duolingo", "https://status.duolingo.com", Consumer, on: false),
        Sp("vimeo", "Vimeo", "https://www.vimeostatus.com", Consumer, on: false),
        Sp("coinbase", "Coinbase", "https://status.coinbase.com", Consumer, on: false),
        Sp("kraken", "Kraken", "https://status.kraken.com", Consumer, on: false),
        Sp("robinhood", "Robinhood", "https://status.robinhood.com", Consumer, on: false),
        Off(V("monzo", "Monzo", Consumer, "https://status.monzo.com/", null, S("statuspage", "Monzo", "https://monzo.statuspage.io"))),

        // ---- Added in catalogue version 2 (all checked live 2026-11-22; off by default, so existing boards don't change) ----
        // Security & identity
        Sp("sentinelone", "SentinelOne", "https://status.sentinelone.com", Security),
        Sp("arcticwolf", "Arctic Wolf", "https://status.arcticwolf.com", Security, on: false),
        Sp("umbrella", "Cisco Umbrella", "https://status.umbrella.com", Security),
        Sp("watchguard", "WatchGuard", "https://status.watchguard.com", Security, on: false),
        Sp("proton", "Proton", "https://status.proton.me", Security, on: false),
        Sp("snyk", "Snyk", "https://status.snyk.io", Security, on: false),
        // IT management
        Sp("auvik", "Auvik", "https://status.auvik.com", ItManagement, on: false),
        Sp("liongard", "Liongard", "https://status.liongard.com", ItManagement, on: false),
        Sp("pax8", "Pax8", "https://status.pax8.com", ItManagement),
        // Cloud & infrastructure
        Sp("bunny", "bunny.net", "https://status.bunny.net", Cloud, on: false),
        Sp("planetscale", "PlanetScale", "https://www.planetscalestatus.com", Cloud, on: false),
        Sp("cloudinary", "Cloudinary", "https://status.cloudinary.com", Cloud, on: false),
        // Developer & AI
        Sp("cursor", "Cursor", "https://status.cursor.com", Developer, on: false),
        Sp("mistral", "Mistral AI", "https://status.mistral.ai", Developer, on: false),
        Sp("perplexity", "Perplexity", "https://status.perplexity.com", Developer, on: false),
        Sp("cohere", "Cohere", "https://status.cohere.com", Developer, on: false),
        Sp("elevenlabs", "ElevenLabs", "https://status.elevenlabs.io", Developer, on: false),
        Sp("postman", "Postman", "https://status.postman.com", Developer, on: false),
        Sp("buildkite", "Buildkite", "https://www.buildkitestatus.com", Developer, on: false),
        Sp("launchdarkly", "LaunchDarkly", "https://status.launchdarkly.com", Developer, on: false),
        Sp("retool", "Retool", "https://status.retool.com", Developer, on: false),
        Sp("contentful", "Contentful", "https://status.contentful.com", Developer, on: false),
        Sp("sanity", "Sanity", "https://status.sanity.io", Developer, on: false),
        // Productivity & collaboration
        Sp("dropboxsign", "Dropbox Sign", "https://status.hellosign.com", Productivity, on: false),
        Sp("toggl", "Toggl", "https://status.toggl.com", Productivity, on: false),
        Sp("workato", "Workato", "https://status.workato.com", Productivity, on: false),
        // Communications
        Sp("brevo", "Brevo", "https://status.brevo.com", Communications, on: false),
        Sp("hootsuite", "Hootsuite", "https://status.hootsuite.com", Communications, on: false),
        // Business & finance
        Sp("brex", "Brex", "https://status.brex.com", Business, on: false),
        Sp("chargebee", "Chargebee", "https://status.chargebee.com", Business, on: false),
        Sp("freshbooks", "FreshBooks", "https://status.freshbooks.com", Business, on: false),
        Sp("segment", "Segment", "https://status.segment.com", Business, on: false),
        Sp("mixpanel", "Mixpanel", "https://status.mixpanel.com", Business, on: false),

        // ---- Added in catalogue version 3 (checked live 2026-11-22). The few marked "on" complete the default set of 60, which
        //      fills rows of 2, 3, 4, 5 or 6 tiles evenly; existing boards still get every new vendor switched off (see Upgrade). ----
        // Sage: one page for all Sage products (Accounting, Sage 50/200, Intacct, People, Payroll…), regional component groups.
        Sp("sage", "Sage", "https://status.sage.com", Business),
        Sp("avalara", "Avalara", "https://status.avalara.com", Business, on: false),
        Sp("expensify", "Expensify", "https://status.expensify.com", Business, on: false),
        // Security & identity
        Sp("huntress", "Huntress", "https://status.huntress.com", Security),
        Sp("bitdefender", "Bitdefender GravityZone", "https://status.gravityzone.bitdefender.com", Security),
        Sp("eset", "ESET", "https://status.eset.com", Security, on: false),
        Sp("checkpoint", "Check Point", "https://status.checkpoint.com", Security, on: false),
        Sp("abnormal", "Abnormal Security", "https://status.abnormalsecurity.com", Security, on: false),
        Sp("ironscales", "IRONSCALES", "https://status.ironscales.com", Security, on: false),
        Sp("todyl", "Todyl", "https://status.todyl.com", Security, on: false),
        Sp("cyberqp", "CyberQP", "https://status.cyberqp.com", Security, on: false),
        Sp("nordlayer", "NordLayer", "https://status.nordlayer.com", Security, on: false),
        Sp("nordpass", "NordPass", "https://status.nordpass.com", Security, on: false),
        Sp("perimeter81", "Perimeter 81", "https://status.perimeter81.com", Security, on: false),
        Sp("twingate", "Twingate", "https://status.twingate.com", Security, on: false),
        // IT management & remote access
        Sp("atera", "Atera", "https://status.atera.com", ItManagement),
        Sp("anydesk", "AnyDesk", "https://status.anydesk.com", ItManagement),
        Sp("papercut", "PaperCut", "https://status.papercut.com", ItManagement, on: false),
        Sp("nutanix", "Nutanix", "https://status.nutanix.com", Cloud, on: false),
        // Productivity & collaboration
        Sp("sharefile", "ShareFile", "https://status.sharefile.com", Productivity, on: false),
        Sp("teamwork", "Teamwork.com", "https://status.teamwork.com", Productivity, on: false),
        // Communications
        Sp("nextiva", "Nextiva", "https://status.nextiva.com", Communications, on: false),
        Sp("constantcontact", "Constant Contact", "https://status.constantcontact.com", Communications, on: false),
        // Developer
        Sp("ngrok", "ngrok", "https://status.ngrok.com", Developer, on: false),
        // Education
        Sp("clever", "Clever", "https://status.clever.com", Education, on: false),
        Sp("classlink", "ClassLink", "https://status.classlink.com", Education, on: false),
        Sp("instructure", "Instructure (Canvas)", "https://status.instructure.com", Education, on: false),
        Sp("powerschool", "PowerSchool", "https://status.powerschool.com", Education, on: false),
        Sp("seesaw", "Seesaw", "https://status.seesaw.me", Education, on: false),

        // ---- Salesforce products ----
        // A Salesforce product: each Tableau pod and its incidents. Blank instances = all pods (set yours, e.g. "PDUKAA").
        V("tableau", "Tableau", Business, "https://status.salesforce.com/products/Tableau", "https://www.tableau.com/favicon.ico",
            S("salesforce", "Tableau", SalesforceProvider.ProductApiBase + "Tableau", ("instances", ""))),
    ];

    /// <summary>
    /// Appends built-ins an older config doesn't have yet (built-ins can only be switched off, so a missing one is new), skipping
    /// any the user already added by hand (same source URL), and fills in a missing section (e.g. a hand-edited file). True if changed.
    /// </summary>
    public static bool Upgrade(AppConfig config)
    {
        var builtIns = Create();
        var changed = false;
        foreach (var v in config.Vendors.Where(v => string.IsNullOrWhiteSpace(v.Category)))
        {
            var category = v.Custom ? Custom : builtIns.FirstOrDefault(b => string.Equals(b.Id, v.Id, StringComparison.OrdinalIgnoreCase))?.Category;
            if (category is null) continue;
            v.Category = category;
            changed = true;
        }
        if (config.DefaultsVersion >= Version) return changed;

        var ids = config.Vendors.Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var urls = config.Vendors.SelectMany(v => v.Sources).Select(s => s.Url.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // New to this board: added switched off, so an update never changes what someone's board shows.
        var added = builtIns.Where(v => !ids.Contains(v.Id) && !v.Sources.Any(s => urls.Contains(s.Url.TrimEnd('/')))).ToList();
        foreach (var v in added) v.Enabled = config.DefaultsVersion == 0 && config.Vendors.Count == 0 && v.Enabled;
        config.Vendors.AddRange(added);
        config.DefaultsVersion = Version;
        return true;
    }

    // ---- Table helpers ----
    private static VendorConfig V(string id, string name, string category, string page, string? logo, params SourceConfig[] sources) =>
        new() { Id = id, Name = name, Category = category, StatusPageUrl = page, Logo = logo, Sources = [.. sources] };

    private static VendorConfig Sp(string id, string name, string url, string category, bool on = true, string? logo = null) =>
        new() { Id = id, Name = name, Category = category, Enabled = on, Logo = logo, StatusPageUrl = url + "/", Sources = [S("statuspage", name, url)] };

    private static VendorConfig Sio(string id, string name, string page, string pageId, string category) =>
        V(id, name, category, page, null, S("statusio", name, StatusIoProvider.ApiBase + pageId, ("link", page)));

    private static SourceConfig S(string type, string label, string url, params (string Key, string Value)[] options)
    {
        var s = new SourceConfig { Type = type, Label = label, Url = url };
        foreach (var (k, v) in options) s.Options[k] = v;
        return s;
    }

    /// <summary>History/state feeds: an item counts while updated in the last <paramref name="hours"/>.</summary>
    private static SourceConfig Recent(SourceConfig s, bool on = true, string hours = "24")
    {
        s.Options["mode"] = "recent";
        s.Options["activeWindowHours"] = hours;
        s.Enabled = on;
        return s;
    }

    /// <summary>Statuspage sources from "Label|host" ("-Label|host" = off by default).</summary>
    private static SourceConfig[] Pages(params string[] pages) => [.. pages.Select(p =>
    {
        var (label, host) = (p.Split('|')[0], p.Split('|')[1]);
        var s = S("statuspage", label.TrimStart('-'), "https://" + host);
        s.Enabled = !label.StartsWith('-');
        return s;
    })];

    private static VendorConfig Off(VendorConfig v) { v.Enabled = false; return v; }
}
