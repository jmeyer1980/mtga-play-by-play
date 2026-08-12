using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The opening: the die roll, who is on the play, and the opening hands. Every transcript
/// used to begin at turn one, which starts the story after the first three decisions of
/// the game have already been made.
/// </summary>
public class OpeningTests
{
    private static Transcript FromFixture() =>
        new EventExtractor(FixtureCardDb.Load(
                Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures")))
            .Extract("sample-match-0001", GoldenFileTests.ReadFixture());

    private static Transcript With(Opening? opening) =>
        RendererTests.Sample() with { Opening = opening };

    /// <summary>An opening with nothing in it but the mulligan counts.</summary>
    private static Opening Hands(int you, int them) =>
        new([new DieRoll(1, 14), new DieRoll(2, 3)], FirstPlayerSeat: 1,
            new Dictionary<int, int> { [1] = you, [2] = them });

    /// <summary>The narrated lines under the opening heading, in order.</summary>
    private static List<string> OpeningOf(Transcript t)
    {
        var lines = Narrator.Narrate(t, Density.Beats).ToList();
        var start = lines.FindIndex(l => l.IsTurnHeader && l.Text == "Opening");
        return start < 0
            ? []
            : lines.Skip(start + 1).TakeWhile(l => !l.IsTurnHeader).Select(l => l.Text).ToList();
    }

    // ---------- reading it out of the log ----------

    [Test]
    public void Die_roll_is_read_for_both_seats_of_a_real_match()
    {
        // dieRollResultsResp is present exactly once, with exactly two rolls, in all 152
        // archived matches. The sample rolled 11 against 19, so the local player lost it.
        var o = FromFixture().Opening;

        Assert.That(o, Is.Not.Null);
        Assert.That(o!.Rolls.OrderBy(r => r.Seat).Select(r => (r.Seat, r.Value)),
            Is.EqualTo(new[] { (1, 11), (2, 19) }));
        Assert.That(o.WinnerSeat, Is.EqualTo(2));
    }

    [Test]
    public void The_player_on_the_play_is_the_seat_active_on_turn_one()
    {
        var t = FromFixture();

        Assert.That(t.Opening!.FirstPlayerSeat, Is.EqualTo(2));

        // The same seat the turn-one header names. These are read from one place for
        // exactly this reason: an opening that says "Opponent plays first" above a
        // header reading "Turn 1 — You" would be worse than no opening at all.
        var header = Narrator.Narrate(t, Density.Beats)
            .First(l => l.IsTurnHeader && l.Text.StartsWith("Turn 1", StringComparison.Ordinal));
        Assert.That(header.Text, Does.StartWith("Turn 1 — Opponent"));
        Assert.That(OpeningOf(t)[0], Does.Contain("Opponent wins the die roll 19 to 11"));
    }

    [Test]
    public void A_hand_kept_without_mulliganing_is_recorded_as_zero_rather_than_left_out()
    {
        // Arena omits mulliganCount while it is zero, so a kept hand is only ever an
        // absence. That inference was checked against the archive rather than assumed:
        // 29 increments, no explicit zero anywhere. What makes the absence evidence is
        // having read the seat's state at all, which is why both seats are present here
        // even though neither mulliganed.
        var mulligans = FromFixture().Opening!.Mulligans;

        Assert.That(mulligans.Keys.OrderBy(s => s), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(mulligans.Values, Is.All.Zero);
    }

    [Test]
    public void A_log_carrying_none_of_the_opening_reports_no_opening()
    {
        // A slice with players but no die roll, no game state and no turn. Better to
        // have nothing to say than to invent a roll nobody made.
        var raw = new[]
        {
            """
            {"timestamp":"1","matchGameRoomStateChangedEvent":{"gameRoomInfo":{"gameRoomConfig":
            {"reservedPlayers":[{"systemSeatId":1,"playerName":"A","eventId":"Ladder"},
            {"systemSeatId":2,"playerName":"B","eventId":"Ladder"}]}}}}
            """.ReplaceLineEndings(""),
        };

        var t = new EventExtractor(FixtureCardDb.Load(
                Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures")))
            .Extract("empty", raw);

        Assert.That(t.Opening, Is.Null);
        Assert.That(Narrator.Narrate(t, Density.Beats).Any(l => l.Text == "Opening"), Is.False);
    }

    // ---------- what the record will and will not claim ----------

    [Test]
    public void A_tied_die_roll_names_no_winner()
    {
        // Arena re-rolls a tie, so this should never reach a transcript — but "the
        // highest roll" is not a well-defined seat when both are highest, and the wrong
        // answer here is picking one of them anyway.
        var tied = new Opening([new DieRoll(1, 9), new DieRoll(2, 9)], 1, new Dictionary<int, int>());
        Assert.That(tied.WinnerSeat, Is.Null);

        // As is a roll that did not name both players.
        Assert.That(new Opening([new DieRoll(1, 9)], 1, new Dictionary<int, int>()).WinnerSeat,
            Is.Null);
    }

    [Test]
    public void Mulliganing_all_seven_keeps_nothing_rather_than_a_negative_hand()
    {
        // Not hypothetical: one archived opponent mulliganed seven times and kept zero
        // cards. Seven less seven is nought, and an eighth would still be nought.
        Assert.That(Hands(0, 7).Kept(2), Is.Zero);
        Assert.That(Hands(0, 9).Kept(2), Is.Zero);
        Assert.That(Hands(0, 0).Kept(2), Is.EqualTo(Opening.StartingHandSize));
    }

    // ---------- how it reads ----------

    [Test]
    public void The_opening_is_its_own_section_ahead_of_turn_one()
    {
        // Not folded into the turn-one header: the roll and the mulligans happen before
        // turn one, so filing them inside it would put pre-game facts in a turn.
        var lines = Narrator.Narrate(RendererTests.Sample(), Density.Beats).ToList();

        Assert.That(lines[0].IsTurnHeader, Is.True);
        Assert.That(lines[0].Text, Is.EqualTo("Opening"));
        Assert.That(lines[0].Turn, Is.Zero, "turn zero, which no real turn can claim");

        var firstTurn = lines.FindIndex(l => l.Text.StartsWith("Turn 1", StringComparison.Ordinal));
        Assert.That(firstTurn, Is.GreaterThan(0));
        Assert.That(OpeningOf(RendererTests.Sample()),
            Is.EqualTo(new[] { "You win the die roll 14 to 3 and play first", "Both players keep seven" }));
    }

    [Test]
    public void The_roll_winner_taking_the_draw_is_never_reported_as_playing_first()
    {
        // The winner of the roll chooses, and choosing to draw is legal. No archived
        // match diverges — the winner took the play in all 152 — which is precisely why
        // this case is pinned rather than left to be discovered by a wrong transcript.
        var drew = new Opening([new DieRoll(1, 4), new DieRoll(2, 18)],
            FirstPlayerSeat: 1, new Dictionary<int, int> { [1] = 0, [2] = 0 });

        var lines = OpeningOf(With(drew));

        Assert.That(lines[0], Is.EqualTo("Opponent wins the die roll 18 to 4 and chooses to draw"));
        Assert.That(lines[1], Is.EqualTo("You play first"));
        Assert.That(lines, Has.None.Contains("Opponent wins the die roll 18 to 4 and plays first"));

        // The same the other way round, so the verb agreement holds for both subjects.
        var youDrew = new Opening([new DieRoll(1, 18), new DieRoll(2, 4)],
            FirstPlayerSeat: 2, new Dictionary<int, int> { [1] = 0, [2] = 0 });
        Assert.That(OpeningOf(With(youDrew)).Take(2),
            Is.EqualTo(new[] { "You win the die roll 18 to 4 and choose to draw", "Opponent plays first" }));
    }

    [Test]
    public void The_high_roll_is_reported_first_whichever_seat_made_it()
    {
        // "wins the die roll 4 to 18" reads as a loss. The sentence is about the roll
        // being won, so it is ordered high-to-low rather than by seat.
        var lowSeatWon = new Opening([new DieRoll(1, 18), new DieRoll(2, 4)],
            FirstPlayerSeat: 1, new Dictionary<int, int>());
        var highSeatWon = new Opening([new DieRoll(1, 4), new DieRoll(2, 18)],
            FirstPlayerSeat: 2, new Dictionary<int, int>());

        Assert.That(OpeningOf(With(lowSeatWon))[0], Does.Contain("die roll 18 to 4"));
        Assert.That(OpeningOf(With(highSeatWon))[0], Does.Contain("die roll 18 to 4"));
    }

    [Test]
    public void Neither_player_mulliganing_is_one_line_rather_than_two()
    {
        // Said rather than left to silence: a reader could not otherwise tell "both
        // kept" from "the log did not say". But 132 of the 152 archived matches have no
        // mulligan at all, and two lines reporting that nothing happened, in seven
        // transcripts out of eight, is how a section turns into furniture.
        Assert.That(OpeningOf(With(Hands(0, 0))),
            Has.Exactly(1).EqualTo("Both players keep seven"));
        Assert.That(OpeningOf(With(Hands(0, 0))), Has.None.Contains("You keep"));
    }

    [Test]
    public void A_mulligan_reports_what_both_players_kept()
    {
        // Once one player has stumbled the other player's hand is information, so both
        // are named — and the one who kept is named too, rather than implied.
        Assert.That(OpeningOf(With(Hands(1, 0))).Skip(1),
            Is.EqualTo(new[] { "You mulligan to six", "Opponent keeps seven" }));

        Assert.That(OpeningOf(With(Hands(0, 2))).Skip(1),
            Is.EqualTo(new[] { "You keep seven", "Opponent mulligans to five" }));

        // You first, as the life totals in a turn header already are.
        Assert.That(OpeningOf(With(Hands(1, 1))).Skip(1),
            Is.EqualTo(new[] { "You mulligan to six", "Opponent mulligans to six" }));
    }

    [Test]
    public void Every_hand_a_london_mulligan_can_leave_has_a_word_for_it()
    {
        // Nought through seven, so the word list can never be indexed past its end —
        // including the seven-mulligan hand a real opponent actually took.
        var expected = new[] { "seven", "six", "five", "four", "three", "two", "one", "zero" };

        for (var mulligans = 0; mulligans <= Opening.StartingHandSize; mulligans++)
        {
            var line = OpeningOf(With(Hands(mulligans, 1)))[1];
            Assert.That(line, Does.EndWith(expected[mulligans]),
                $"{mulligans} mulligans should leave {expected[mulligans]} cards");
        }
    }

    [Test]
    public void A_roll_whose_game_never_started_says_who_won_and_no_more()
    {
        // A log that stops before a turn is announced knows who won the roll but not
        // what they chose to do with it, and must not fill that in.
        var unopened = new Opening([new DieRoll(1, 3), new DieRoll(2, 12)],
            FirstPlayerSeat: null, new Dictionary<int, int> { [1] = 0, [2] = 0 });

        var lines = OpeningOf(With(unopened));

        Assert.That(lines[0], Is.EqualTo("Opponent wins the die roll 12 to 3"));
        Assert.That(lines, Has.None.Contains("first"));
    }

    [Test]
    public void A_seat_never_read_before_turn_one_gets_no_hand_line()
    {
        // "Opponent keeps seven" rests on having watched the mulligan phase go by with
        // no count appearing. A seat we never saw supports no such claim.
        var oneSided = new Opening([new DieRoll(1, 14), new DieRoll(2, 3)],
            FirstPlayerSeat: 1, new Dictionary<int, int> { [1] = 0 });

        var lines = OpeningOf(With(oneSided));

        Assert.That(lines, Is.EqualTo(new[]
        {
            "You win the die roll 14 to 3 and play first", "You keep seven"
        }));
        Assert.That(lines, Has.None.Contains("Opponent keeps"));
        Assert.That(lines, Has.None.Contains("Both players"));
    }

    [Test]
    public void A_transcript_with_no_opening_grows_no_heading()
    {
        var lines = Narrator.Narrate(With(null), Density.Beats);

        Assert.That(lines.Any(l => l.Text == "Opening"), Is.False);
        Assert.That(lines[0].Text, Does.StartWith("Turn 1"));
        Assert.That(MarkdownRenderer.Render(With(null)), Does.Not.Contain("## Opening"));
    }

    [Test]
    public void Both_densities_carry_the_opening()
    {
        // Nothing about the opening is detail worth hiding, and the two views are meant
        // to be the same match at two levels of zoom.
        foreach (var density in new[] { Density.Beats, Density.Verbose })
            Assert.That(Narrator.Narrate(RendererTests.Sample(), density)
                    .Any(l => l.IsTurnHeader && l.Text == "Opening"),
                Is.True, $"{density} dropped the opening");
    }

    // ---------- how it renders ----------

    [Test]
    public void Markdown_puts_the_opening_between_the_deck_and_the_first_turn()
    {
        var md = MarkdownRenderer.Render(RendererTests.Sample(deck: RendererTests.SampleDeck()))
            .ReplaceLineEndings("\n");

        Assert.That(md.IndexOf("## Opening", StringComparison.Ordinal),
            Is.GreaterThan(md.IndexOf("## Your deck", StringComparison.Ordinal))
              .And.LessThan(md.IndexOf("## Turn 1", StringComparison.Ordinal)));
        Assert.That(md, Does.Contain("\n- You win the die roll 14 to 3 and play first\n"));
    }

    [Test]
    public void The_game_page_gives_the_opening_a_heading_and_a_list()
    {
        var beats = Markup.Parse(GamePageRenderer.Render(RendererTests.Sample()))
            .Descendants("section")
            .Single(s => s.Attribute("data-density")?.Value == "beats");

        var heading = beats.Descendants("h2").First();
        Assert.That(heading.Value, Is.EqualTo("Opening"));
        Assert.That(heading.Attribute("id")?.Value, Is.EqualTo("t0"));

        // A real list, for the same reason a turn is one: entering it announces how many
        // things it holds, and the list quick-keys can step through them.
        var list = beats.Descendants("ol").First();
        Assert.That(list.Attribute("role")?.Value, Is.EqualTo("list"));
        Assert.That(list.Elements("li").Select(li => li.Attribute("class")?.Value),
            Is.All.EqualTo("beat"));
    }

    [Test]
    public void The_opening_reads_aloud_without_any_glyph_to_supply_a_word_for()
    {
        // Deliberate: the die roll is spelled "19 to 11" rather than "19–11". A dash is
        // read inconsistently, and every other notation on the page that a synthesiser
        // mishandles ("×", "·", "→") had to be given a hidden spoken form to fix. Words
        // need none, so the markdown, the page and the clipboard are the same string.
        var lis = Markup.Parse(GamePageRenderer.Render(RendererTests.Sample()))
            .Descendants("section")
            .Single(s => s.Attribute("data-density")?.Value == "beats")
            .Descendants("ol").First().Elements("li").ToList();

        foreach (var li in lis)
        {
            Assert.That(li.Elements(), Is.Empty, "an opening line needs no spoken-form spans");
            Assert.That(Markup.Spoken(li), Is.EqualTo(Markup.Clipboard(li)));
        }
    }

    [Test]
    public void Copying_the_page_reproduces_the_markdown_export_of_the_opening()
    {
        // The clipboard and the .md file are meant to be the same document, and the
        // opening has to land in the same place in both.
        var copied = Markup.Parse(GamePageRenderer.Render(RendererTests.Sample()))
            .Descendants("section")
            .Single(s => s.Attribute("data-density")?.Value == "beats")
            .Descendants("ol").First().Elements("li")
            .Select(Markup.Clipboard)
            .ToList();

        var exported = MarkdownRenderer.Render(RendererTests.Sample())
            .ReplaceLineEndings("\n").Split('\n')
            .SkipWhile(l => !l.StartsWith("## Opening", StringComparison.Ordinal))
            .SkipWhile(l => !l.StartsWith("- ", StringComparison.Ordinal))
            .TakeWhile(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..])
            .ToList();

        Assert.That(copied, Is.EqualTo(exported));
        Assert.That(copied, Has.Count.EqualTo(2));
    }
}
