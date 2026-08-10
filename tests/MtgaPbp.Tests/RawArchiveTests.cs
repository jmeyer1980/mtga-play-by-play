using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class RawArchiveTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() =>
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"arch_{Guid.NewGuid():N}")).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private static MatchSlice Slice(string id, bool incomplete = false, params string[] lines) =>
        new(id, 100, 200, lines.Length == 0 ? ["""{"a":1}"""] : lines, incomplete);

    [Test]
    public void Write_then_ReadLines_round_trips_content()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", false, """{"x":1}""", """{"y":2}"""));

        Assert.That(a.ReadLines("m1"), Is.EqualTo(new[] { """{"x":1}""", """{"y":2}""" }));
    }

    [Test]
    public void Write_is_idempotent_for_a_complete_match()
    {
        var a = new RawArchive(_root);
        Assert.That(a.Write(Slice("m1")), Is.True);
        Assert.That(a.Write(Slice("m1")), Is.False, "second write should be skipped");
    }

    [Test]
    public void Write_overwrites_an_incomplete_match_with_a_complete_one()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: true, """{"partial":1}"""));
        Assert.That(a.Write(Slice("m1", incomplete: false, """{"full":1}""")), Is.True);
        Assert.That(a.ReadLines("m1"), Is.EqualTo(new[] { """{"full":1}""" }));
        Assert.That(a.Meta("m1")!.Incomplete, Is.False);
    }

    [Test]
    public void Ledger_survives_reopening_the_archive()
    {
        new RawArchive(_root).Write(Slice("m1"));
        var reopened = new RawArchive(_root);
        Assert.That(reopened.Contains("m1"), Is.True);
        Assert.That(reopened.MatchIds(), Is.EquivalentTo(new[] { "m1" }));
    }

    [Test]
    public void Meta_records_timestamps()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));
        var meta = a.Meta("m1")!;
        Assert.That(meta.StartedAtMs, Is.EqualTo(100));
        Assert.That(meta.EndedAtMs, Is.EqualTo(200));
    }

    [Test]
    public void Written_payload_is_gzip_compressed()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));
        var file = Path.Combine(_root, "raw", "m1.json.gz");
        Assert.That(File.Exists(file), Is.True);
        using var fs = File.OpenRead(file);
        Assert.That(fs.ReadByte(), Is.EqualTo(0x1f));
        Assert.That(fs.ReadByte(), Is.EqualTo(0x8b));
    }
}
