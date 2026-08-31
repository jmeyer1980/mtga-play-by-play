using System.Net.Sockets;
using System.Text;
using MtgaPbp.Cli;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class LiveServerTests
{
    private string _parent = null!;
    private string _root = null!;
    private LiveServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _parent = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"live_{Guid.NewGuid():N}")).FullName;
        _root = Directory.CreateDirectory(Path.Combine(_parent, "serve")).FullName;

        File.WriteAllText(Path.Combine(_root, "index.html"), "<html>the report</html>");
        Directory.CreateDirectory(Path.Combine(_root, "text"));
        File.WriteAllText(Path.Combine(_root, "text", "note.md"), "# note");

        // A sibling whose name continues the root's — the shape a bare string-prefix
        // path check fails to tell apart from the root itself.
        var evil = Directory.CreateDirectory(Path.Combine(_parent, "serve-evil")).FullName;
        File.WriteAllText(Path.Combine(evil, "secret.txt"), "should never be served");

        _server = new LiveServer(_root, port: 0);   // 0: the OS picks a free port
        _server.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _server.Dispose();
        Directory.Delete(_parent, recursive: true);
    }

    /// <summary>Sends raw bytes and returns the raw response, for full header control.</summary>
    private string Send(string request)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", _server.Port);
        client.ReceiveTimeout = 5000;
        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(request);
        stream.Write(bytes, 0, bytes.Length);
        using var ms = new MemoryStream();
        try { stream.CopyTo(ms); } catch (IOException) { /* server closed or timed out */ }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string Get(string path, string? host = null) => Send(
        $"GET {path} HTTP/1.1\r\nHost: {host ?? $"127.0.0.1:{_server.Port}"}\r\n" +
        "Connection: close\r\n\r\n");

    private string Post(string path, string? origin = null)
    {
        var originLine = origin is null ? "" : $"Origin: {origin}\r\n";
        return Send(
            $"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\n{originLine}" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");
    }

    [Test]
    public void Serves_the_report_to_a_local_request()
    {
        var response = Get("/");
        Assert.That(response, Does.Contain("200 OK"));
        Assert.That(response, Does.Contain("text/html"));
        Assert.That(response, Does.Contain("the report"));
    }

    [Test]
    public void Localhost_is_as_good_as_the_loopback_ip()
    {
        Assert.That(Get("/", host: $"localhost:{_server.Port}"), Does.Contain("200 OK"));
    }

    [Test]
    public void A_request_with_a_foreign_host_is_refused()
    {
        // DNS rebinding: a hostile page points its own hostname at 127.0.0.1 and
        // becomes same-origin with this server. The Host header carrying that
        // hostname is the only place the trick is visible.
        var response = Get("/", host: "attacker.example");
        Assert.That(response, Does.Contain("404"));
        Assert.That(response, Does.Not.Contain("the report"));
    }

    [Test]
    public void A_loopback_host_with_the_wrong_port_is_refused()
    {
        // A Host that names us but not our port is nothing a browser pointed at
        // this server would ever send — refuse it rather than reason about it.
        var response = Get("/", host: $"127.0.0.1:{_server.Port + 1}");
        Assert.That(response, Does.Contain("404"));
        Assert.That(response, Does.Not.Contain("the report"));
    }

    [Test]
    public void A_portless_loopback_host_is_still_accepted()
    {
        // A bare loopback name states no port and so names no other server.
        Assert.That(Get("/", host: "localhost"), Does.Contain("200 OK"));
    }

    [Test]
    public void A_request_with_no_host_at_all_is_refused()
    {
        var response = Send("GET / HTTP/1.1\r\nConnection: close\r\n\r\n");
        Assert.That(response, Does.Not.Contain("the report"));
    }

    [Test]
    public void A_sibling_directory_sharing_the_root_name_prefix_is_not_served()
    {
        var response = Get("/..%2Fserve-evil%2Fsecret.txt");
        Assert.That(response, Does.Contain("404"));
        Assert.That(response, Does.Not.Contain("should never be served"));
    }

    [Test]
    public void Traversal_out_of_the_root_is_refused()
    {
        Assert.That(Get("/..%2F..%2Fanything.txt"), Does.Contain("404"));
    }

    [Test]
    public void Markdown_is_served_with_its_content_type()
    {
        var response = Get("/text/note.md");
        Assert.That(response, Does.Contain("200 OK"));
        Assert.That(response, Does.Contain("text/markdown"));
    }

    [Test]
    public void A_missing_file_is_a_404()
    {
        Assert.That(Get("/nope.html"), Does.Contain("404"));
    }

    [Test]
    public void The_favorite_endpoint_answers_the_pages_own_origin()
    {
        (string Id, bool On)? seen = null;
        _server.OnFavorite = (id, on) => { seen = (id, on); return true; };

        var response = Post("/api/favorite/m1", origin: $"http://127.0.0.1:{_server.Port}");

        Assert.That(response, Does.Contain("200 OK"));
        Assert.That(seen, Is.EqualTo(("m1", true)));
    }

    [Test]
    public void The_favorite_endpoint_refuses_a_cross_site_origin()
    {
        // A no-cors POST from any website lands its side effect even though the
        // response is unreadable — the foreign Origin is the tell, and match ids
        // are published on purpose, so guessing one is not a barrier.
        var invoked = false;
        _server.OnFavorite = (_, _) => invoked = true;

        var response = Post("/api/favorite/m1", origin: "https://evil.example");

        Assert.That(response, Does.Contain("403"));
        Assert.That(invoked, Is.False, "the callback must not run for a cross-site request");
    }

    [Test]
    public void The_favorite_endpoint_refuses_a_sibling_loopback_origin()
    {
        // Same-origin includes the port: a page served by some other local
        // application is another site, however local it is.
        var invoked = false;
        _server.OnFavorite = (_, _) => invoked = true;

        var response = Post("/api/favorite/m1", origin: $"http://127.0.0.1:{_server.Port + 1}");

        Assert.That(response, Does.Contain("403"));
        Assert.That(invoked, Is.False);
    }

    [Test]
    public void The_favorite_endpoint_still_works_without_an_origin()
    {
        // No Origin means a non-browser caller — curl, a script, this test — which
        // the loopback binding already vouches for.
        _server.OnFavorite = (_, _) => true;
        Assert.That(Post("/api/favorite/m1"), Does.Contain("200 OK"));
    }

    [Test]
    public void The_favorite_endpoint_parses_the_off_switch()
    {
        bool? sawOn = null;
        _server.OnFavorite = (_, on) => { sawOn = on; return true; };

        Post("/api/favorite/m1?on=false");

        Assert.That(sawOn, Is.False);
    }

    [Test]
    public void An_unknown_match_is_a_404_from_the_favorite_endpoint()
    {
        _server.OnFavorite = (_, _) => false;
        Assert.That(Post("/api/favorite/nope"), Does.Contain("404"));
    }

    [Test]
    public void A_change_notification_reaches_a_subscribed_page()
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", _server.Port);
        client.ReceiveTimeout = 5000;
        var stream = client.GetStream();
        var request = Encoding.UTF8.GetBytes(
            $"GET /api/events HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\n\r\n");
        stream.Write(request, 0, request.Length);

        var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null && line != ": connected") { }
        Assert.That(line, Is.EqualTo(": connected"), "subscription handshake");

        _server.NotifyChanged();

        while ((line = reader.ReadLine()) is not null && line != "event: changed") { }
        Assert.That(line, Is.EqualTo("event: changed"));
    }

    /// <summary>
    /// Subscribes and returns once the server has said <c>: connected</c>, which it
    /// writes only after the subscriber is in the list — so a caller may look at the
    /// count without racing the accept.
    /// </summary>
    private (TcpClient Client, StreamReader Reader) OpenStream()
    {
        var client = new TcpClient();
        client.Connect("127.0.0.1", _server.Port);
        client.ReceiveTimeout = 5000;
        var stream = client.GetStream();
        var request = Encoding.UTF8.GetBytes(
            $"GET /api/events HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\n\r\n");
        stream.Write(request, 0, request.Length);

        var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null && line != ": connected") { }
        Assert.That(line, Is.EqualTo(": connected"), "subscription handshake");
        return (client, reader);
    }

    [Test]
    public void Reloading_the_page_does_not_leave_a_socket_behind_each_time()
    {
        // Ten reloads. Before the reaping, the list was pruned only by a write that
        // failed, so on an evening with nothing to notify all ten stayed in it and the
        // count below was eleven (#132).
        for (var i = 0; i < 10; i++) OpenStream().Client.Close();

        // The tab that stays open. Its own subscribe is what drops the ten before it —
        // all but possibly the last, whose close may still be in flight on the wire,
        // which is the one thing this count is allowed to be uncertain about.
        using var live = OpenStream().Client;

        Assert.That(_server.Subscribers, Is.LessThanOrEqualTo(2));
    }

    [Test]
    public void Reaping_leaves_a_page_that_is_still_open_alone()
    {
        var (live, reader) = OpenStream();
        using (live)
        {
            // This subscribe reaps, and must find nothing to reap.
            using var other = OpenStream().Client;

            _server.NotifyChanged();

            string? line;
            while ((line = reader.ReadLine()) is not null && line != "event: changed") { }
            Assert.That(line, Is.EqualTo("event: changed"),
                "a page nobody closed still gets its notifications");
        }
    }
}
