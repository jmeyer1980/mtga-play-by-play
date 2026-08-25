using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Grouping matches into the sittings they were played in.
/// </summary>
/// <remarks>
/// The report lists 672 matches and never says how a night went, so the last row of an
/// evening is the whole impression it leaves. Every test here is one of the ways that
/// summary could be quietly wrong.
/// </remarks>
public class SessionsTests
{
    private static int _n;
    private static readonly DateTime Origin = new(2026, 8, 19, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>A match <paramref name="minutes"/> after the origin.</summary>
    private static MatchSummary At(double minutes, string result = "Won 1-0", bool incomplete = false) =>
        new($"m{++_n}",
            Origin.AddMinutes(minutes).ToString("yyyy-MM-dd HH:mm"),
            (long)(Origin.AddMinutes(minutes) - DateTime.UnixEpoch).TotalMilliseconds,
            "Brawl_Ladder", "Opponent", result, 10, incomplete, []);

    [Test]
    public void Matches_close_together_are_one_sitting()
    {
        var s = Sessions.From([At(0), At(20), At(45), At(70)]);
        Assert.That(s, Has.Count.EqualTo(1));
        Assert.That(s[0].Games, Is.EqualTo(4));
    }

    /// <summary>
    /// Measured, not chosen: only 12 of the archive's 671 between-match gaps sit between
    /// one and two hours, so the boundary is placed in a hole in the distribution and a
    /// gap either side of it is unambiguous.
    /// </summary>
    [Test]
    public void A_break_longer_than_the_gap_starts_a_new_sitting()
    {
        var s = Sessions.From([At(0), At(30), At(30 + 121), At(30 + 140)]);
        Assert.That(s, Has.Count.EqualTo(2));
        Assert.That(s.Select(x => x.Games), Is.EqualTo(new[] { 2, 2 }), "newest first");
    }

    [Test]
    public void A_gap_exactly_at_the_threshold_is_still_one_sitting()
    {
        Assert.That(Sessions.From([At(0), At(120)]), Has.Count.EqualTo(1),
            "the boundary is a break LONGER than the gap, not one equal to it");
    }

    /// <summary>
    /// A run from 22:00 to 02:00 is one sitting. Labelling by date alone would either
    /// split it across two days or file the whole evening under the day it ended.
    /// </summary>
    [Test]
    public void A_sitting_that_crosses_midnight_stays_one_sitting_named_for_its_start()
    {
        var s = Sessions.From([At(4 * 60), At(5 * 60), At(6 * 60), At(7 * 60), At(8 * 60)]);
        Assert.That(s, Has.Count.EqualTo(1));
        Assert.That(s[0].Started, Does.StartWith("2026-08-19 22:"),
            "named for when it began, not when it ended");
    }

    [Test]
    public void Sessions_come_back_newest_first_the_way_the_index_lists_matches()
    {
        var s = Sessions.From([At(0), At(400), At(800)]);
        Assert.That(s.Select(x => x.StartedAtMs), Is.Ordered.Descending);
    }

    /// <summary>
    /// The case where one loss looks like a trend. Hiding it would hide exactly that.
    /// </summary>
    [Test]
    public void A_single_game_is_still_a_sitting()
    {
        var s = Sessions.From([At(0, "Lost 0-1")]);
        Assert.That(s, Has.Count.EqualTo(1));
        Assert.That(s[0].Games, Is.EqualTo(1));
        Assert.That(s[0].Lost, Is.EqualTo(1));
    }

    /// <summary>
    /// An unfinished match has no result — the mistake issue #9 was about. It still
    /// happened, so it stays in the game count and out of the record.
    /// </summary>
    [Test]
    public void An_unfinished_match_belongs_to_the_sitting_but_not_to_the_record()
    {
        var s = Sessions.From([At(0, "Won 1-0"), At(10, "Lost 0-1", incomplete: true)]);
        Assert.That(s[0].Games, Is.EqualTo(2));
        Assert.That(s[0].Decided, Is.EqualTo(1));
        Assert.That(s[0].Lost, Is.EqualTo(0), "incomplete is not a loss");
        Assert.That(s[0].WinRate, Is.EqualTo(1.0));
    }

    [Test]
    public void A_sitting_with_no_finished_match_reports_no_win_rate_rather_than_zero()
    {
        var s = Sessions.From([At(0, "Lost 0-1", incomplete: true)]);
        Assert.That(s[0].WinRate, Is.Null);
        Assert.That(s[0].Spoken, Does.Contain("none finished"));
    }

    /// <summary>
    /// A summary row that reads as "7 8" tells a synthesiser nothing. The words are what
    /// carry it, the same split the Length column and the deck colours already make.
    /// </summary>
    [Test]
    public void The_record_has_a_spoken_form_in_words()
    {
        var s = Sessions.From([At(0), At(5), At(10, "Lost 0-1")]);
        Assert.That(s[0].Spoken, Is.EqualTo("2 wins, 1 loss"));
    }

    [Test]
    public void One_of_each_is_singular_in_the_spoken_form()
    {
        var s = Sessions.From([At(0), At(5, "Lost 0-1"), At(10, "Drew 1-1")]);
        Assert.That(s[0].Spoken, Is.EqualTo("1 win, 1 loss, 1 draw"));
    }

    [Test]
    public void Sessions_name_the_decks_that_were_played_most_played_first()
    {
        var rows = new[] { At(0), At(5), At(10, "Lost 0-1") };
        var deckOf = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [rows[0].MatchId] = "gix",
            [rows[1].MatchId] = "hares",
            [rows[2].MatchId] = "hares"
        };
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gix"] = "Gix",
            ["hares"] = "Hare Apparent"
        };
        var s = Sessions.From(rows, deckOf, labels);
        Assert.That(s[0].Decks.Select(d => d.Name), Is.EqualTo(new[] { "Hare Apparent", "Gix" }));

