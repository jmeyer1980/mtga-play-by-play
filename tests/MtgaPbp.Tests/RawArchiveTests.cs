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

    private static MatchSlice At(string id, long started) =>
        new(id, started, started + 100, ["""{"a":1}"""], false);

    [Test]
    public void Prune_removes_the_oldest_until_the_cap_is_met()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 5; i++) a.Write(At($"m{i}", i * 1000));

        var removed = a.Prune(keep: 3);

        Assert.That(removed, Is.EquivalentTo(new[] { "m1", "m2" }));
        Assert.That(a.MatchIds(), Is.EquivalentTo(new[] { "m3", "m4", "m5" }));
        Assert.That(File.Exists(Path.Combine(_root, "raw", "m1.json.gz")), Is.False);
    }

    [Test]
    public void Prune_never_removes_a_favourite_however_old()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 5; i++) a.Write(At($"m{i}", i * 1000));
        a.SetFavorite("m1", true);

        a.Prune(keep: 3);

        Assert.That(a.Contains("m1"), Is.True, "the oldest match was favourited");
        Assert.That(a.MatchIds(), Is.EquivalentTo(new[] { "m1", "m4", "m5" }));
    }

    [Test]
    public void Prune_keeps_everything_when_favourites_exceed_the_cap()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 5; i++) { a.Write(At($"m{i}", i * 1000)); a.SetFavorite($"m{i}", true); }

        Assert.That(a.Prune(keep: 2), Is.Empty);
        Assert.That(a.MatchIds().Count(), Is.EqualTo(5));
    }

    [Test]
    public void Prune_does_nothing_when_the_cap_is_unset_or_not_reached()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 3; i++) a.Write(At($"m{i}", i * 1000));

        Assert.That(a.Prune(keep: 0), Is.Empty, "0 means no limit");
        Assert.That(a.Prune(keep: 10), Is.Empty);
        Assert.That(a.MatchIds().Count(), Is.EqualTo(3));
    }

    /// <summary>
    /// Completing a match is a write, but not a new match. Watch mode used to decide
    /// whether to rebuild by comparing the number of archived matches before and
    /// after, so a game captured mid-play was never re-rendered once it finished —
    /// the report sat showing it as still in progress forever.
    /// </summary>
    [Test]
    public void Completing_a_match_is_a_write_even_though_the_count_does_not_change()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: true));
        var countAfterFirst = a.MatchIds().Count();

        var wrote = a.Write(Slice("m1", incomplete: false));

        Assert.That(wrote, Is.True, "the completed match must be written");
        Assert.That(a.MatchIds().Count(), Is.EqualTo(countAfterFirst),
            "and the count is unchanged, which is why it cannot be the rebuild signal");
        Assert.That(a.Meta("m1")!.Incomplete, Is.False);
    }

    [Test]
    public void An_unchanged_in_progress_match_is_not_rewritten_every_poll()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: true));

        Assert.That(a.Write(Slice("m1", incomplete: true)), Is.False,
            "watch polls every few seconds; a game still in progress must stay quiet");
    }

    [Test]
    public void Favourite_survives_recapturing_the_same_match()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: true));
        a.SetFavorite("m1", true);
        a.Write(Slice("m1", incomplete: false));   // completes it

        Assert.That(a.Meta("m1")!.Favorite, Is.True);
    }

    [Test]
    public void Favourite_state_survives_reopening()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));
        a.SetFavorite("m1", true);

        Assert.That(new RawArchive(_root).Meta("m1")!.Favorite, Is.True);
    }

    [Test]
    public void SetFavorite_reports_when_the_match_is_unknown()
    {
        Assert.That(new RawArchive(_root).SetFavorite("nope", true), Is.False);
    }

    /// <summary>
    /// Gap detection arrived after 152 matches had already been archived, and the
    /// markers proving two of them lost data still sit in a Player-prev.log that has
    /// not rotated. Without this, those matches would keep claiming to be complete
    /// forever, which is the exact failure the detection exists to prevent.
    /// </summary>
    [Test]
    public void A_recapture_that_finds_withheld_data_replaces_a_complete_match()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: false));

        var wrote = a.Write(Slice("m1", incomplete: false) with { Gaps = 1 });

        Assert.That(wrote, Is.True);
        Assert.That(a.Meta("m1")!.Gaps, Is.EqualTo(1));
    }

    [Test]
    public void A_match_whose_gaps_are_already_known_is_not_rewritten_every_capture()
    {
        // Gaps only ever accumulate, so a healed match settles after one rewrite. If
        // this rewrote on every pass, `watch` would rebuild the whole site every three
        // seconds for as long as the log holding the marker survived.
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: false) with { Gaps = 1 });

        Assert.That(a.Write(Slice("m1", incomplete: false) with { Gaps = 1 }), Is.False);
    }

    [Test]
    public void Finding_gaps_never_trades_a_finished_match_for_a_partial_one()
    {
        // A match spanning both logs is seen incomplete in one of them. Learning about
        // a gap from that partial view must not cost us the ending we already have —
        // losing the result to gain a warning is a bad trade in both directions.
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: false, """{"full":1}"""));

        var wrote = a.Write(Slice("m1", incomplete: true, """{"partial":1}""") with { Gaps = 1 });

        Assert.That(wrote, Is.False);
        Assert.That(a.Meta("m1")!.Incomplete, Is.False);
        Assert.That(a.ReadLines("m1"), Is.EqualTo(new[] { """{"full":1}""" }));
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
