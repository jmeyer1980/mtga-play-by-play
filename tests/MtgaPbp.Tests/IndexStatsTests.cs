using System.Globalization;
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

    [Test]
    public void Records_group_by_the_opponents_commander()
    {
        var stats = IndexStats.From([
            Match("Won 2-0") with { OpponentCommanders = ["Kinnan, Bonder Prodigy"] },
            Match("Lost 0-2") with { OpponentCommanders = ["Kinnan, Bonder Prodigy"] },
            Match("Won 2-1") with { OpponentCommanders = ["Katara, Waterbending Master"] },
            Match("Won 2-0")
        ]);

        Assert.That(stats.ByOpponentDeck, Has.Count.EqualTo(2),
            "a match with no commander recorded belongs to no row, not to a pooled one");
        var kinnan = stats.ByOpponentDeck.Single(r => r.Name.Contains("Kinnan"));
        Assert.That((kinnan.Won, kinnan.Lost), Is.EqualTo((1, 1)));
    }

    [Test]
    public void Partner_pairs_are_one_deck_not_two()
    {
        var stats = IndexStats.From([
            Match("Won 2-0") with { OpponentCommanders = ["A", "B"] },
            Match("Lost 0-2") with { OpponentCommanders = ["A", "B"] }
        ]);

        Assert.That(stats.ByOpponentDeck, Has.Count.EqualTo(1));
        Assert.That(stats.ByOpponentDeck[0].Name, Does.Contain("A").And.Contain("B"));
    }

    // ---------- Their cards (#137) ----------

    /// <summary>
    /// A match counts once for every card the opponent showed in it, so the rows add up
    /// past the match count by design; a match that showed nothing contributes to none;
    /// an unfinished one contributes to none either; and basic lands stay out, because
    /// they only restate which colours a loss was to, which the table above already
    /// says — on the archive, Swamp alone was seen in 459 decided matches.
    /// </summary>
    [Test]
    public void Opponent_cards_count_once_per_decided_match_they_were_seen_in()
    {
        var stats = IndexStats.From([
            Match("Won 2-0") with { OpponentCards = ["Get Lost", "Swamp"] },
            Match("Lost 0-2") with { OpponentCards = ["Get Lost", "Sheoldred, the Apocalypse", "Swamp"] },
            Match("Lost 1-2") with { OpponentCards = ["Sheoldred, the Apocalypse"] },
            Match("Lost 0-1", incomplete: true) with { OpponentCards = ["Get Lost"] },
            Match("Won 2-1")
        ]);

        var rows = stats.OpponentCardsMostSeen;
        Assert.That(rows.Select(r => r.Name),
            Is.EqualTo(new[] { "Get Lost", "Sheoldred, the Apocalypse" }),
            "most seen first, ties by name; no basics; nothing from a match that showed nothing");
        Assert.That((rows[0].Won, rows[0].Lost), Is.EqualTo((1, 1)), "the unfinished match is not a loss");
        Assert.That((rows[1].Won, rows[1].Lost), Is.EqualTo((0, 2)));
    }

    /// <summary>
    /// "Which of their cards do I lose to" is not the raw loss count. With a losing
    /// record overall, every staple the opponent shows often comes out ahead on losses
    /// simply by being seen often — ranked that way on the archive, Arcane Signet and
    /// Command Tower topped the list. The ranking is wins short of par instead: how many
    /// fewer wins than the overall rate predicts for that many matches. And a card needs
    /// a minimum sample before it is ranked at all, or every 0-1 would top the list.
    /// </summary>
    [Test]
    public void Cards_you_lose_to_are_ranked_by_wins_short_of_par_over_a_minimum_sample()
    {
        // Ten matches, five won: par is 50%.
        //   Staple  — in all ten, 5-5: exactly par, not bad news.
        //   Sweeper — in five losses and no win: five matches, 2.5 wins short of par.
        //   Fine    — in four wins and one loss: ahead of par.
        //   Rare    — in one loss only: too small a sample to say anything.
        var rows = new List<MatchSummary>();
        for (var i = 0; i < 5; i++)
            rows.Add(Match("Won 2-0") with { OpponentCards = i < 4 ? ["Staple", "Fine"] : ["Staple"] });
        for (var i = 0; i < 5; i++)
            rows.Add(Match("Lost 0-2") with
            { OpponentCards = i == 0 ? ["Staple", "Sweeper", "Rare", "Fine"] : ["Staple", "Sweeper"] });

        var stats = IndexStats.From(rows);

        Assert.That(stats.Overall.WinRate, Is.EqualTo(0.5));
        Assert.That(stats.OpponentCardsLosingTo.Select(r => r.Name), Is.EqualTo(new[] { "Sweeper" }));
        Assert.That(IndexStats.WinsVsPar(stats.OpponentCardsLosingTo[0], 0.5), Is.EqualTo(-2.5));
        Assert.That(IndexStats.OpponentCardMinimum, Is.EqualTo(5));

        // Both tables are capped, so a long archive does not put thousands of rows on
        // the index — 4,852 distinct non-basic cards over 1,455 decided matches on the
        // archive this was built against.
        Assert.That(stats.OpponentCardsMostSeen.Select(r => r.Name),
            Is.EqualTo(new[] { "Staple", "Fine", "Sweeper", "Rare" }));
        Assert.That(IndexStats.OpponentCardRows, Is.EqualTo(25));
    }

    /// <summary>
    /// The raw shortfall grows with the sample, so a staple a little under par over a
    /// hundred games outranks a card that beat you nearly every time — on the archive,
    /// Arcane Signet at 37-59 ranked second by raw shortfall, above Bloom Tender at 2-17.
    /// Scaled to the square root of the sample it drops to the middle of the table, and
    /// the cards that lead are the ones a reader would name: Day of Judgment 3-20, Bloom
    /// Tender 2-17, Get Lost 1-14, Mana Drain 0-10.
    /// </summary>
    [Test]
    public void A_staple_a_little_under_par_does_not_outrank_a_card_that_beats_you()
    {
        // Overall 50%. Staple: 40-60 over a hundred, ten wins short — scaled, 1.0.
        // Sweeper: 0-9, four and a half short — scaled, 1.5. Sweeper leads.
        var rows = new List<MatchSummary>();
        for (var i = 0; i < 40; i++) rows.Add(Match("Won 2-0") with { OpponentCards = ["Staple"] });
        for (var i = 0; i < 60; i++)
            rows.Add(Match("Lost 0-2") with { OpponentCards = i < 9 ? ["Staple", "Sweeper"] : ["Staple"] });
        for (var i = 0; i < 20; i++) rows.Add(Match("Won 2-0"));

        var stats = IndexStats.From(rows);

        Assert.That(stats.Overall.WinRate, Is.EqualTo(0.5));
        Assert.That(stats.OpponentCardsLosingTo.Select(r => r.Name), Is.EqualTo(new[] { "Sweeper", "Staple" }));
        Assert.That(IndexStats.WinsVsPar(stats.OpponentCardsLosingTo[1], 0.5), Is.EqualTo(-10),
            "the staple is further short in absolute wins");
        Assert.That(IndexStats.ShortfallScaled(stats.OpponentCardsLosingTo[0], 0.5), Is.EqualTo(-1.5));
        Assert.That(IndexStats.ShortfallScaled(stats.OpponentCardsLosingTo[1], 0.5), Is.EqualTo(-1.0));
    }

    [Test]
    public void Opponent_card_tables_stop_at_the_row_cap()
    {
        var rows = new List<MatchSummary>();
        for (var i = 0; i < 30; i++)
            rows.Add(Match("Lost 0-2") with { OpponentCards = Enumerable.Range(0, 30).Select(n => $"Card {n:00}").ToList() });
        for (var i = 0; i < 30; i++)
            rows.Add(Match("Won 2-0") with { OpponentCards = [$"Card {i:00}"] });

        var stats = IndexStats.From(rows);

        Assert.That(stats.OpponentCardsMostSeen, Has.Count.EqualTo(IndexStats.OpponentCardRows));
        Assert.That(stats.OpponentCardsLosingTo, Has.Count.EqualTo(IndexStats.OpponentCardRows));
    }

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

    // ---------- Issue 40: what a record sorts by ----------

    /// <summary>
    /// Two records with the same wins and different losses do not sort equal.
    /// </summary>
    /// <remarks>
    /// Caught by Copilot on PR #45. The column was keyed on wins alone, so 10-0 and
    /// 10-10 compared the same and a column labelled "Record" sat unsorted against
    /// itself. It reads as won-lost, so it sorts on the balance.
    /// </remarks>
    [Test]
    public void A_record_sorts_on_its_balance_and_not_on_its_wins_alone()
    {
        var html = IndexRenderer.Render([
            .. Enumerable.Range(0, 10).Select(_ => Match("Won 2-0", format: "Spotless")),
            .. Enumerable.Range(0, 10).Select(_ => Match("Won 2-0", format: "Even")),
            .. Enumerable.Range(0, 10).Select(_ => Match("Lost 0-2", format: "Even"))
        ]);

        var keys = Markup.Parse(html).Descendants("table")
            .Single(t => t.Attribute("id")?.Value == "by-format")
            .Descendants("tbody").Single().Descendants("tr")
            .ToDictionary(tr => tr.Descendants("th").Single().Value.Trim(),
                          tr => tr.Descendants("td").ToList()[1].Attribute("data-key")?.Value);

        // Ten wins apiece; one has ten losses under it and the other none.
        Assert.That(keys["Spotless"], Is.EqualTo("10"));
        Assert.That(keys["Even"], Is.EqualTo("0"));
        Assert.That(keys["Spotless"], Is.Not.EqualTo(keys["Even"]));
    }

    /// <summary>
    /// And it is not a second copy of the win rate, which sits in the next column along.
    /// A rate cannot tell 1-0 from 100-0; a balance cannot tell 6-4 from 60-40. Both
    /// columns earn their place by disagreeing.
    /// </summary>
    [Test]
    public void A_record_and_a_win_rate_do_not_order_decks_the_same_way()
    {
        var html = IndexRenderer.Render([
            Match("Won 2-0", format: "Tiny"),
            .. Enumerable.Range(0, 6).Select(_ => Match("Won 2-0", format: "Busy")),
            .. Enumerable.Range(0, 4).Select(_ => Match("Lost 0-2", format: "Busy"))
        ]);

        var rows = Markup.Parse(html).Descendants("table")
            .Single(t => t.Attribute("id")?.Value == "by-format")
            .Descendants("tbody").Single().Descendants("tr")
            .ToDictionary(tr => tr.Descendants("th").Single().Value.Trim(),
                          tr => tr.Descendants("td").ToList());

        double Key(string name, int column) =>
            double.Parse(rows[name][column].Attribute("data-key")!.Value,
                         CultureInfo.InvariantCulture);

        // Tiny is 1-0: a perfect rate on one match. Busy is 6-4: a worse rate, a better
        // balance. The two columns put them in opposite orders, which is the point.
        // The name is a th, so the tds run Played, Record, Win rate.
        Assert.That(Key("Tiny", 1), Is.LessThan(Key("Busy", 1)), "balance");
        Assert.That(Key("Tiny", 2), Is.GreaterThan(Key("Busy", 2)), "win rate");
    }
}
