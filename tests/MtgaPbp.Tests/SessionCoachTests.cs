using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The thing that speaks up between two games.
/// </summary>
/// <remarks>
/// It replaces a rule that fired in 22 of the archive's 28 sittings, so the tests that
/// matter most here are the ones about staying quiet.
/// </remarks>
public class SessionCoachTests
{
    private static int _n;
    private static readonly DateTime Origin = new(2026, 8, 19, 18, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Alpha =
        ["Hare Apparent", "Delney", "Plains", "Ossification", "Dawn of Hope", "Split Up"];
    private static readonly string[] Beta =
        ["Gix", "Swamp", "Cut Down", "Hero's Downfall", "Vraan", "Sengir Vampire"];
    private static readonly string[] Gamma =
        ["Hulk", "Forest", "Mountain", "Serpent Specialist", "Quicksilver", "Hercules"];

    private static MatchSummary At(double minutes, string result, string[] deck, bool incomplete = false) =>
        new($"m{++_n}",
            Origin.AddMinutes(minutes).ToString("yyyy-MM-dd HH:mm"),
            (long)(Origin.AddMinutes(minutes) - DateTime.UnixEpoch).TotalMilliseconds,
            "Brawl_Ladder", "Opponent", result, 10, incomplete, [],
            Deck: deck.Select(n => new DeckEntry(n, 1, true)).ToList());

    private static Nudge? Coach(IReadOnlyList<MatchSummary> rows, IReadOnlySet<string>? silenced = null) =>
        SessionCoach.Check(rows, IndexStats.From(rows), silenced);

    /// <summary>Games of one deck, alternating nothing, all inside one sitting.</summary>
    private static List<MatchSummary> Run(string[] deck, params string[] results)
    {
        var rows = new List<MatchSummary>();
        for (var i = 0; i < results.Length; i++) rows.Add(At(i * 10, results[i], deck));
        return rows;
    }

    // ---------- the rotation nudge ----------

    [Test]
    public void Three_straight_losses_with_one_deck_suggests_a_rotation()
    {
        var n = Coach(Run(Alpha, "Lost 0-1", "Lost 0-1", "Lost 0-1"));
        Assert.That(n, Is.Not.Null);
        Assert.That(n!.Kind, Is.EqualTo(NudgeKind.Rotate));
        Assert.That(n.Text, Does.Contain("0-3"));
    }

    [Test]
    public void Two_losses_says_nothing()
    {
        Assert.That(Coach(Run(Alpha, "Lost 0-1", "Lost 0-1")), Is.Null);
    }

    [Test]
    public void A_win_resets_the_streak()
    {
        Assert.That(Coach(Run(Alpha, "Lost 0-1", "Lost 0-1", "Won 1-0")), Is.Null);
        Assert.That(Coach(Run(Alpha, "Lost 0-1", "Lost 0-1", "Won 1-0", "Lost 0-1")), Is.Null);
    }

    /// <summary>
    /// Two decks alternated through an evening are two runs. Reading the sitting as one
    /// stream would credit a streak to whichever deck happened to be holding the
    /// controller when the third loss landed.
    /// </summary>
    [Test]
    public void Losses_spread_across_two_decks_are_not_one_streak()
    {
        var rows = new List<MatchSummary>
        {
            At(0, "Lost 0-1", Alpha), At(10, "Lost 0-1", Beta),
            At(20, "Lost 0-1", Alpha), At(30, "Lost 0-1", Beta)
        };
        Assert.That(Coach(rows), Is.Null, "each deck is only 0-2");
    }

    /// <summary>
    /// The streak is about tonight. Losses from a previous sitting are a different
    /// evening's problem and must not carry over into this one.
    /// </summary>
    [Test]
    public void A_streak_does_not_reach_back_across_a_break()
    {
        var rows = new List<MatchSummary>
        {
            At(0, "Lost 0-1", Alpha), At(10, "Lost 0-1", Alpha),
            At(10 + 200, "Lost 0-1", Alpha)
        };
        Assert.That(Coach(rows), Is.Null, "only one loss belongs to the sitting in progress");
    }

    /// <summary>
    /// A suggestion that returns every single game is one nobody reads by the third time.
    /// </summary>
    [Test]
    public void A_deck_that_was_declined_stays_quiet()
    {
        var rows = Run(Alpha, "Lost 0-1", "Lost 0-1", "Lost 0-1");
        var slug = IndexStats.From(rows).DeckOf[rows[^1].MatchId];
        Assert.That(Coach(rows, new HashSet<string>(StringComparer.Ordinal) { slug }), Is.Null);
    }

    /// <summary>
    /// An unfinished match has no result, so it neither extends a streak nor breaks one.
    /// </summary>
    [Test]
    public void An_unfinished_match_does_not_count_as_a_loss()
    {
        var rows = new List<MatchSummary>
        {
            At(0, "Lost 0-1", Alpha), At(10, "Lost 0-1", Alpha),
            At(20, "Lost 0-1", Alpha, incomplete: true)
        };
        Assert.That(Coach(rows), Is.Null, "two real losses and one that never finished");
    }

    // ---------- the suggestion itself ----------

    /// <summary>
    /// Rotation exists to get somebody off a deck that is going badly. Pointing them at
    /// another struggling deck would make the suggestion worse than silence.
    /// </summary>
    [Test]
    public void The_deck_suggested_next_is_the_best_one_that_has_left_its_learning_window()
    {
        var rows = new List<MatchSummary>();
        for (var i = 0; i < SessionCoach.LearningWindow; i++)
            rows.Add(At(-5000 + i, i % 4 == 0 ? "Lost 0-1" : "Won 1-0", Beta));      // 75%
        for (var i = 0; i < SessionCoach.LearningWindow; i++)
            rows.Add(At(-3000 + i, i % 2 == 0 ? "Lost 0-1" : "Won 1-0", Gamma));     // 50%
        rows.AddRange(Run(Alpha, "Lost 0-1", "Lost 0-1", "Lost 0-1"));

        var n = Coach(rows);
        Assert.That(n, Is.Not.Null);
        Assert.That(n!.NextUp, Is.Not.Null);
        Assert.That(n.Text, Does.Contain(n.NextUp!));
    }

    /// <summary>
    /// Early on there is no deck worth recommending, and the honest answer is to say
    /// nothing rather than to name one the numbers cannot stand behind.
    /// </summary>
    [Test]
    public void With_nothing_established_the_nudge_names_no_replacement()
    {
        var n = Coach(Run(Alpha, "Lost 0-1", "Lost 0-1", "Lost 0-1"));
        Assert.That(n!.NextUp, Is.Null);
        Assert.That(n.Text, Does.Not.Contain("Next in rotation"));
    }

    // ---------- the learning window and the verdict ----------

    /// <summary>
    /// A deck's first games are what learning it costs. The old rule judged them, which
    /// is the specific reason it stopped anyone learning a deck.
    /// </summary>
    [Test]
    public void A_deck_inside_its_learning_window_is_never_given_a_verdict()
    {
        var results = Enumerable.Range(0, SessionCoach.LearningWindow - 1)
            .Select(i => i % 3 == 0 ? "Won 1-0" : "Lost 0-1").ToArray();
        var n = Coach(Run(Alpha, results));
        Assert.That(n?.Kind, Is.Not.EqualTo(NudgeKind.Verdict));
    }

    [Test]
    public void A_verdict_arrives_on_the_game_that_reaches_the_evaluation_mark()
    {
        // Alternate so no three losses land in a row and the rotation rule stays quiet.
        var results = Enumerable.Range(0, SessionCoach.EvaluationAt)
            .Select(i => i % 2 == 0 ? "Won 1-0" : "Lost 0-1").ToArray();
        var n = Coach(Run(Alpha, results));
        Assert.That(n, Is.Not.Null);
        Assert.That(n!.Kind, Is.EqualTo(NudgeKind.Verdict));
        Assert.That(n.Text, Does.Contain($"{SessionCoach.EvaluationAt} games"));
        Assert.That(n.Text, Does.Contain("holding even"), "50% is neither a rebuild nor a keeper");
    }

    [Test]
    public void A_deck_below_the_rebuild_line_is_told_so_once_it_has_the_games()
    {
        var results = Enumerable.Range(0, SessionCoach.EvaluationAt)
            .Select(i => i % 4 == 0 ? "Won 1-0" : "Lost 0-1").ToArray();   // 25%
        var n = Coach(Run(Alpha, results));
        Assert.That(n!.Kind, Is.EqualTo(NudgeKind.Rotate).Or.EqualTo(NudgeKind.Verdict));
        if (n.Kind == NudgeKind.Verdict) Assert.That(n.Text, Does.Contain("rebuild"));
    }

    // ---------- staying quiet when the night is over ----------

    /// <summary>
    /// The whole value of a nudge is that it lands between two games. A report opened
    /// the next morning greeting somebody with "you are 0-3" is describing an evening
    /// that finished hours ago.
    /// </summary>
    [Test]
    public void A_finished_night_says_nothing_when_asked_about_now()
    {
        var rows = Run(Alpha, "Lost 0-1", "Lost 0-1", "Lost 0-1");
        var later = rows[^1].SortKey + (long)Sessions.Gap.TotalMilliseconds + 1;
        Assert.That(SessionCoach.Check(rows, IndexStats.From(rows), null, later), Is.Null);
    }

    [Test]
    public void The_same_night_still_speaks_up_while_it_is_still_going()
    {
        var rows = Run(Alpha, "Lost 0-1", "Lost 0-1", "Lost 0-1");
        var soon = rows[^1].SortKey + 60_000;
        Assert.That(SessionCoach.Check(rows, IndexStats.From(rows), null, soon), Is.Not.Null);
    }

    [Test]
    public void Nothing_at_all_produces_no_nudge()
    {
        Assert.That(SessionCoach.Check([], IndexStats.From([])), Is.Null);
    }

    [Test]
    public void A_match_with_no_decklist_produces_no_nudge()
    {
        var rows = new List<MatchSummary>
        {
            new($"x{++_n}", "2026-08-19 18:00", 1, "Ladder", "Opponent", "Lost 0-1", 10, false, [])
        };
        Assert.That(Coach(rows), Is.Null, "nothing to attribute a streak to");
    }

    // ---------- how it reaches the page ----------

    /// <summary>
    /// Silence renders nothing at all rather than an empty box, for the same reason the
    /// footnotes below the table are omitted when nothing needs them.
    /// </summary>
    [Test]
    public void With_nothing_to_say_the_page_carries_no_banner()
    {
        var html = IndexRenderer.Render(Run(Alpha, "Won 1-0"), null);
        Assert.That(html, Does.Not.Contain("id=\"coach\""));
    }

    /// <summary>
    /// A status region, never an alert. This arrives between games when nothing is
    /// urgent, and an assertive region interrupts whatever a screen reader is saying —
    /// which on this page is usually the result of the match that just finished.
    /// </summary>
    [Test]
    public void The_banner_is_polite_and_names_a_way_out_of_it()
    {
        var rows = Run(Alpha, "Lost 0-1", "Lost 0-1", "Lost 0-1");
        var html = IndexRenderer.Render(rows, Coach(rows));

        Assert.That(html, Does.Contain("role=\"status\""));
        Assert.That(html, Does.Not.Contain("role=\"alert\""));
        Assert.That(html, Does.Contain("id=\"coach-dismiss\""));
        Assert.That(html, Does.Contain("0-3"));
    }

    /// <summary>
    /// Dismissal is keyed on the exact text, so a nudge about a different deck — or a
    /// longer streak — is a new message and says itself again rather than inheriting a
    /// dismissal meant for something else.
    /// </summary>
    [Test]
    public void The_banner_carries_the_text_dismissal_is_keyed_on()
    {
        var rows = Run(Alpha, "Lost 0-1", "Lost 0-1", "Lost 0-1");
        var nudge = Coach(rows)!;
        Assert.That(IndexRenderer.Render(rows, nudge), Does.Contain($"data-nudge=\"{nudge.Text}\""));
    }
}
