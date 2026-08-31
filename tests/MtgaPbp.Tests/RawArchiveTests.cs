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

    /// <summary>
    /// The archive's one unrecoverable failure: a slice that lost its contents was
    /// defended by its own ledger entry. The comparisons consulted only the metadata —
    /// which said complete, with a deck and no gaps — so a re-capture carrying the whole
    /// match was refused, and the match could never heal (#131).
    /// </summary>
    [Test]
    public void A_match_whose_slice_was_emptied_is_written_again()
    {
        var a = new RawArchive(_root);
        a.Write(new MatchSlice("m1", 100, 200, ["""{"x":1}"""], false));

        // What a crash mid-write used to leave behind.
        File.WriteAllBytes(Path.Combine(_root, "raw", "m1.json.gz"), []);

        Assert.That(a.Write(new MatchSlice("m1", 100, 200, ["""{"x":1}"""], false)), Is.True,
            "the stored copy has nothing in it, so there is nothing to defend");
        Assert.That(a.ReadLines("m1"), Is.EqualTo(new[] { """{"x":1}""" }));
    }

    /// <summary>
    /// And a slice that is intact is still defended — a re-offer with nothing new in it
    /// is refused, which is what keeps a capture from rewriting the whole archive.
    /// </summary>
    [Test]
    public void An_intact_match_is_still_not_rewritten_for_nothing()
    {
        var a = new RawArchive(_root);
        a.Write(new MatchSlice("m1", 100, 200, ["""{"x":1}"""], false));

        Assert.That(a.Write(new MatchSlice("m1", 100, 200, ["""{"x":1}"""], false)), Is.False);
    }

    /// <summary>
    /// The write goes through a temp file and swaps in, so a crash cannot leave a torn
    /// slice where a whole match was. Asserted on the leftovers rather than by crashing:
    /// nothing may be left behind beside the file.
    /// </summary>
    [Test]
    public void Writing_a_slice_leaves_no_temporary_file_behind()
    {
        var a = new RawArchive(_root);
        a.Write(new MatchSlice("m1", 100, 200, ["""{"x":1}"""], false));

        var raw = Path.Combine(_root, "raw");
        Assert.That(Directory.GetFiles(raw), Has.Exactly(1).Items);
        Assert.That(Directory.GetFiles(raw, "*.tmp"), Is.Empty);
    }

    /// <summary>
    /// A torn slice is an error and not an empty match. The caller has to be able to
    /// tell "this match is damaged" from "there is no file", because only one of those
    /// is worth telling anybody about — and the quiet case is the dangerous one: .NET's
    /// decompressor does not always complain about a stream cut short, it just stops.
    /// </summary>
    [Test]
    public void A_torn_slice_is_an_error_and_not_an_empty_match()
    {
        var a = new RawArchive(_root);
        a.Write(new MatchSlice("m1", 100, 200, ["""{"x":1}"""], false));

        // A gzip header and nothing after it: bytes on disk, no lines out, no complaint
        // from the decompressor. This is the shape that used to make a match disappear.
        File.WriteAllBytes(Path.Combine(_root, "raw", "m1.json.gz"), [0x1f, 0x8b, 0x08, 0x00, 0x99]);
        Assert.That(() => a.ReadLines("m1"), Throws.InstanceOf<InvalidDataException>());

        // And outright rubbish, which the decompressor does object to.
        File.WriteAllText(Path.Combine(_root, "raw", "m1.json.gz"), "not a gzip file at all");
        Assert.That(() => a.ReadLines("m1"), Throws.InstanceOf<InvalidDataException>());
    }

    /// <summary>No file is still no match, and says so without raising anything.</summary>
    [Test]
    public void A_match_with_no_file_reads_as_empty_rather_than_damaged()
    {
        Assert.That(new RawArchive(_root).ReadLines("never-archived"), Is.Empty);
    }

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

    /// <summary>
    /// Answering "what would go" must not be the same act as it going — the archive is
    /// the only copy and File.Delete does not go via the recycle bin (#133).
    /// </summary>
    [Test]
    public void Prunable_names_the_doomed_without_touching_them()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 5; i++) a.Write(At($"m{i}", i * 1000));

        Assert.That(a.Prunable(keep: 3), Is.EqualTo(new[] { "m1", "m2" }), "oldest first");

        // Asked twice, because a question that consumes its subject gives a different
        // answer the second time.
        Assert.That(a.Prunable(keep: 3), Is.EqualTo(new[] { "m1", "m2" }));
        Assert.That(a.MatchIds(), Is.EquivalentTo(new[] { "m1", "m2", "m3", "m4", "m5" }));
        Assert.That(File.Exists(Path.Combine(_root, "raw", "m1.json.gz")), Is.True);
        Assert.That(a.Count, Is.EqualTo(5));
    }

    /// <summary>Favourites are exempt from the preview exactly as they are from the act.</summary>
    [Test]
    public void Prunable_leaves_favourites_out()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 5; i++) a.Write(At($"m{i}", i * 1000));
        a.SetFavorite("m1", true);

        Assert.That(a.Prunable(keep: 3), Does.Not.Contain("m1"));
    }

    /// <summary>
    /// The lost update behind #146: a star set while a capture is in flight used to be
    /// reverted the moment that capture wrote back the ledger it had loaded before the
    /// click. The ledger is read once and written whole, so last writer wins — #140's
    /// atomic write stops torn files, not lost updates.
    /// </summary>
    /// <remarks>
    /// Asserted against a ledger re-read from disk, not against the in-memory copy: the
    /// in-memory one is the thing under test, and asking it whether it remembered would
    /// be asking the witness to alibi itself.
    /// </remarks>
    [Test]
    public void A_star_survives_a_capture_running_beside_it()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 20; i++) a.Write(At($"m{i}", i * 1000));

        // One instance, two writers — the shape `watch` runs: a poll loop archiving
        // matches while a web request toggles a star.
        Parallel.Invoke(
            () => { for (var i = 21; i <= 60; i++) a.Write(At($"m{i}", i * 1000)); },
            () => { for (var i = 1; i <= 20; i++) a.SetFavorite($"m{i}", true); });

        var reloaded = new RawArchive(_root);
        for (var i = 1; i <= 20; i++)
            Assert.That(reloaded.Meta($"m{i}")?.Favorite, Is.True, $"m{i} lost its star");

        // And the captures that ran beside the stars are all there.
        Assert.That(reloaded.MatchIds(), Has.Count.EqualTo(60));
    }

    /// <summary>
    /// MatchIds used to hand out the dictionary's live key view, which throws if anyone
    /// writes while a caller is part-way through it. Nothing could hit that while every
    /// caller had its own ledger — the #146 bug was hiding this one — and a rebuild
    /// enumerating ids while the poll loop captures is precisely the pair that now
    /// shares an instance.
    /// </summary>
    [Test]
    public void Listing_matches_does_not_break_when_one_is_written_mid_enumeration()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 20; i++) a.Write(At($"m{i}", i * 1000));

        Assert.DoesNotThrow(() => Parallel.Invoke(
            () => { for (var i = 21; i <= 60; i++) a.Write(At($"m{i}", i * 1000)); },
            () =>
            {
                for (var round = 0; round < 40; round++)
                    foreach (var id in a.MatchIds()) _ = a.Meta(id);
            }));
    }

    [Test]
    public void Prune_never_removes_a_favourite_however_old()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 5; i++) a.Write(At($"m{i}", i * 1000));
        a.SetFavorite("m1", true);

        a.Prune(keep: 3);

        // The cap counts prunable matches only, so three of m2..m5 survive alongside
        // the favourite. The old expectation here was { m1, m4, m5 } — it asserted that
        // m3 had been deleted, which is the documented contract inverted, so the test
        // named for protecting favourites was in fact protecting the bug that made them
        // cost ordinary matches their place.
        Assert.That(a.Contains("m1"), Is.True, "the oldest match was favourited");
        Assert.That(a.MatchIds(), Is.EquivalentTo(new[] { "m1", "m3", "m4", "m5" }));
    }

    /// <summary>
    /// The arrangement the arithmetic got wrong, and the one the README states outright:
    /// "a cap of 60 with 70 kept matches keeps all 70".
    /// </summary>
    /// <remarks>
    /// With favourites counted into the total, this pruned every ordinary match and then
    /// the one just captured — so a player who had starred `keep` matches lost every
    /// match they played from then on, in the same run that reported capturing it.
    /// </remarks>
    [Test]
    public void A_favourite_never_costs_an_ordinary_match_its_place()
    {
        var a = new RawArchive(_root);
        for (var i = 1; i <= 6; i++) { a.Write(At($"f{i}", i * 1000)); a.SetFavorite($"f{i}", true); }
        for (var i = 1; i <= 4; i++) a.Write(At($"m{i}", 10_000 + i * 1000));

        Assert.That(a.Prune(keep: 6), Is.Empty,
            "six favourites and four ordinary matches, under a cap of six, is nothing to prune");
        Assert.That(a.MatchIds(), Has.Exactly(10).Items);

        // And the cap still bites once the prunable matches alone exceed it.
        for (var i = 5; i <= 9; i++) a.Write(At($"m{i}", 10_000 + i * 1000));
        Assert.That(a.Prune(keep: 6), Is.EquivalentTo(new[] { "m1", "m2", "m3" }));
        Assert.That(a.MatchIds().Count(id => id.StartsWith('f')), Is.EqualTo(6),
            "and never a favourite");
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

    /// <summary>
    /// Same reasoning as the gap rule above, and the same shape of problem: the slicer
    /// used to throw the deck message away, so 128 of the 152 archived matches are
    /// stored without one. For 29 of them the line is still sitting in a log that has
    /// not rotated, and a re-capture can only recover it if the archive will take it.
    /// </summary>
    [Test]
    public void A_recapture_that_finds_the_deck_replaces_a_complete_match_without_one()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: false));

        var wrote = a.Write(Slice("m1", incomplete: false) with { HasDeck = true });

        Assert.That(wrote, Is.True);
        Assert.That(a.Meta("m1")!.HasDeck, Is.True);
    }

    [Test]
    public void A_match_whose_deck_is_already_stored_is_not_rewritten_every_capture()
    {
        // Settles after one rewrite, exactly as gaps do — otherwise `watch` rebuilds
        // the whole site every three seconds for as long as the log survives.
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: false) with { HasDeck = true });

        Assert.That(a.Write(Slice("m1", incomplete: false) with { HasDeck = true }), Is.False);
    }

    [Test]
    public void Finding_the_deck_never_trades_a_finished_match_for_a_partial_one()
    {
        // The deck message opens a match, so the log that holds it is the likeliest one
        // to hold only the first half of that match. Gaining a decklist must not cost
        // us the ending.
        var a = new RawArchive(_root);
        a.Write(Slice("m1", incomplete: false, """{"full":1}"""));

        var wrote = a.Write(
            Slice("m1", incomplete: true, """{"partial":1}""") with { HasDeck = true });

        Assert.That(wrote, Is.False);
        Assert.That(a.Meta("m1")!.Incomplete, Is.False);
    }

    [Test]
    public void Saving_the_ledger_leaves_no_temp_file_behind()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));
        a.Write(Slice("m2"));

        Assert.That(File.Exists(Path.Combine(_root, "index.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(_root, "index.json.tmp")), Is.False);
    }

    [Test]
    public void Saving_the_ledger_keeps_the_previous_generation_as_a_backup()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));   // first save: nothing to back up yet
        a.Write(Slice("m2"));   // second save: the m1-only ledger becomes the backup

        var bak = Path.Combine(_root, "index.json.bak");
        Assert.That(File.Exists(bak), Is.True);
        Assert.That(File.ReadAllText(bak), Does.Contain("m1").And.Not.Contain("m2"));
    }

    [Test]
    public void A_corrupt_ledger_falls_back_to_the_backup()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));
        a.Write(Slice("m2"));
        a.Write(Slice("m3"));   // backup now holds m1+m2

        // A torn write: the power went out mid-WriteAllText.
        File.WriteAllText(Path.Combine(_root, "index.json"), """{"m1": {"Match""");

        var reopened = new RawArchive(_root);

        // The backup is one save behind, and the sweep below re-indexes anything
        // it lacks — so every match is present either way.
        Assert.That(reopened.MatchIds(), Is.EquivalentTo(new[] { "m1", "m2", "m3" }));
        Assert.That(File.Exists(Path.Combine(_root, "index.json.unreadable")), Is.True,
            "the torn file is kept for inspection, not overwritten");
    }

    [Test]
    public void A_lost_ledger_is_rebuilt_from_the_raw_files_themselves()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1", false, """{"x":1}"""));
        a.Write(Slice("m2"));

        // Ledger and backup both gone — the raw files are the only survivors.
        File.Delete(Path.Combine(_root, "index.json"));
        File.Delete(Path.Combine(_root, "index.json.bak"));

        var reopened = new RawArchive(_root);

        Assert.That(reopened.MatchIds(), Is.EquivalentTo(new[] { "m1", "m2" }));
        Assert.That(reopened.ReadLines("m1"), Is.EqualTo(new[] { """{"x":1}""" }));
        Assert.That(reopened.Meta("m1")!.StartedAtMs, Is.Not.Zero,
            "a re-indexed match still orders somewhere: the file's own timestamp");
    }

    [Test]
    public void A_reindexed_match_lets_a_recapture_heal_its_metadata()
    {
        new RawArchive(_root).Write(Slice("m1", incomplete: false) with { HasDeck = true });
        File.Delete(Path.Combine(_root, "index.json"));

        var reopened = new RawArchive(_root);

        // The rebuilt entry claims no deck and no gaps — the least the archive can
        // prove from a filename — so the standard recapture rules are allowed to
        // win again and restore what the ledger used to know.
        Assert.That(reopened.Write(Slice("m1", incomplete: false) with { HasDeck = true }),
            Is.True);
    }

    [Test]
    public void A_failed_swap_does_not_leave_the_temp_file_behind()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));

        // Hold the ledger open so the swap cannot land — the shape of an AV scanner
        // or indexer sitting on the file at the wrong moment.
        using (File.Open(Path.Combine(_root, "index.json"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.That(() => a.SetFavorite("m1", true), Throws.InstanceOf<IOException>());

        Assert.That(File.Exists(Path.Combine(_root, "index.json.tmp")), Is.False,
            "a save that failed must clean up after itself");
    }

    [Test]
    public void A_backup_one_save_behind_does_not_lose_the_newest_match()
    {
        var a = new RawArchive(_root);
        a.Write(Slice("m1"));
        a.Write(Slice("m2"));
        a.Write(Slice("m3"));   // backup holds m1+m2; m3's entry lives only in index.json

        File.WriteAllText(Path.Combine(_root, "index.json"), "not json at all");

        // m3's raw file is on disk, so falling back to the backup must not make the
        // match invisible — the sweep picks it up alongside the backed-up entries.
        Assert.That(new RawArchive(_root).Contains("m3"), Is.True);
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
