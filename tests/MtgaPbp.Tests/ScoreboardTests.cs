using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The block <c>watch</c> pins to the foot of the terminal.
/// </summary>
/// <remarks>
/// It exists because the old output printed one line per match and nothing else: 41
/// lines an evening saying "report updated", with the one line that mattered 38 scrolls
/// out of sight. So the tests that matter are the ones about it staying a fixed size and
/// never wrapping — a block that grows or wraps is the wall of text all over again.
/// </remarks>
public class ScoreboardTests
{
    private static readonly DateTime Updated = new(2026, 8, 22, 16, 52, 14);

    private static SessionRow Session(
        int games = 22, int won = 9, int lost = 13, int drawn = 0,
        params SessionDeck[] decks) =>
        new(0, "2026-08-22 10:36", games, won, lost, drawn,
            decks.Length > 0
                ? decks
                : [new SessionDeck("Elspeth, Storm Slayer", 8, 6), new SessionDeck("Hulk, Gamma Goliath", 1, 4)],
            ["m1"]);

    private static IReadOnlyList<string> Board(
        SessionRow? session = null, IReadOnlyList<Beat>? beats = null,
        string? playing = null, int width = 78, int height = 30, string? nextUp = null) =>
        Scoreboard.Lines(session ?? Session(), beats ?? [], playing, nextUp,
            "http://127.0.0.1:8787/", Updated, width, height);

    private static string Text(IEnumerable<string> lines) => string.Join("\n", lines);

    [Test]
    public void The_session_record_is_the_headline()
    {
        Assert.That(Text(Board()), Does.Contain("22 games · 9-13 · since 10:36"));
    }

    [Test]
    public void A_draw_shows_in_the_headline_record()
    {
        Assert.That(Text(Board(Session(drawn: 1))), Does.Contain("9-13-1"));
    }

    [Test]
    public void One_game_is_singular()
    {
        Assert.That(Text(Board(Session(games: 1, won: 1, lost: 0))), Does.Contain("1 game ·"));
    }

    /// <summary>
    /// A deck at 57% lifetime can be 0-4 tonight, and tonight is the question the board
    /// answers. So the numbers beside each deck are the session's, never the archive's.
    /// </summary>
    [Test]
    public void Each_deck_carries_its_own_share_of_the_night()
    {
        var text = Text(Board());
        Assert.That(text, Does.Match(@"Elspeth, Storm Slayer\s+8-6"));
        Assert.That(text, Does.Match(@"Hulk, Gamma Goliath\s+1-4"));
    }

    [Test]
    public void The_deck_in_play_is_marked_and_the_others_are_not()
    {
        var lines = Board(playing: "Hulk, Gamma Goliath");
        Assert.That(lines.Single(l => l.Contains("Hulk")), Does.Contain("<- playing"));
        Assert.That(lines.Single(l => l.Contains("Elspeth")), Does.Not.Contain("<- playing"));
    }

    [Test]
    public void Only_the_last_few_results_are_shown()
    {
        var beats = Enumerable.Range(0, 10)
            .Select(i => new Beat($"16:{i:00}", "Elspeth", "Won 1-0")).ToList();
        var shown = Board(beats: beats).Count(l => l.Contains("Won 1-0"));
        Assert.That(shown, Is.EqualTo(Scoreboard.Recent));
    }

    [Test]
    public void Before_the_first_match_it_says_so_rather_than_showing_a_blank_record()
    {
        // Called directly: the helper above substitutes a session for null, which is
        // convenient everywhere else and exactly wrong here.
        var lines = Scoreboard.Lines(null, [], null, null, "http://127.0.0.1:8787/", Updated);
        Assert.That(Text(lines), Does.Contain("no matches yet this session"));
    }

    // ---------- staying inside the window ----------

    /// <summary>
    /// A line longer than the terminal wraps, which pushes the block's own height past
    /// what the caller erased and leaves fragments on screen. Clipping is the only
    /// behaviour that keeps the repaint honest.
    /// </summary>
    [Test]
    public void No_line_ever_exceeds_the_width_it_was_given()
    {
        foreach (var width in new[] { 20, 40, 60, 80, 120 })
        {
            var beats = new[] { new Beat("16:52", new string('D', 60), "Won 1-0") };
            var lines = Scoreboard.Lines(
                Session(decks: [new SessionDeck(new string('X', 80), 3, 1)]),
                beats, new string('X', 80), new string('N', 80), "http://127.0.0.1:8787/", Updated, width, 30);

            Assert.That(lines.Select(l => l.Length), Has.All.LessThan(width),
                $"a line wrapped at width {width}");
        }
    }

