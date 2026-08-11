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
/// </remarks>
public sealed class LiveServer(string rootDirectory, int port) : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private readonly List<StreamWriter> _subscribers = [];
    private readonly CancellationTokenSource _cts = new();

    public string Url => $"http://127.0.0.1:{port}/";

    /// <summary>Invoked when the page asks to keep or unkeep a match.</summary>
    public Func<string, bool, bool>? OnFavorite { get; set; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Tells every open page that the archive changed.</summary>
    public void NotifyChanged()
    {
        lock (_subscribers)
        {
            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                try
                {
                    _subscribers[i].Write("event: changed\ndata: 1\n\n");
                    _subscribers[i].Flush();
                }
                catch
                {
                    // The page was closed. Nothing to recover, just stop tracking it.
                    _subscribers.RemoveAt(i);
                }
            }
        }
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
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };

            var requestLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(requestLine)) { client.Dispose(); return; }

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) { client.Dispose(); return; }
            var (method, target) = (parts[0], parts[1]);

            while (reader.ReadLine() is { Length: > 0 }) { /* headers are not needed */ }

            var path = target.Split('?')[0];
            var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";

            if (path == "/api/events") { Subscribe(client, writer); return; }

            if (method == "POST" && path.StartsWith("/api/favorite/", StringComparison.Ordinal))
            {
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

        // Refuse anything that escapes the output directory.
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
            foreach (var s in _subscribers) { try { s.Dispose(); } catch { /* gone */ } }
            _subscribers.Clear();
        }
        _cts.Dispose();
    }
}
