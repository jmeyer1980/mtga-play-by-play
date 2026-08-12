using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class LogScannerTests
{
    private string _path = null!;

    [TearDown]
    public void TearDown() { if (File.Exists(_path)) File.Delete(_path); }

    private string WriteLog(params string[] lines)
    {
        _path = Path.Combine(Path.GetTempPath(), $"log_{Guid.NewGuid():N}.log");
        File.WriteAllLines(_path, lines);
        return _path;
    }

    [Test]
    public void Scan_skips_non_json_lines_and_counts_them()
    {
        var p = WriteLog(
            "Mono path[0] = 'C:/whatever'",
            "[UnityCrossThreadLogger] noise",
            """{ "timestamp": "1786326812781", "greToClientEvent": { } }""");

        var stats = new ScanStats();
        var got = LogScanner.Scan(p, stats).ToList();

        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(stats.NonJsonLines, Is.EqualTo(2));
        Assert.That(stats.JsonLines, Is.EqualTo(1));
    }

    [Test]
    public void Scan_parses_string_epoch_timestamp_to_long()
    {
        var p = WriteLog("""{ "timestamp": "1786326812781", "a": 1 }""");
        var got = LogScanner.Scan(p, new ScanStats()).Single();
        Assert.That(got.TimestampMs, Is.EqualTo(1786326812781L));
    }

    [Test]
    public void Scan_counts_malformed_json_without_throwing()
    {
        var p = WriteLog(
            """{ "timestamp": "1", "ok": true }""",
            """{ "truncated": """);

        var stats = new ScanStats();
        var got = LogScanner.Scan(p, stats).ToList();

        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(stats.MalformedLines, Is.EqualTo(1));
    }

    [Test]
    public void Scan_records_one_based_line_numbers()
    {
        var p = WriteLog("noise", """{ "timestamp": "5" }""");
        Assert.That(LogScanner.Scan(p, new ScanStats()).Single().LineNumber, Is.EqualTo(2));
    }

    /// <summary>
    /// Arena keeps Player.log open for writing the whole time it runs. Opening it with
    /// the default share mode fails with a sharing violation, which meant you had to
    /// quit the game before you could read a match you had just played.
    /// </summary>
    [Test]
    public void Scan_reads_a_log_that_is_still_open_for_writing()
    {
        var p = WriteLog(
            """{ "timestamp": "1", "greToClientEvent": { } }""",
            """{ "timestamp": "2", "greToClientEvent": { } }""");

        // Hold it exactly as a running logger would: writing, and only tolerating readers.
        using var holder = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.Read);

        var stats = new ScanStats();
        var got = LogScanner.Scan(p, stats).ToList();

        Assert.That(got, Has.Count.EqualTo(2));
        Assert.That(stats.JsonLines, Is.EqualTo(2));
    }

    [Test]
    public void Scan_does_not_block_the_writer_from_appending()
    {
        var p = WriteLog("""{ "timestamp": "1" }""");

        using var holder = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        _ = LogScanner.Scan(p, new ScanStats()).ToList();

        // Arena must still be able to write while we are reading.
        holder.Seek(0, SeekOrigin.End);
        var line = System.Text.Encoding.UTF8.GetBytes("\n{ \"timestamp\": \"2\" }\n");
        Assert.DoesNotThrow(() => { holder.Write(line, 0, line.Length); holder.Flush(); });
    }

    [Test]
    public void Scan_defaults_timestamp_to_zero_when_absent()
    {
        var p = WriteLog("""{ "noTimestamp": 1 }""");
        Assert.That(LogScanner.Scan(p, new ScanStats()).Single().TimestampMs, Is.EqualTo(0L));
    }

    // ---------- Withheld data ----------
    //
    // Arena will not log a GameStateMessage past 50 game objects or 50 annotations. It
    // writes one line of prose in place of the body, and the state that line stands for
    // is gone: nothing downstream can recover it. Before this, those lines fell into
    // the same bucket as Unity's console noise and were counted as NonJsonLines, so a
    // transcript with a hole in it looked exactly like a whole one.
    //
    // The block below is copied from Player-prev.log line 10483, with the account id on
    // the header line replaced — everything the scanner reads is verbatim.

    private const string Marker =
        "[Message summarized because one or more GameStateMessages exceeded " +
        "the 50 GameObject or 50 Annotation limit.]";

    private static readonly string[] RealSummarizedBlock =
    [
        "[UnityCrossThreadLogger]8/11/2026 2:41:39 PM: Match to ACCOUNT: GreToClientEvent",
        Marker,
        "::: GameStateMessage",
        ":: GameObject Count = 77",
        ":: Annotation Count = 3",
        "::: ActionsAvailableReq",
    ];

    [Test]
    public void Scan_turns_a_summarized_message_into_a_gap_carrying_what_was_withheld()
    {
        var p = WriteLog([
            .. RealSummarizedBlock,
            """{ "timestamp": "2", "greToClientEvent": { } }""",
        ]);

        var stats = new ScanStats();
        var got = LogScanner.Scan(p, stats).ToList();

        Assert.That(stats.SummarizedMessages, Is.EqualTo(1));
        Assert.That(got, Has.Count.EqualTo(2), "the gap, then the real envelope after it");

        var gap = LogGaps.Read(got[0].Root);
        Assert.That(gap, Is.Not.Null);
        Assert.That(gap!.Kind, Is.EqualTo(LogGapKind.Summarized));
        Assert.That(gap.LineNumber, Is.EqualTo(2), "the marker's own line, for tracing back");

        // The counts are the difference between "something is missing" and "a whole
        // board state is missing", which is what makes the warning worth reading.
        Assert.That(gap.GameObjects, Is.EqualTo(77));
        Assert.That(gap.Annotations, Is.EqualTo(3));
        Assert.That(gap.Messages, Is.EqualTo(new[] { "GameStateMessage", "ActionsAvailableReq" }));
    }

    [Test]
    public void Scan_survives_a_marker_that_arena_has_reworded()
    {
        // Matched by prefix and keyword, never by the whole sentence. The sub-heading
        // under the marker already differs between 2019 logs ("GREMessageType_...") and
        // 2026 ones, so the wording demonstrably drifts; a guard that stops working
        // when it does is worse than useless, because the page would go back to
        // claiming completeness without anyone noticing the guard had lapsed.
        var p = WriteLog(
            "[Message summarized because it was too big.]",
            "::: GREMessageType_GameStateMessage",
            """{ "timestamp": "2" }""");

        var stats = new ScanStats();
        var gap = LogGaps.Read(LogScanner.Scan(p, stats).First().Root);

        Assert.That(stats.SummarizedMessages, Is.EqualTo(1));
        Assert.That(gap!.GameObjects, Is.Zero, "no counts offered, so none claimed");
        Assert.That(gap.Messages, Is.EqualTo(new[] { "GameStateMessage" }),
            "the 2019 spelling is normalized to the modern one");
    }

    [Test]
    public void Scan_reports_a_marker_that_ends_the_file()
    {
        // Unlike a torn line, a marker is complete on its own: the counts beneath it are
        // a bonus. Dropping it because the file happened to end would lose the match's
        // only evidence that anything was withheld.
        var p = WriteLog(Marker);
        var stats = new ScanStats();

        Assert.That(LogScanner.Scan(p, stats).ToList(), Has.Count.EqualTo(1));
        Assert.That(stats.SummarizedMessages, Is.EqualTo(1));
    }

    [Test]
    public void Scan_does_not_mistake_arena_deck_chatter_for_a_withheld_message()
    {
        // A false positive here is worse than no guard at all: it would put "part of
        // this match is missing" on a transcript that is complete, and a warning that
        // cries wolf gets ignored on the one match where it is true. These are the
        // nearest misses in 67 MB of real log — every line there containing "Summar".
        var p = WriteLog(
            """[UnityCrossThreadLogger]==> DeckGetDeckSummariesV3 {"id":"x","request":"{}"}""",
            "<== DeckGetDeckSummariesV3(x)",
            """{"Summaries":[{"DeckId":"d","Name":"Power to the People"}]}""",
            """{"Courses":[{"CourseDeckSummary":{"DeckId":"d"}}]}""",
            """{"DeckSummariesCacheVersion":0}""",
            "  Overflow Count (too large) 0");

        var stats = new ScanStats();
        _ = LogScanner.Scan(p, stats).ToList();

        Assert.That(stats.SummarizedMessages, Is.Zero);
        Assert.That(stats.TornEnvelopes, Is.Zero);
    }

    [Test]
    public void Scan_reports_an_envelope_that_stops_mid_line()
    {
        // Never seen in the logs this was built against, but it is what a damaged log
        // looks like, and to a parser that only counts parse failures it is invisible.
        var p = WriteLog(
            """{ "timestamp": "1", "greToClientEvent": { "greToClientMe""",
            """{ "timestamp": "2" }""");

        var stats = new ScanStats();
        var got = LogScanner.Scan(p, stats).ToList();

        Assert.That(stats.TornEnvelopes, Is.EqualTo(1));
        Assert.That(LogGaps.Read(got[0].Root)!.Kind, Is.EqualTo(LogGapKind.Torn));
    }

    [Test]
    public void Scan_ignores_a_half_written_line_at_the_end_of_the_file()
    {
        // `watch` re-reads the log every three seconds while a game is being played, so
        // reading it in the middle of Arena's write is routine rather than exceptional,
        // and the next pass sees the same line whole. Flagging that as lost data would
        // put a permanent warning on healthy matches — and because gaps only ever
        // accumulate, it would never come off again.
        var p = WriteLog(
            """{ "timestamp": "1" }""",
            """{ "timestamp": "2", "greToClientEve""");

        var stats = new ScanStats();
        var got = LogScanner.Scan(p, stats).ToList();

        Assert.That(stats.TornEnvelopes, Is.Zero);
        Assert.That(got, Has.Count.EqualTo(1));
        Assert.That(stats.MalformedLines, Is.EqualTo(1), "still counted, just not blamed");
    }

    [Test]
    public void Scan_does_not_treat_pretty_printed_blocks_as_damage()
    {
        // Arena writes its own client-to-server messages indented across many lines, so
        // a lone "{" is normal output, not a truncated envelope. Every one of the 8,121
        // malformed lines in the two local logs is exactly this, which is why the test
        // for damage is "a brace with something after it" rather than "failed to parse".
        var p = WriteLog(
            "{",
            """  "requestId": 42,""",
            """  "payload": {""",
            "  }",
            "}",
            """{ "timestamp": "1" }""");

        var stats = new ScanStats();
        var got = LogScanner.Scan(p, stats).ToList();

        Assert.That(stats.TornEnvelopes, Is.Zero);
        Assert.That(got, Has.Count.EqualTo(1));
    }
}
