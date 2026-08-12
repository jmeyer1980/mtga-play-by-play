using System.Globalization;
using System.Text.Json;

namespace MtgaPbp.Core;

public sealed class ScanStats
{
    public long JsonLines;
    public long NonJsonLines;
    public long MalformedLines;

    /// <summary>Message bodies Arena replaced with a one-line summary.</summary>
    public long SummarizedMessages;

    /// <summary>Envelopes that stopped part-way through their line, mid-file.</summary>
    public long TornEnvelopes;
}

public static class LogScanner
{
    /// <summary>
    /// Streams parsed JSON envelopes from an Arena log. Non-JSON and malformed
    /// lines are counted and skipped — a log is untrusted input that changes
    /// with every Arena patch, so this must never throw.
    /// <para>
    /// Two kinds of line are not skipped quietly, because skipping them quietly is how
    /// a transcript ends up telling someone a story with pieces missing and no sign
    /// that anything is gone. Both are turned into <see cref="LogGaps"/> envelopes and
    /// pushed through the same pipeline as real traffic, so they reach the page.
    /// </para>
    /// </summary>
    public static IEnumerable<LogEnvelope> Scan(string path, ScanStats stats)
    {
        // Arena holds Player.log open for writing the entire time it runs, so the
        // default share mode raises a sharing violation and you would have to quit
        // the game to read a match you just played. ReadWrite tolerates its write
        // handle; Delete lets it rotate the log without being blocked by us.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        long lineNo = 0;

        // A summarization marker is followed by a few "::" lines saying how much was
        // withheld, so the gap is only complete once a line that is not one of those
        // arrives. A torn envelope is held for the opposite reason — see below.
        SummaryBlock? block = null;
        LogGap? torn = null;

        while (reader.ReadLine() is { } raw)
        {
            lineNo++;
            var line = raw.TrimStart();

            // A half-written last line is Arena's writer racing our reader, not lost
            // data: `watch` re-reads the log every three seconds while a game is in
            // progress, so catching one mid-flush is routine and the next pass sees it
            // whole. Any line arriving after it proves the tear was real.
            if (torn is not null)
            {
                stats.TornEnvelopes++;
                yield return Gap(torn);
                torn = null;
            }

            if (block is not null)
            {
                if (line.StartsWith("::", StringComparison.Ordinal)) { block.Read(line); continue; }
                yield return Gap(block.ToGap());
                block = null;
                // ...and then fall through: this line is ordinary traffic again.
            }

            if (IsSummarized(line))
            {
                stats.SummarizedMessages++;
                block = new SummaryBlock(lineNo);
                continue;
            }

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
                // A bare "{" is not damage: Arena pretty-prints its own client-to-server
                // messages across many lines, and those openers are every single
                // malformed line in the 67 MB of logs this was measured against. Only a
                // line with content after the brace is an envelope that got cut off.
                if (line.Length > 1) torn = new LogGap(LogGapKind.Torn, lineNo, 0, 0, []);
                continue;
            }

            stats.JsonLines++;
            var root = doc.RootElement.Clone();
            doc.Dispose();
            yield return new LogEnvelope(lineNo, ReadTimestamp(root), root);
        }

        // A marker at the very end of the file is still a complete signal; a torn line
        // there is the write race, and is deliberately left unreported.
        if (block is not null) yield return Gap(block.ToGap());
    }

    private static LogEnvelope Gap(LogGap gap) =>
        // Timestamp 0: the marker carries none, and slicing already ignores zero rather
        // than letting it drag a match's start time back to the epoch.
        new(gap.LineNumber, 0, LogGaps.ToEnvelope(gap));

    /// <summary>
    /// Recognises the line Arena writes in place of a message it would not log.
    /// </summary>
    /// <remarks>
    /// Matched by prefix and keyword rather than by the whole sentence. The wording has
    /// already drifted once in the wild — the sub-heading beneath it reads
    /// <c>::: GREMessageType_GameStateMessage</c> in 2019 logs and
    /// <c>::: GameStateMessage</c> in 2026 ones — so pinning this to an exact string
    /// would mean the guard silently stops working the next time Arena rephrases it,
    /// which is the failure it exists to prevent. "summarized" appears on no other line
    /// in the logs measured against: the near misses are all "DeckSummaries" and
    /// "CourseDeckSummary", which this does not match.
    /// </remarks>
    internal static bool IsSummarized(string line) =>
        line.StartsWith('[') &&
        line.Contains("summarized", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>::</c> lines under a marker, which say how much was withheld.
    /// </summary>
    private sealed class SummaryBlock(long lineNo)
    {
        private readonly List<string> _messages = [];
        private int _gameObjects;
        private int _annotations;

        public void Read(string line)
        {
            // ":::" names a withheld message type, "::" carries its counts.
            if (line.StartsWith(":::", StringComparison.Ordinal))
            {
                var name = line[3..].Trim();
                // 2019 logs spell these "GREMessageType_GameStateMessage"; recent ones
                // drop the prefix. Store the short form so both read alike.
                const string prefix = "GREMessageType_";
                if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
                if (name.Length > 0) _messages.Add(name);
                return;
            }

            if (line.Contains("GameObject Count", StringComparison.Ordinal))
                _gameObjects = Count(line);
            else if (line.Contains("Annotation Count", StringComparison.Ordinal))
                _annotations = Count(line);
        }

        public LogGap ToGap() =>
            new(LogGapKind.Summarized, lineNo, _gameObjects, _annotations, _messages);

        private static int Count(string line)
        {
            var eq = line.IndexOf('=');
            return eq >= 0 &&
                   int.TryParse(line[(eq + 1)..].Trim(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var n)
                ? n
                : 0;
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