    /// <summary>
    /// A block taller than its window fights the scrollback, and what gets pushed off is
    /// whatever notable line was printed above it — the exact thing this design is meant
    /// to preserve.
    /// </summary>
    [Test]
    public void A_short_window_truncates_the_deck_list_and_says_how_many_it_dropped()
    {
        var many = Enumerable.Range(0, 12)
            .Select(i => new SessionDeck($"Deck {i}", i, i)).ToArray();
        var lines = Board(Session(decks: many), height: 16);

        Assert.That(lines.Count, Is.LessThanOrEqualTo(16));
        Assert.That(Text(lines), Does.Match(@"\+\d+ more"));
    }

    [Test]
    public void A_tall_window_shows_every_deck_without_a_more_line()
    {
        var decks = Enumerable.Range(0, 5)
            .Select(i => new SessionDeck($"Deck {i}", i, i)).ToArray();
        Assert.That(Text(Board(Session(decks: decks), height: 50)), Does.Not.Contain("more"));
    }

    /// <summary>
    /// Caught in review: the fixed-line count was one short, so a four-deck session with
    /// three results came to thirteen lines inside a twelve-line budget — the guarantee
    /// broken in exactly the case it exists for. The earlier test missed it by passing
    /// no results at all.
    /// </summary>
    [TestCase(16)]
    [TestCase(20)]
    [TestCase(24)]
    [TestCase(30)]
    [TestCase(50)]
    public void The_block_never_takes_more_than_half_the_window(int height)
    {
        var decks = Enumerable.Range(0, 8)
            .Select(i => new SessionDeck($"Deck {i}", i, i)).ToArray();
        var beats = Enumerable.Range(0, Scoreboard.Recent)
            .Select(i => new Beat($"16:{i:00}", "Elspeth", "Won 1-0")).ToList();

        var lines = Board(Session(decks: decks), beats, height: height);

        Assert.That(lines.Count, Is.LessThanOrEqualTo(height / 2),
            $"the block claimed {lines.Count} of a {height}-row window");
    }

    [Test]
    public void The_footer_says_where_the_report_is_and_how_to_stop()
    {
        var text = Text(Board());
        Assert.That(text, Does.Contain("updated 16:52:14"));
        Assert.That(text, Does.Contain("http://127.0.0.1:8787/"));
        Assert.That(text, Does.Contain("Ctrl+C"));
    }

    // ---------- rule state, not only rule events ----------

    /// <summary>
    /// The rotation rule fired twice in one evening, hours apart, into a terminal window
    /// a later rebuild destroyed — and the only way to have known where a deck stood was
    /// to have been watching at the instant it spoke. A number that is simply there can
    /// be looked at.
    /// </summary>
    [Test]
    public void A_deck_on_a_losing_run_says_so_on_the_board()
    {
        var text = Text(Board(Session(decks:
        [
            new SessionDeck("Kitsa, Otterball Elite", 1, 6, Streak: 4),
            new SessionDeck("The Notary Hobbits", 3, 5, Streak: 1)
        ])));

        Assert.That(text, Does.Contain("4 losses in a row"));
        Assert.That(text, Does.Not.Contain("1 loss"), "one loss is not a run");
    }

    /// <summary>
    /// Shown from two rather than three, so the run is visible while it is building
    /// rather than only once it has tripped the rule.
    /// </summary>
    [Test]
    public void A_run_shows_before_it_reaches_the_rotation_threshold()
    {
        var text = Text(Board(Session(decks: [new SessionDeck("Kitsa", 1, 2, Streak: 2)])));
        Assert.That(text, Does.Contain("2 losses in a row"));
    }

    [Test]
    public void The_deck_being_played_is_marked_rather_than_counted()
    {
        var lines = Board(
            Session(decks: [new SessionDeck("Kitsa", 1, 6, Streak: 4)]),
            playing: "Kitsa");

        Assert.That(lines.Single(l => l.Contains("Kitsa")), Does.Contain("<- playing"));
        Assert.That(lines.Single(l => l.Contains("Kitsa")), Does.Not.Contain("in a row"),
            "the deck in hand needs no advice about itself");
    }

    /// <summary>
    /// A standing line, because the answer to "which one next" has to be there when the
    /// question is asked rather than only at the moment a rule happened to trip.
    /// </summary>
    [Test]
    public void The_board_names_what_to_switch_to()
    {
        Assert.That(Text(Board(nextUp: "The Unbeatable Squirrel Girl")),
            Does.Contain("next up if you switch: The Unbeatable Squirrel Girl"));
    }

    [Test]
    public void With_nothing_worth_recommending_the_board_stays_quiet_about_it()
    {
        Assert.That(Text(Board()), Does.Not.Contain("next up"));
    }
}
