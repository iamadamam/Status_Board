using System.Net;
using System.Net.Sockets;
using System.Text;
using StatusBoard.Core;
using Xunit;

namespace StatusBoard.Tests;

/// <summary>Hardening: credentials never follow redirects, hostile XML can't expand entities, profiles can't point at other files.</summary>
public sealed class SecurityTests
{
    /// <summary>A one-thread HTTP server on 127.0.0.1: "/start" redirects to "/end", which answers "ok". Records request paths.</summary>
    private sealed class RedirectServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public List<string> Paths { get; } = [];
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public RedirectServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buf = new byte[8192];
                        var n = await stream.ReadAsync(buf);
                        var path = Encoding.ASCII.GetString(buf, 0, n).Split(' ')[1];
                        lock (Paths) Paths.Add(path);
                        var response = path == "/start"
                            ? "HTTP/1.1 302 Found\r\nLocation: /end\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                    }
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public async Task Plain_requests_follow_redirects()
    {
        using var server = new RedirectServer();
        using var http = new HttpFetcher(maxAttempts: 1);
        var text = await ((IHttpFetcher)http).GetTextAsync(server.Url + "/start", CancellationToken.None);
        Assert.Equal("ok", text);
    }

    [Fact]
    public async Task Requests_with_a_token_never_follow_a_redirect()
    {
        using var server = new RedirectServer();
        using var http = new HttpFetcher(maxAttempts: 1);
        var opts = new FetchOptions { Headers = new Dictionary<string, string> { ["X-Auth-Token"] = "secret" } };
        var ex = await Assert.ThrowsAsync<FetchException>(() => http.GetAsync(server.Url + "/start", CancellationToken.None, opts));
        Assert.Equal(302, ex.StatusCode);
        lock (server.Paths) Assert.DoesNotContain("/end", server.Paths); // the token went nowhere else
    }

    [Fact]
    public async Task Feed_detection_ignores_entity_expansion()
    {
        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY a "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]>
            <rss><channel><title>&c;</title><item><title>x</title></item></channel></rss>
            """;
        var http = new FakeFetcher().Add("https://status.example.com/feed.xml", bomb, "application/rss+xml");
        var r = await VendorDetector.DetectAsync("https://status.example.com/feed.xml", http, CancellationToken.None);
        Assert.DoesNotContain("aaaaaaaaaa", r.Message + r.SuggestedName);
    }

    [Fact]
    public void Profiles_can_only_point_at_settings_files()
    {
        var dir = Directory.CreateTempSubdirectory("sb-sec");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "important.txt"), "keep me");
            File.WriteAllText(Path.Combine(dir.FullName, ProfileStore.IndexFile),
                """{ "active": "x", "profiles": [ { "id": "x", "name": "Evil", "file": "important.txt" }, { "id": "y", "name": "Up", "file": "..\\other.json" } ] }""");
            var s = new ProfileStore(dir.FullName).Load();
            Assert.Empty(s.Profiles);
            Assert.True(File.Exists(Path.Combine(dir.FullName, "important.txt")));
        }
        finally { dir.Delete(true); }
    }
}
