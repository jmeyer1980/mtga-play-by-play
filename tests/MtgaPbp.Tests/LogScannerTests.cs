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

    [Test]
    public void Scan_defaults_timestamp_to_zero_when_absent()
    {
        var p = WriteLog("""{ "noTimestamp": 1 }""");
        Assert.That(LogScanner.Scan(p, new ScanStats()).Single().TimestampMs, Is.EqualTo(0L));
    }
}