        // Each deck carries its own share of the night, not the archive-wide record: a
        // deck at 57% lifetime can be 0-2 this evening, and tonight is the question.
        Assert.That(s[0].Decks[0], Is.EqualTo(new SessionDeck("Hare Apparent", Won: 1, Lost: 1, Streak: 1)),
            "its last game was the loss, so the streak stands at one");
        Assert.That(s[0].Decks[1], Is.EqualTo(new SessionDeck("Gix", Won: 1, Lost: 0)));
    }

    [Test]
    public void A_sitting_whose_matches_carry_no_decklist_names_no_decks()
    {
        var s = Sessions.From([At(0), At(5)], new Dictionary<string, string>());
        Assert.That(s[0].Decks, Is.Empty);
    }

    /// <summary>
    /// Boundaries are a claim about play order, so the input order must not change them.
    /// </summary>
    [Test]
    public void The_order_the_matches_arrive_in_does_not_change_the_sittings()
    {
        var rows = new[] { At(0), At(10), At(200), At(210) };
        var forward = Sessions.From(rows);
        var shuffled = Sessions.From([rows[2], rows[0], rows[3], rows[1]]);
        Assert.That(shuffled.Select(x => x.Games), Is.EqualTo(forward.Select(x => x.Games)));
        Assert.That(shuffled.Select(x => x.StartedAtMs), Is.EqualTo(forward.Select(x => x.StartedAtMs)));
    }

    [Test]
    public void Containing_finds_the_sitting_a_match_belongs_to()
    {
        var rows = new[] { At(0), At(300) };
        var s = Sessions.From(rows);
        Assert.That(Sessions.Containing(s, rows[0].MatchId)!.StartedAtMs, Is.EqualTo(rows[0].SortKey));
        Assert.That(Sessions.Containing(s, "nope"), Is.Null);
    }

    [Test]
    public void No_matches_means_no_sittings()
    {
        Assert.That(Sessions.From([]), Is.Empty);
    }
}
