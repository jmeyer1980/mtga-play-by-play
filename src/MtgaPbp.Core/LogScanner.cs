using System.Text.Json;

namespace MtgaPbp.Core;

public sealed class ScanStats
{
    public long JsonLines;
    public long NonJsonLines;
    public long MalformedLines;
}

public static class LogScanner
{
    /// <summary>
    /// Streams parsed JSON envelopes from an Arena log. Non-JSON and malformed
    /// lines are counted and skipped — a log is untrusted input that changes
    /// with every Arena patch, so this must never throw.
    /// </summary>
    public static IEnumerable<LogEnvelope> Scan(string path, ScanStats stats)
    {
        using var reader = new StreamReader(path);
        long lineNo = 0;
        while (reader.ReadLine() is { } raw)
        {
            lineNo++;
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] != '{')
            {
                stats.NonJsonLines++;
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                stats.MalformedLines++;
                continue;
            }

            stats.JsonLines++;
            var root = doc.RootElement.Clone();
            doc.Dispose();
            yield return new LogEnvelope(lineNo, ReadTimestamp(root), root);
        }
    }

    internal static long ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var ts)) return 0;
        return ts.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(ts.GetString(), out var v) => v,
            JsonValueKind.Number when ts.TryGetInt64(out var v) => v,
            _ => 0
        };
    }
}
