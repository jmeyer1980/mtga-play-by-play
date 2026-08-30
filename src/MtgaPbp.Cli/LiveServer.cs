using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MtgaPbp.Cli;

/// <summary>
/// A minimal HTTP server for the live report, bound to loopback only.
/// </summary>
/// <remarks>
/// Hand-rolled on <see cref="TcpListener"/> rather than <c>HttpListener</c>: the
/// latter routinely needs a URL ACL reservation on Windows, which means running as
/// administrator once before the tool works at all. This has to work from a
/// double-click.
/// <para>
/// Serving the report over http also lifts the <c>file://</c> restriction the static
/// output is built around — the page can finally fetch, so it can refresh itself and
/// star a match without a full reload.
/// </para>
/// <para>
/// Binding to loopback is necessary but not sufficient: the user's own browser sits
/// on this side of it, running pages from everywhere. A hostile page can point a
/// hostname it controls at 127.0.0.1 and become same-origin with this server (DNS
/// rebinding), and any page can land a no-cors POST whose response it cannot read.
/// The <c>Host</c> and <c>Origin</c> checks in <see cref="Serve"/> are what stand
/// between those tricks and an archive full of real player names (#116).
/// </para>
/// </remarks>
public sealed class LiveServer(string rootDirectory, int port) : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private readonly List<StreamWriter> _subscribers = [];
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The port actually bound — asked of the listener because a requested
    /// port of 0 means the OS picks one, which is how the tests avoid collisions.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Url => $"http://127.0.0.1:{Port}/";

    /// <summary>Invoked when the page asks to keep or unkeep a match.</summary>
    public Func<string, bool, bool>? OnFavorite { get; set; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Tells every open page that the archive changed.</summary>
    /// <remarks>
    /// The writes happen outside the lock: they block until the peer accepts the
    /// bytes, and holding the lock through that would let one stalled page make
    /// every other caller queue behind it. The socket send timeout set in
    /// <see cref="Serve"/> bounds how long a dead subscriber can stall this call
    /// itself before it is noticed and dropped.
    /// </remarks>
    public void NotifyChanged()
    {
        StreamWriter[] targets;
        lock (_subscribers) targets = [.. _subscribers];

        List<StreamWriter>? dead = null;
        foreach (var s in targets)
        {
            try
            {
                // Per-writer lock: two overlapping notifications must not interleave
                // bytes inside one subscriber's stream. The list lock above guards
                // membership only, so this is the only thing serializing the writes.
                lock (s)
                {
                    s.Write("event: changed\ndata: 1\n\n");
                    s.Flush();
                }
            }
            catch
            {
                // The page was closed. Nothing to recover, just stop tracking it.
                (dead ??= []).Add(s);
            }
        }

        if (dead is null) return;
        lock (_subscribers)
            foreach (var s in dead) _subscribers.Remove(s);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }

            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            // A stalled or dribbling peer must never pin a pool thread for good. The
            // send timeout doubles as the bound on how long a dead subscriber can
            // stall NotifyChanged before its write fails and it is dropped.
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };

            var requestLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(requestLine)) { client.Dispose(); return; }

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) { client.Dispose(); return; }
            var (method, target) = (parts[0], parts[1]);

            // Two headers are load-bearing; the rest are still discarded. Host is the
            // DNS-rebinding check — a hostile page can point a hostname it controls
            // at 127.0.0.1 and become same-origin with this server, and the header
            // carrying that hostname is the only place the trick shows.
            string? host = null, origin = null;
            var headers = 0;
            while (reader.ReadLine() is { Length: > 0 } header)
            {
                if (++headers > 100) { client.Dispose(); return; }
                var colon = header.IndexOf(':');
                if (colon < 1) continue;
                var name = header[..colon].Trim();
                var value = header[(colon + 1)..].Trim();
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = value;
                else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = value;
            }

            if (!IsLoopbackHost(host))
            {
                Respond(writer, "404 Not Found", "text/plain", "not found"u8.ToArray());
                client.Dispose();
                return;
            }

            var path = target.Split('?')[0];
            var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";

            if (path == "/api/events") { Subscribe(client, writer); return; }

            if (method == "POST" && path.StartsWith("/api/favorite/", StringComparison.Ordinal))
            {
                // Browsers put an Origin on every cross-site POST, and a no-cors
                // fetch lands its side effect even though the response is opaque —
                // so a foreign Origin is refused outright. No Origin at all means a
                // non-browser caller, which the loopback binding already vouches for.
                if (origin is not null && !IsOwnOrigin(origin))
                {
                    Respond(writer, "403 Forbidden", "text/plain", "forbidden"u8.ToArray());
                    client.Dispose();
                    return;
                }

                var id = Uri.UnescapeDataString(path["/api/favorite/".Length..]);
                var on = !query.Contains("on=false", StringComparison.Ordinal);
                var ok = OnFavorite?.Invoke(id, on) ?? false;
                Respond(writer, ok ? "200 OK" : "404 Not Found", "text/plain", "{}"u8.ToArray());
                client.Dispose();
                return;
            }

            ServeFile(writer, path);
            client.Dispose();
        }
        catch
        {
            // A dropped connection is normal; never let it take the server down.
            try { client.Dispose(); } catch { /* already gone */ }
        }
    }

    /// <summary>True when a Host header names this server and no other.</summary>
    /// <remarks>
    /// A stated port must be this server's own — <c>127.0.0.1:1234</c> aimed at a
    /// listener on 8787 is nothing a browser pointed here would send. A bare
    /// loopback name states no port and so names no other server; it stays
    /// accepted for plain non-browser clients.
    /// </remarks>
    private bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var colon = host.LastIndexOf(':');
        var name = (colon < 0 ? host : host[..colon]).Trim();
        if (name is not "127.0.0.1" &&
            !name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        return colon < 0 || host[(colon + 1)..].Trim() == Port.ToString();
    }

    /// <summary>True when an Origin header is this server's own page, exactly.</summary>
    /// <remarks>
    /// Same-origin includes the port: a page served by some other local application
    /// is another site, however local it is. An opaque <c>Origin: null</c>
    /// (sandboxed frames, some redirects) fails on purpose — it proves nothing
    /// about who is asking.
    /// </remarks>
    private bool IsOwnOrigin(string origin)
    {
        if (!origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = origin["http://".Length..].TrimEnd('/');
        var colon = rest.LastIndexOf(':');
        var name = colon < 0 ? rest : rest[..colon];
        var port = colon < 0 ? "80" : rest[(colon + 1)..];
        return (name is "127.0.0.1" ||
                name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            && port == Port.ToString();
    }

    /// <summary>Holds the connection open and streams change events to the page.</summary>
    private void Subscribe(TcpClient client, StreamWriter writer)
    {
        writer.Write("HTTP/1.1 200 OK\r\n");
        writer.Write("Content-Type: text/event-stream\r\n");
        writer.Write("Cache-Control: no-cache\r\n");
        writer.Write("Connection: keep-alive\r\n\r\n");
        writer.Write(": connected\n\n");
        writer.Flush();

        lock (_subscribers) _subscribers.Add(writer);
        // The socket stays open; NotifyChanged writes to it and drops it when it dies.
        _ = client;
    }

    private void ServeFile(StreamWriter writer, string path)
    {
        var relative = path == "/" ? "index.html" : path.TrimStart('/');
        relative = Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);

        var full = Path.GetFullPath(Path.Combine(rootDirectory, relative));
        var root = Path.GetFullPath(rootDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        // Refuse anything that escapes the output directory. The comparison needs
        // the trailing separator: without it a bare prefix check also admits a
        // sibling directory whose name merely continues the root's, like `out-old`
        // beside `out`.
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            Respond(writer, "404 Not Found", "text/plain", "not found"u8.ToArray());
            return;
        }

        Respond(writer, "200 OK", ContentType(full), File.ReadAllBytes(full));
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".md" => "text/markdown; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };

    private static void Respond(StreamWriter writer, string status, string type, byte[] body)
    {
        writer.Write($"HTTP/1.1 {status}\r\n");
        writer.Write($"Content-Type: {type}\r\n");
        writer.Write($"Content-Length: {body.Length}\r\n");
        writer.Write("Cache-Control: no-store\r\n");
        writer.Write("Connection: close\r\n\r\n");
        writer.Flush();
        writer.BaseStream.Write(body, 0, body.Length);
        writer.BaseStream.Flush();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        lock (_subscribers)
        {
            foreach (var s in _subscribers)
            {
                // Same per-writer lock as NotifyChanged, so a dispose cannot land
                // in the middle of a notification's write.
                try { lock (s) s.Dispose(); } catch { /* gone */ }
            }
            _subscribers.Clear();
        }
        _cts.Dispose();
    }
}
