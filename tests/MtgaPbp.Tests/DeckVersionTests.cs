using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// A deck's record pools every version of it, which answers "how is this deck doing" and
/// cannot answer "did that change help" (#158).
/// </summary>
public class DeckVersionTests
{
    private static IReadOnlyList<DeckEntry> Deck(params string[] names) =>
        names.Select(n => new DeckEntry(n, 1, Seen: false)).ToList();

    /// <summary>Twelve shared cards, so any two of these lists cluster together.</summary>
    private static string[] Core =>
        ["Swamp", "Duress", "Fell", "Pilfer", "Diresight", "Cut Down",
         "Go for the Throat", "Sengir Vampire", "Typhoid Rats", "Burglar Rat",
         "Phyrexian Arena", "Hero's Downfall"];

    /// <summary>Matches arrive newest first, which is what the ordering leans on.</summary>
    private static IReadOnlyList<DeckCluster> Cluster(
        params (string Id, string[] Extra)[] newestFirst) =>
        DeckIdentity.Cluster(newestFirst.Select(m =>
            (m.Id, Deck([.. Core, .. m.Extra]), (string?)"Gix, Yawgmoth Praetor")));

    [Test]
    public void A_deck_played_as_one_list_has_a_single_version()
    {
        var c = Cluster(("m2", ["Duress"]), ("m1", ["Duress"])).Single();

        Assert.That(c.Versions, Has.Count.EqualTo(1));
        Assert.That(c.Versions[0].MatchIds, Is.EquivalentTo(new[] { "m1", "m2" }));
        Assert.That(c.Versions[0].Added, Is.Empty, "there is nothing before it to differ from");
        Assert.That(c.Versions[0].Removed, Is.Empty);
    }

    /// <summary>Oldest first, whatever order the matches arrived in.</summary>
    [Test]
    public void Versions_run_oldest_first_and_carry_their_own_matches()
    {
        var c = Cluster(
            ("m3", ["Bitter Triumph"]),      // newest
            ("m2", ["Duress"]),
            ("m1", ["Duress"])).Single();    // oldest

        Assert.That(c.Versions, Has.Count.EqualTo(2));
        Assert.That(c.Versions[0].MatchIds, Is.EquivalentTo(new[] { "m1", "m2" }));
        Assert.That(c.Versions[1].MatchIds, Is.EquivalentTo(new[] { "m3" }));
    }

    /// <summary>The whole point: what the edit gave up and what it brought in.</summary>
    [Test]
    public void A_version_says_what_changed_against_the_one_before_it()
    {
        var c = Cluster(
            ("m2", ["Settle the Wreckage"]),
            ("m1", ["Angel of Finality"])).Single();

        Assert.That(c.Versions[1].Added, Is.EqualTo(new[] { "Settle the Wreckage" }));
        Assert.That(c.Versions[1].Removed, Is.EqualTo(new[] { "Angel of Finality" }));
    }

    /// <summary>
    /// A version is a distinct list of names. Running more copies of a card is a real
    /// edit but has nothing to show as added or removed, so it does not start one.
    /// </summary>
    [Test]
    public void Changing_only_how_many_copies_does_not_begin_a_version()
    {
        var one = Deck([.. Core, "Duress"]);
        var two = Deck([.. Core]).Append(new DeckEntry("Duress", 4, Seen: false)).ToList();

        var c = DeckIdentity.Cluster([
            ("m2", two, (string?)"Gix, Yawgmoth Praetor"),
            ("m1", one, (string?)"Gix, Yawgmoth Praetor")]).Single();

        Assert.That(c.Versions, Has.Count.EqualTo(1));
        Assert.That(c.Versions[0].MatchIds, Is.EquivalentTo(new[] { "m1", "m2" }));
    }

    /// <summary>
    /// The record has to be the version's own, not the deck's — that is the entire
    /// reason this exists.
    /// </summary>
    [Test]
    public void Each_version_gets_its_own_record()
    {
        var rows = new List<MatchSummary>
        {
            Row("m3", 3, "Lost 0-1", ["Settle the Wreckage"]),
            Row("m2", 2, "Won 1-0",  ["Angel of Finality"]),
            Row("m1", 1, "Won 1-0",  ["Angel of Finality"]),
        };

        var versions = IndexStats.From(rows).DeckVersions.OrderBy(v => v.Number).ToList();

        Assert.That(versions, Has.Count.EqualTo(2));
        Assert.That(versions[0].Record.Won, Is.EqualTo(2), "the older list won both");
        Assert.That(versions[0].Record.Lost, Is.Zero);
        Assert.That(versions[1].Record.Won, Is.Zero, "the newer list lost its only game");
        Assert.That(versions[1].Record.Lost, Is.EqualTo(1));
        Assert.That(versions[1].Added, Is.EqualTo(new[] { "Settle the Wreckage" }));
    }

    /// <summary>
    /// A deck that never changed contributes nothing. A lone version restating the
    /// deck's own record would be a row of noise under every unchanged deck.
    /// </summary>
    [Test]
    public void A_deck_that_never_changed_contributes_no_versions()
    {
        var rows = new List<MatchSummary>
        {
            Row("m2", 2, "Won 1-0", ["Angel of Finality"]),
            Row("m1", 1, "Lost 0-1", ["Angel of Finality"]),
        };

        Assert.That(IndexStats.From(rows).DeckVersions, Is.Empty);
    }

    /// <summary>And it reaches the page, under the deck it belongs to.</summary>
    [Test]
    public void The_panel_shows_the_versions_and_what_changed()
    {
        var rows = new List<MatchSummary>
        {
            Row("m3", 3, "Lost 0-1", ["Settle the Wreckage"]),
            Row("m2", 2, "Won 1-0",  ["Angel of Finality"]),
            Row("m1", 1, "Won 1-0",  ["Angel of Finality"]),
        };

        var html = IndexRenderer.Render(rows);

        Assert.That(html, Does.Contain("2 versions"));
        Assert.That(html, Does.Contain("out: Angel of Finality"));
        Assert.That(html, Does.Contain("in: Settle the Wreckage"));
        Assert.That(html, Does.Contain("read the record, not the percentage"),
            "a version's sample is small by construction and the page has to say so");
    }

    private static MatchSummary Row(string id, long at, string result, string[] extra) =>
        new(id, "2026-08-30 04:00", at, "Brawl_Ladder", "Opponent", result,
            Turns: 10, Incomplete: false, Cards: [],
            Deck: Deck([.. Core, .. extra]),
            Commander: "Gix, Yawgmoth Praetor");
}
