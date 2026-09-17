using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MtgaPbp.Cli;

/// <summary>
/// A minimal HTTP server for the live report — loopback by default, the whole network
/// when asked with a key.
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
/// <para>
/// LAN mode binds <see cref="IPAddress.Any"/> instead, so it demands a key: the
/// constructor refuses <c>lan: true</c> without a usable one, whatever the caller —
/// the CLI's own check is a courtesy message, and this is the guarantee.
/// </para>
/// </remarks>
public sealed class LiveServer(string rootDirectory, int port, bool lan = false, string? lanKey = null) : IDisposable
{
    private readonly TcpListener _listener = new(ValidateLan(lan, lanKey) ? IPAddress.Any : IPAddress.Loopback, port);
    private readonly List<Subscriber> _subscribers = [];
    private readonly CancellationTokenSource _cts = new();
    // The key is inert outside LAN mode, whatever was configured: Program always
    // passes cfg.LanKey, and a key sitting in mtga-pbp.json must not change the
    // behavior of the loopback-only watch that never asked for it (found in
    // review). --lan is what arms the key, and nothing else.
    private readonly string? _key = lan ? lanKey : null;

    /// <summary>Whether the server is in LAN mode (bound to all interfaces).</summary>
    public bool Lan => lan;

    /// <summary>The port actually bound — asked of the listener because a requested
    /// port of 0 means the OS picks one, which is how the tests avoid collisions.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Url => $"http://127.0.0.1:{Port}/";

    /// <summary>The URL to give another device in LAN mode, or null when this machine
    /// holds no address another device could reach — the caller says so instead of
    /// advertising an address that cannot work.</summary>
    /// <remarks>
    /// In LAN mode the key rides along: the bare address is refused, so the URL the
    /// user copies is the one that gets them in on the first navigation — which then
    /// becomes a cookie and a clean address bar.
    /// </remarks>
    public string? LanUrl => lan && LanAccess.LanAddress() is { } addr
        ? $"http://{addr}:{Port}/?key={_key}"
        : null;

    /// <summary>True when the listener must bind every interface — and, the price of
    /// that, only when the key is one <see cref="LanAccess.Refusal"/> will accept.
    /// Asked from the listener's own initializer so the constructor cannot finish
    /// without it.</summary>
    private static bool ValidateLan(bool lan, string? lanKey)
    {
        if (lan && LanAccess.Refusal(lanKey) is { } refusal)
            throw new ArgumentException(refusal, nameof(lanKey));
        return lan;
    }

    /// <summary>The address the listener bound — Any in LAN mode, Loopback otherwise.</summary>
    public IPAddress BoundAddress => ((IPEndPoint)_listener.LocalEndpoint).Address;

