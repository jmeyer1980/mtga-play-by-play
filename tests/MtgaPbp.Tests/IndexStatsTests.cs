using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The record the index reports: overall, by format, and by deck.
/// </summary>
/// <remarks>
/// Every test here is one of issue #13's traps, because each of them is a way to publish
/// a confident wrong number rather than a way to look untidy.
/// </remarks>
public class IndexStatsTests
{
    private static int _n;

    private static MatchSummary Match(
        string result, string format = "Ladder", int turns = 10,
        bool incomplete = false, IReadOnlyList<DeckEntry>? deck = null,
        string? commander = null, bool? onThePlay = null, long at = 0) =>
        new($"m{++_n}", "2026-08-19 00:00", at == 0 ? _n : at, format,
            "Opponent", result, turns, incomplete, [],
            Deck: deck, Commander: commander, OnThePlay: onThePlay);

    private static IReadOnlyList<DeckEntry> Deck(params string[] names) =>
        names.Select(n => new DeckEntry(n, 4, true)).ToList();

    // ---------- the four traps ----------

    /// <summary>
    /// An unfinished match has no result. Counting it as a loss is the shape of mistake
    /// issue #9 was about.
    /// </summary>
    [Test]
    public void An_unfinished_match_is_left_out_of_every_record_rather_than_lost()
    {
        var stats = IndexStats.From([
            Match("Won 2-0"),
            Match("Unfinished", incomplete: true),
            Match("Lost 0-2")
        ]);

        Assert.That(stats.Overall.Won, Is.EqualTo(1));
        Assert.That(stats.Overall.Lost, Is.EqualTo(1));
        Assert.That(stats.Overall.Played, Is.EqualTo(2), "the unfinished one is not a loss");
        Assert.That(stats.Excluded, Is.EqualTo(1), "and it is reported, not dropped in silence");
    }

    /// <summary>
    /// A match whose log carried no decklist still happened. It belongs in the overall
    /// and per-format records, and its absence from the deck table has to be stated.
    /// </summary>
    [Test]
    public void A_match_with_no_decklist_still_counts_and_is_reported_as_unattributed()
    {
        var stats = IndexStats.From([
            Match("Won 2-0", deck: Deck("Hare Apparent", "Plains")),
            Match("Won 2-1"),
            Match("Lost 1-2")
        ]);

        Assert.That(stats.Overall.Played, Is.EqualTo(3), "all three are in the overall record");
        Assert.That(stats.Unattributed, Is.EqualTo(2));
        Assert.That(stats.ByDeck.Sum(d => d.Played), Is.EqualTo(1), "only the one with a list");
    }

    /// <summary>
    /// The on-the-play split has its own denominator: the log did not record an opening
    /// for older matches, and those are not losses of the die roll.
    /// </summary>
    [Test]
    public void The_on_the_play_split_counts_only_matches_that_recorded_an_opening()
    {
        var deck = Deck("Hare Apparent", "Plains");
        var stats = IndexStats.From([
            Match("Won 2-0", deck: deck, onThePlay: true),
            Match("Lost 0-2", deck: deck, onThePlay: false),
            Match("Won 2-1", deck: deck)
        ]);

        var row = stats.ByDeck.Single();
        Assert.That(row.Played, Is.EqualTo(3));
        Assert.That(row.WithOpening, Is.EqualTo(2), "the third recorded no opening");
        Assert.That(row.OnThePlay, Is.EqualTo(1));
    }

    /// <summary>
    /// A streak is a claim about the order matches were played in, and the index is
    /// sorted newest-first.
    /// </summary>
    [Test]
    public void The_longest_streak_is_read_in_the_order_the_matches_were_played()
    {
        // Oldest first: W W W L W. Read backwards this would still be three, so the
        // losing run is placed to make the two readings differ: L W W W W reversed.
        var stats = IndexStats.From([
            Match("Lost 0-2", at: 100),
            Match("Won 2-0", at: 200),
            Match("Won 2-0", at: 300),
            Match("Won 2-0", at: 400),
            Match("Won 2-0", at: 500)
        ]);

        Assert.That(stats.LongestWinStreak, Is.EqualTo(4));
    }

    // ---------- records ----------

    [Test]
    public void A_draw_is_a_match_played_and_not_a_match_won()
    {
        var stats = IndexStats.From([Match("Won 2-0"), Match("Drew 1-1"), Match("Lost 0-2")]);

        Assert.That(stats.Overall.Drawn, Is.EqualTo(1));
        Assert.That(stats.Overall.Played, Is.EqualTo(3));
        Assert.That(stats.Overall.WinRate, Is.EqualTo(1.0 / 3).Within(0.0001));
    }

    [Test]
    public void Formats_are_counted_apart()
    {
        var stats = IndexStats.From([
            Match("Won 2-0", format: "Ladder"),
            Match("Lost 0-2", format: "Ladder"),
            Match("Won 2-0", format: "Brawl_Ladder")
        ]);

        Assert.That(stats.ByFormat.Select(f => f.Name),
            Is.EquivalentTo(new[] { "Ladder", "Brawl_Ladder" }));
        Assert.That(stats.ByFormat.Single(f => f.Name == "Ladder").Played, Is.EqualTo(2));
    }

    [Test]
    public void Median_turns_are_reported_for_wins_and_losses_apart()
    {
        var deck = Deck("Hare Apparent", "Plains");
        var stats = IndexStats.From([
            Match("Won 2-0", deck: deck, turns: 8),
            Match("Won 2-0", deck: deck, turns: 12),
            Match("Lost 0-2", deck: deck, turns: 22)
        ]);

        var row = stats.ByDeck.Single();
        Assert.That(row.TurnsInWins, Is.EqualTo(8), "the lower middle of 8 and 12");
        Assert.That(row.TurnsInLosses, Is.EqualTo(22));
    }

    [Test]
    public void Nothing_with_a_result_means_nothing_to_report()
    {
        Assert.That(IndexStats.From([Match("Unfinished", incomplete: true)]).Any, Is.False);
        Assert.That(IndexStats.From([]).Any, Is.False);
    }
}