    /// <summary>Invoked when the page asks to keep or unkeep a match.</summary>
    public Func<string, bool, bool>? OnFavorite { get; set; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>How many pages are currently subscribed to the change stream.</summary>
    /// <remarks>
    /// Here for the test that pins <see cref="Reap"/>: a leak whose only symptom is a
    /// number quietly climbing all evening cannot be caught by asserting on anything
    /// the pages receive, because every page still receives everything. The count is
    /// the symptom, so the count is what is checked.
    /// </remarks>
    public int Subscribers { get { lock (_subscribers) return _subscribers.Count; } }

    /// <summary>Tells every open page that the archive changed.</summary>
    public void NotifyChanged() => Deliver("event: changed\ndata: 1\n\n");

    /// <summary>
    /// Writes one frame to every subscriber, and drops the ones it cannot reach.
    /// </summary>
    /// <remarks>
    /// The writes happen outside the list lock: they block until the peer accepts the
    /// bytes, and holding the lock through that would let one stalled page make every
    /// other caller queue behind it. The socket send timeout set in <see cref="Serve"/>
    /// bounds how long a dead subscriber can stall this call itself before it is
    /// noticed and dropped.
    /// </remarks>
    private void Deliver(string frame)
    {
        Subscriber[] targets;
        lock (_subscribers) targets = [.. _subscribers];

        List<Subscriber>? dead = null;
        foreach (var s in targets)
        {
            try
            {
                // Per-writer lock: two overlapping notifications must not interleave
                // bytes inside one subscriber's stream. The list lock above guards
                // membership only, so this is the only thing serializing the writes.
                lock (s.Writer)
                {
                    s.Writer.Write(frame);
                    s.Writer.Flush();
                }
            }
            catch
            {
                // The page was closed. Nothing to recover, just stop tracking it.
                (dead ??= []).Add(s);
            }
        }

        Drop(dead);
    }

    /// <summary>Forgets and closes subscribers that are no longer worth writing to.</summary>
    private void Drop(List<Subscriber>? dead)
    {
        if (dead is null || dead.Count == 0) return;
        lock (_subscribers)
            foreach (var s in dead) _subscribers.Remove(s);
        foreach (var s in dead) s.Dispose();
    }

    /// <summary>
    /// Drops every subscriber whose page has gone away.
    /// </summary>
    /// <remarks>
    /// Until now the list was pruned only by a write that failed, so on an evening with
    /// no matches — no captures, no rebuilds, nothing to notify — every reload left its
    /// socket in the list and took another, and a tab opened and closed a dozen times
    /// was still counted a dozen times (#132).
    /// <para>
    /// Asked of the socket rather than answered by writing to it. Measured against a
    /// closed loopback peer: the first write after the close succeeds and only the
    /// second fails, so a write-probe would reap every connection exactly one round
    /// late — and it would cost every live page a wakeup to learn something about the
    /// dead ones. Readable with nothing to read is the peer's FIN instead, and it is
    /// true immediately; an <c>EventSource</c> sends nothing after its request, so
    /// anything actually readable is a page talking, which is not this.
    /// </para>
    /// </remarks>
    private void Reap()
    {
        Subscriber[] targets;
        lock (_subscribers) targets = [.. _subscribers];

        List<Subscriber>? dead = null;
        foreach (var s in targets)
            if (s.HasGoneAway) (dead ??= []).Add(s);

        Drop(dead);
    }

    /// <summary>One page holding the change stream open.</summary>
    /// <remarks>
    /// The client travels with the writer because both are needed to let a subscriber
    /// go: the socket is what can be asked whether the page is still there, and the
    /// writer is what owns it once it is not. Keeping only the writer is why a dropped
    /// subscriber used to hold its handle until a garbage collection noticed — the same
    /// leak by a slower route.
    /// </remarks>
    private sealed record Subscriber(TcpClient Client, StreamWriter Writer) : IDisposable
    {
        public bool HasGoneAway
        {
            get
            {
                // Any failure to ask counts as an answer: a socket that cannot say
                // whether it is still there is one nothing will be written to again.
                try
                {
                    return !Client.Connected ||
                           (Client.Client.Poll(0, SelectMode.SelectRead) && Client.Available == 0);
                }
                catch
                {
                    return true;
                }
            }
        }

        public void Dispose()
        {
            // Same per-writer lock as Deliver, so a close cannot land in the middle of
            // a notification's write.
            try { lock (Writer) Writer.Dispose(); } catch { /* gone */ }
            try { Client.Dispose(); } catch { /* gone */ }
        }
    }

    /// <summary>The most of a request head this parser will ever take: 8 KiB in total,
    /// and ten seconds on the clock, whichever runs out first.</summary>
    /// <remarks>
    /// LAN mode hands this parser to strangers, so both limits are load-bearing. The
    /// socket's per-read timeout only bounds a read that returns nothing — a peer that
    /// drips one byte per keepalive used to hold a pool thread forever inside a
    /// <c>ReadLine</c> that never ended, and a single line of unbounded length was an
    /// allocation sized by whoever connected, all before the key check could have said
    /// no (found in review). A head that trips either limit gets a hangup, not a
    /// response; there is nothing worth saying to a peer doing that.
    /// </remarks>
    private static List<string>? ReadRequestHead(Stream stream)
    {
        const int maxHeadBytes = 8 * 1024;
        var clock = Stopwatch.StartNew();
        var head = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        var complete = false;
        while (!complete)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) return null;
            var budget = maxHeadBytes + 1 - (int)head.Length;
            if (budget <= 0) return null;
            if ((read = stream.Read(chunk, 0, Math.Min(chunk.Length, budget))) <= 0) break;
            head.Write(chunk, 0, read);
            // The head's terminator may land mid-read: a POST can carry its body in
            // the same segment the head ends in, so the delimiter is not necessarily
            // at the end of the bytes (found in review). The first one wins; whatever
            // follows it is body, and goes away with the connection.
            if (FirstBlankLine(head) is { } end)
            {
                head.SetLength(end + 1);
                complete = true;
            }
        }
        if (head.Length == 0) return null;
        if (!complete) return null;   // never saw the terminator — hangup, not a guess
        return [.. Encoding.UTF8.GetString(head.ToArray()).Split('\n').Select(l => l.TrimEnd('\r'))];
    }

    /// <summary>The index of the <c>'\n'</c> that closes the first blank line anywhere
    /// in the buffer — the head's terminator, be it a well-behaved client's
    /// CRLF CRLF or a lazy one's LF LF — or null when it is not there yet.</summary>
    private static int? FirstBlankLine(MemoryStream head)
    {
        var b = head.GetBuffer();
        var n = (int)head.Length;
        for (var i = 1; i < n; i++)
            if (b[i] == (byte)'\n' &&
                (b[i - 1] == (byte)'\n' ||
                 i >= 3 && b[i - 1] == (byte)'\r' && b[i - 2] == (byte)'\n' && b[i - 3] == (byte)'\r'))
                return i;
        return null;
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
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };

            var head = ReadRequestHead(stream);
            if (head is null || string.IsNullOrWhiteSpace(head[0])) { client.Dispose(); return; }

            var parts = head[0].Split(' ');
            if (parts.Length < 2) { client.Dispose(); return; }
            var (method, target) = (parts[0], parts[1]);

            // Two headers are load-bearing; the rest are still discarded. Host is the
            // DNS-rebinding check — a hostile page can point a hostname it controls
            // at 127.0.0.1 and become same-origin with this server, and the header
            // carrying that hostname is the only place the trick shows.
            string? host = null, origin = null;
            var headerLines = new List<string>();
            var headers = 0;
            foreach (var header in head.Skip(1))
            {
                if (header.Length == 0) break;
                if (++headers > 100) { client.Dispose(); return; }
                var colon = header.IndexOf(':');
                if (colon < 1) continue;
                var name = header[..colon].Trim();
                var value = header[(colon + 1)..].Trim();
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = value;
                else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = value;
                headerLines.Add(header);
            }

            // The rebinding check: a Host that does not name this server — any name
            // but loopback and this machine's own addresses — is refused outright,
            // in loopback mode and in LAN mode alike (#116).
            if (!LanAccess.IsOwnHost(host, Port, LanAccess.OwnAddresses()))
            {
                Respond(writer, "404 Not Found", "text/plain", "not found"u8.ToArray());
                client.Dispose();
                return;
            }

            var path = target.Split('?')[0];
            var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";

            // Admission, in LAN mode. The machine's own browser is vouched for by its
            // loopback socket and never needs a key; anything from off this machine
            // must present the configured key — as ?key= on a first navigation or as
            // the pbp_key cookie afterwards, which is what lets EventSource and the
            // page's own fetch authenticate without the key in every URL.
            if (_key is not null)
            {
                var cookieHeader = string.Empty;
                foreach (var h in headerLines)
                {
                    if (h.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                    {
                        cookieHeader = h["Cookie:".Length..].Trim();
                        break;
                    }
                }

                var fromLoopback = LanAccess.IsLoopbackPeer(
                    (client.Client.RemoteEndPoint as IPEndPoint)?.Address);
                var hasKeyInQuery = LanAccess.KeyMatches(LanAccess.KeyFromQuery(target), _key);
                var hasValidCookie = LanAccess.KeyMatches(LanAccess.KeyFromCookie(cookieHeader), _key);

                if (!fromLoopback && !hasKeyInQuery && !hasValidCookie)
                {
                    Respond(writer, "401 Unauthorized", "text/plain", "unauthorized"u8.ToArray());
                    client.Dispose();
                    return;
                }

                // The key never has to appear twice: a URL that carried it is answered
                // with a 302 to the clean path and the cookie that remembers it. Only
                // a document navigation gets the redirect — a query-keyed API call
                // must be executed, not bounced (found in review), and /api/events
                // never re-navigates.
                if (hasKeyInQuery && !hasValidCookie &&
                    method is "GET" or "HEAD" &&
                    !path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    RespondCookieRedirect(writer, path);
                    client.Dispose();
                    return;
                }
            }

            if (path == "/api/events") { Subscribe(client, writer); return; }

            if (method == "POST" && path.StartsWith("/api/favorite/", StringComparison.Ordinal))
            {
                // Browsers put an Origin on every cross-site POST, and a no-cors
                // fetch lands its side effect even though the response is opaque —
                // so a foreign Origin is refused outright. No Origin at all means a
                // non-browser caller, which the loopback binding already vouches for.
                if (origin is not null && !LanAccess.IsOwnOrigin(origin, Port, LanAccess.OwnAddresses()))
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
            // Client hung up or errored — nothing to send back.
            try { client.Dispose(); } catch { /* already gone */ }
        }
    }

    /// <summary>Writes the 302 that turns a key-carrying URL into a cookie session.</summary>
    private void RespondCookieRedirect(StreamWriter writer, string path)
    {
        lock (writer)
        {
            // The path came off the request line, which is attacker-typed. A leading
            // `//host/...` resolves as a network-path redirect, and browsers treat
            // `\` as `/` in URLs, so `/\host/...` was the same trick wearing a leading
            // slash — both are normalized onto this server, and control characters
            // (which the head's line-splitting already keeps out of the CRLF sense,
            // but never say never) are dropped outright (found in review).
            var safe = new string(path.Replace('\\', '/')
                .Where(c => c > (char)0x1F && c != (char)0x7F).ToArray()).TrimStart('/');
            writer.Write("HTTP/1.1 302 Found\r\n");
            writer.Write($"Location: /{safe}\r\n");
            writer.Write($"Set-Cookie: {LanAccess.CookieName}={_key}; " +
                         $"Max-Age={LanAccess.CookieMaxAgeSeconds}; Path=/; HttpOnly; SameSite=Strict\r\n");
            writer.Write("Cache-Control: no-store\r\n");
            writer.Write("Connection: close\r\n\r\n");
            writer.Flush();
        }
    }

    /// <summary>Holds the connection open and streams change events to the page.</summary>
    /// <remarks>
    /// Ordered so that a client which has seen <c>: connected</c> is already in the
    /// subscriber list: headers first (nothing may interleave before them), then
    /// membership, then the comment. It used to flush the comment before the add,
    /// and a notification landing in that gap was lost — a real path now that the
    /// startup build notifies the moment it finishes, when a page has typically just
    /// subscribed. The writer locks pair with <see cref="NotifyChanged"/>'s, so a
    /// racing notification interleaves as whole frames, never as bytes.
    /// </remarks>
    private void Subscribe(TcpClient client, StreamWriter writer)
    {
        // Every page that has gone away is dropped here, before this one joins them.
        // A reload is a close and a subscribe, so this is the moment that comes around
        // on the quiet evenings when nothing else prunes the list.
        Reap();

        lock (writer)
        {
            writer.Write("HTTP/1.1 200 OK\r\n");
            writer.Write("Content-Type: text/event-stream\r\n");
            writer.Write("Cache-Control: no-cache\r\n");
            writer.Write("Connection: keep-alive\r\n\r\n");
            writer.Flush();
        }

        lock (_subscribers) _subscribers.Add(new Subscriber(client, writer));

        lock (writer)
        {
            writer.Write(": connected\n\n");
            writer.Flush();
        }

        // The socket stays open, held by the Subscriber added above — which is also
        // what closes it, once a write fails or Reap finds the page gone.
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
            foreach (var s in _subscribers) s.Dispose();
            _subscribers.Clear();
        }
        _cts.Dispose();
    }
}
