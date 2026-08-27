using System.Xml.Linq;
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Everything that only becomes true once a match has more than one game, asserted
/// against the archive's one real Bo3.
/// </summary>
/// <remarks>
/// Until this fixture arrived every archived match was a Bo1, so this whole path shipped
/// unexercised and was broken in several ways at once: turn numbers carried across the
/// boundary, so game two's turn one rendered as "Turn 13"; both games claimed the same
/// anchors, putting 26 duplicate ids on a page whose accessibility pass exists largely to
/// keep ids unique; nothing said a new game had started; and the second game's mulligans
/// were silently folded into the first game's opening.
/// <para>
/// The deeper fault was invisible from the markup. Arena hands out instance ids again in
/// each game — 58 of this match's game objects are described under an id the other game
/// also used, and 63 ids reach the event stream in both — while <c>NamePermanents</c>,
/// <c>NameBoards</c> and <c>FillTargets</c> all run after the last message and ask the
/// tracker what each id is called. One tracker for the whole match therefore answered
/// every one of game one's questions out of game two's state, and the transcript
/// reported a Swamp as a 6/6 and had a player casting, resolving and attacking with a
/// Plains. So the fixture is the whole match rather than a trimmed shape: the bugs that
/// mattered most were ones nobody thought to look for.
/// </para>
/// </remarks>
public class MultiGameTests
{
    private static Transcript Bo3() =>
        GoldenFileTests.ExtractFixture(GoldenFileTests.Bo3Fixture, GoldenFileTests.Bo3MatchId);

    private static Transcript Bo1() =>
        GoldenFileTests.ExtractFixture(
            GoldenFileTests.SampleFixture, GoldenFileTests.SampleMatchId);

    private static IReadOnlyList<Line> Lines() => Narrator.Narrate(Bo3(), Density.Beats);

    private static List<string> HeadingsOf(IReadOnlyList<Line> lines) =>
        lines.Where(l => l.IsTurnHeader).Select(l => l.Text).ToList();

    // ---------- the fixture itself ----------

    [Test]
    public void The_bo3_fixture_carries_no_trace_of_either_player()
    {
        // The repository is public and has had a privacy incident. The scrub is asserted
        // here rather than trusted, because a fixture is exactly the kind of file that
        // gets regenerated later by someone who did not know it had to be scrubbed.
        var raw = string.Join("\n", GoldenFileTests.ReadFixture(GoldenFileTests.Bo3Fixture));
        var t = Bo3();

        Assert.That(t.You?.ScreenName, Is.EqualTo("PlayerTwo"));
        Assert.That(t.Opponent?.ScreenName, Is.EqualTo("PlayerOne"));
        Assert.That(t.You?.UserId, Is.EqualTo("USER_2"));
        Assert.That(t.Opponent?.UserId, Is.EqualTo("USER_1"));

        // The real match id is itself an identifier tied to an account's history.
        Assert.That(raw, Does.Not.Contain("1b35a013"));
        Assert.That(raw, Does.Contain(GoldenFileTests.Bo3MatchId));
    }

    [Test]
    public void The_bo3_fixture_really_is_two_games_of_one_match()
    {
        var t = Bo3();

        Assert.That(t.Games, Has.Count.EqualTo(2));
        Assert.That(t.Games.Select(g => g.Number), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(t.Incomplete, Is.False);

        // Both games were conceded by the local player, so the match reads 0-2. This
        // already rendered correctly before games were modelled at all, and has to keep
        // doing so — GamesWon/GamesLost come from the match result, not from the records.
        Assert.That(TranscriptSummary.Result(t), Is.EqualTo("Lost 0-2"));
    }

    // ---------- turn numbering ----------

    [Test]
    public void Each_game_numbers_its_turns_from_one()
    {
        // The defect this replaces: turnInfo carries no turnNumber on either game's first
        // turn, and the extractor's fallback reached for the last turn it had seen. In
        // game two that was game one's thirteenth, so the second game opened on "Turn 13"
        // directly beneath game one's real turn 13.
        var t = Bo3();

        foreach (var g in t.Games)
        {
            var turns = t.Events
                .Where(e => e.GameNumber == g.Number && e.Kind == EventKind.TurnStart)
                .OrderBy(e => e.Seq)
                .Select(e => e.Turn)
                .ToList();

            Assert.That(turns, Is.Not.Empty, $"game {g.Number} opened no turns");
            Assert.That(turns[0], Is.EqualTo(1), $"game {g.Number} must open on turn 1");
            Assert.That(turns, Is.Ordered, $"game {g.Number} turn numbers must not decrease");
            Assert.That(turns, Is.Unique);
        }

        Assert.That(t.Games.Select(g => g.Turns), Is.EqualTo(new[] { 13, 17 }));
    }

    [Test]
    public void The_subtitle_counts_every_turn_of_every_game()
    {
        // The maximum turn number is the length of the longest game, not the length of
        // the match: this one ran 13 turns and then 17 more, and used to claim 17.
        var t = Bo3();

        Assert.That(TranscriptSummary.Turns(t), Is.EqualTo(30));
        Assert.That(TranscriptSummary.Subtitle(t), Does.Contain("30 turns across 2 games"));
        Assert.That(TranscriptSummary.Subtitle(Bo1()), Does.Not.Contain("across"),
            "a single-game match has no games to count");
    }

    // ---------- the boundary ----------

    [Test]
    public void Every_game_gets_a_heading_of_its_own()
    {
        var headings = HeadingsOf(Lines());

        Assert.That(headings, Does.Contain("Game 1"));
        Assert.That(headings, Does.Contain("Game 2"));

        // In order, and each one ahead of the turns it owns.
        Assert.That(headings.IndexOf("Game 1"), Is.LessThan(headings.IndexOf("Game 2")));
        Assert.That(headings.IndexOf("Game 1"), Is.EqualTo(0));
    }

    [Test]
    public void A_game_says_how_it_ended_where_it_ended()
    {
        var t = Bo3();
        var lines = Narrator.Narrate(t, Density.Beats).Select(l => l.Text).ToList();

        var ending = lines.IndexOf("You concede — opponent wins game 1");
        Assert.That(ending, Is.GreaterThan(0), "game one has to say how it finished");
        Assert.That(ending, Is.LessThan(lines.IndexOf("Game 2")),
            "and it belongs to the game that ended, not to the one that follows");

        // The last game's own result is deliberately not narrated: the match-end line is
        // the very next thing said, and would say the same thing twice.
        Assert.That(t.Games[1].ResultLine, Is.EqualTo("You concede — opponent wins game 2"));
        Assert.That(lines, Does.Not.Contain("You concede — opponent wins game 2"));
        Assert.That(lines[^1], Is.EqualTo("You concede — opponent wins the match"));
    }

    [Test]
    public void A_single_game_match_grows_no_game_heading_and_no_game_result_line()
    {
        // The whole shape only appears when there is more than one game. A Bo1 has one
        // result, stated once, by the line that already stated it.
        var lines = Narrator.Narrate(Bo1(), Density.Beats).Select(l => l.Text).ToList();

        Assert.That(lines, Does.Not.Contain("Game 1"));
        Assert.That(lines.Any(l => l.Contains("wins game", StringComparison.Ordinal)), Is.False);
        Assert.That(MarkdownRenderer.Render(Bo1()), Does.Not.Contain("## Game"));
    }

    // ---------- the opening ----------

    [Test]
    public void Every_game_opens_with_its_own_opening_section()
    {
        var lines = Lines();
        var openings = lines.Where(l => l.IsTurnHeader && l.Text == "Opening").ToList();

        Assert.That(openings, Has.Count.EqualTo(2),
            "each game deals its own hands, so each game has its own opening");
        Assert.That(openings.Select(l => l.Game), Is.EqualTo(new[] { 1, 2 }));

        // Each sits under its own game heading rather than all of them at the top.
        var headings = HeadingsOf(lines);
        Assert.That(headings.Take(2), Is.EqualTo(new[] { "Game 1", "Opening" }));
    }

    [Test]
    public void Only_the_first_game_is_decided_by_a_die_roll()
    {
        var t = Bo3();

        Assert.That(t.Games[0].Opening!.Rolls, Has.Count.EqualTo(2));
        Assert.That(t.Games[0].Opening!.WinnerSeat, Is.EqualTo(1));
        Assert.That(t.Games[0].Opening!.ChoosingSeat, Is.Null);

        // Nobody rolls again. The loser of game one chooses, and seat 2 lost it.
        Assert.That(t.Games[1].Opening!.Rolls, Is.Empty);
        Assert.That(t.Games[1].Opening!.WinnerSeat, Is.Null);
        Assert.That(t.Games[1].Opening!.ChoosingSeat, Is.EqualTo(2));
        Assert.That(t.Games[1].Opening!.FirstPlayerSeat, Is.EqualTo(2),
            "and having chosen, took the play");
    }

    [Test]
    public void A_later_games_opening_names_the_chooser_rather_than_a_roll()
    {
        var texts = Lines().Select(l => l.Text).ToList();

        Assert.That(texts, Does.Contain("Opponent wins the die roll 19 to 3 and plays first"));
        Assert.That(texts, Does.Contain("You choose to play first"));

        // Exactly one die roll is mentioned on the page. A second game repeating game
        // one's roll would be inventing a roll that never happened.
        Assert.That(texts.Count(x => x.Contains("die roll", StringComparison.Ordinal)),
            Is.EqualTo(1));
    }

    [Test]
    public void Each_game_reports_its_own_mulligans()
    {
        var t = Bo3();

        // Read per game. One dictionary for the whole match would have folded game two's
        // counts into game one's opening hands, which is what the extractor's old
        // "stop once the turn is known" guard was there to prevent and could not.
        foreach (var g in t.Games)
            Assert.That(g.Opening!.Mulligans.Keys.Order(), Is.EqualTo(new[] { 1, 2 }),
                $"game {g.Number} should know both seats' hands");

        Assert.That(Lines().Count(l => l.Text == "Both players keep seven"), Is.EqualTo(2));
    }

    [Test]
    public void A_game_whose_predecessor_has_no_recorded_winner_claims_no_chooser()
    {
        // Only the rule identifies the chooser — the loser of the previous game — so
        // without a previous result there is nobody to name. Saying "You choose" on a
        // guess would be worse than saying only who ended up on the play.
        var opening = new Opening([], FirstPlayerSeat: 1, new Dictionary<int, int>());
        var t = RendererTests.Sample() with { Opening = opening };

        Assert.That(Narrator.Narrate(t, Density.Beats).Select(l => l.Text),
            Does.Contain("You play first"));
    }

    [Test]
    public void A_chooser_who_takes_the_draw_is_reported_in_two_lines()
    {
        // Never seen in the archive — the one Bo3 has its chooser take the play — but it
        // is legal and it is the case a reader must not misread, so it reads the way the
        // die roll's equivalent does: one line per player, each starting with the player
        // it is about.
        var drew = new Opening([], FirstPlayerSeat: 2, new Dictionary<int, int>(),
                               ChoosingSeat: 1);
        var texts = Narrator.Narrate(RendererTests.Sample() with { Opening = drew }, Density.Beats)
            .Select(l => l.Text).ToList();

        Assert.That(texts, Does.Contain("You choose to draw"));
        Assert.That(texts, Does.Contain("Opponent plays first"));
        Assert.That(texts.IndexOf("You choose to draw"),
            Is.LessThan(texts.IndexOf("Opponent plays first")));
    }

    // ---------- ids reused across games ----------

    [Test]
    public void Instance_ids_really_are_handed_out_again_in_the_next_game()
    {
        // The premise every other fix in this file rests on, asserted rather than
        // assumed. If Arena ever stopped reusing ids this test would fail, and the
        // per-game trackers would become an expensive way to do nothing. Counted over
        // the ids that reach the event stream, which is where the damage was done.
        var reused = SeenInstanceIds(1).Intersect(SeenInstanceIds(2)).ToList();

        Assert.That(reused, Is.Not.Empty,
            "this fixture is here because ids collide between games");
        Assert.That(reused, Has.Count.GreaterThan(20));
    }

    [Test]
    public void A_reused_id_does_not_carry_the_earlier_games_card_into_the_later_one()
    {
        var t = Bo3();

        // The failure this catches was spectacular and completely silent: the deferred
        // naming passes run after the last message, so game one's lines were named out
        // of game two's tracker. A Swamp was reported as a 6/6 creature and a Plains was
        // cast, resolved and attacked with.
        var board = t.Events
            .Where(e => e.Kind == EventKind.BoardSnapshot && e.Detail is not null)
            .Select(e => e.Detail!)
            .ToList();

        Assert.That(board, Is.Not.Empty);
        foreach (var land in new[] { "Swamp", "Plains", "Mountain" })
            Assert.That(board.Any(b => b.Contains(land, StringComparison.Ordinal)), Is.False,
                $"a {land} is not a creature and cannot stand on a board line");

        var cast = t.Events
            .Where(e => e.Kind == EventKind.SpellCast)
            .Select(e => e.SourceName)
            .ToList();
        Assert.That(cast, Does.Not.Contain("Plains"), "a land is played, never cast");
    }

    [Test]
    public void A_spell_that_never_resolved_is_not_resolved_by_the_next_game()
    {
        // FillTargets looks forward from a cast for the resolution that matches its
        // instance id. Unbounded, that search runs into the next game, where the same id
        // belongs to a different card — so a countered spell would report an "after"
        // taken from a card it never touched. Bounded to its own game, every match in
        // the archive still resolves the same way it did.
        var t = Bo3();

        foreach (var e in t.Events.Where(x => x.Kind == EventKind.Resolved))
        {
            var cast = t.Events.LastOrDefault(
                c => c.Kind == EventKind.SpellCast && c.Seq < e.Seq &&
                     c.SourceInstanceId == e.SourceInstanceId);
            if (cast is null) continue;
            Assert.That(cast.GameNumber, Is.EqualTo(e.GameNumber),
                "a cast and its resolution belong to the same game");
        }
    }

    [Test]
    public void The_decklist_counts_a_card_as_seen_when_either_game_saw_it()
    {
        // "Not seen" claims the card sat in the library all match, and a match is both
        // games. Per-game trackers made it possible to answer that from game two alone,
        // which would have been a quieter version of the same lie.
        var t = Bo3();

        Assert.That(t.Deck, Is.Not.Empty);
        Assert.That(t.Deck.Sum(d => d.Count), Is.EqualTo(60));
        Assert.That(t.Deck.Any(d => d.Seen), Is.True);
    }

    // ---------- the clock ----------

    [Test]
    public void The_sideboarding_gap_is_not_reported_as_a_turn_length()
    {
        // Measured, not assumed: 76.5 seconds separate game one's last turn from game
        // two's first, which is past the 60-second mark that would have annotated it.
        // The span is sideboarding and a result screen, and none of it is a turn.
        var t = Bo3();
        var starts = t.Events
            .Where(e => e.Kind == EventKind.TurnStart)
            .OrderBy(e => e.Seq)
            .ToList();

        var last = starts.Last(e => e.GameNumber == 1);
        var first = starts.First(e => e.GameNumber == 2);

        Assert.That((first.TimestampMs - last.TimestampMs) / 1000.0,
            Is.GreaterThan(TurnClock.LongTurnSeconds),
            "if this stops being true the test no longer proves anything");

        Assert.That(TurnClock.Durations(t).ContainsKey(last.Seq), Is.False,
            "the last turn of a game has no successor in its own game to measure against");
        Assert.That(TurnClock.LongTurns(t).ContainsKey(last.Seq), Is.False);

        // And every other turn of game one is still measured, so the exclusion is the
        // boundary and not the game.
        Assert.That(starts.Count(e => e.GameNumber == 1 &&
                                      TurnClock.Durations(t).ContainsKey(e.Seq)),
            Is.EqualTo(12));
    }

    // ---------- the rendered page ----------

    [Test]
    public void The_page_gives_every_heading_of_every_game_a_unique_anchor()
    {
        // 24 anchors appeared twice on this page and two of them three times, which is
        // exactly the fault the accessibility pass had just cleared from 93 of 94 pages.
        var html = GamePageRenderer.Render(Bo3());
        var ids = Markup.Parse(html).Descendants()
            .Select(e => e.Attribute("id")?.Value)
            .Where(id => id is not null)
            .ToList();

        Assert.That(ids, Is.Unique);
        Assert.That(ids, Does.Contain("g2-t1"));
        Assert.That(ids, Does.Contain("v-g2-t1"), "the verbose density keeps its own set");
        Assert.That(ids, Does.Contain("g2"), "and a game is somewhere to link to");
    }

    [Test]
    public void The_page_keeps_its_structural_and_accessibility_invariants()
    {
        var html = GamePageRenderer.Render(Bo3());
        var root = Markup.Parse(html);

        Assert.That(root.Descendants("main").Count(), Is.EqualTo(1));

        // Every turn is exactly one ordered list with its role stated, on a page where
        // the turn headings now come in two runs of one to seventeen.
        var lists = root.Descendants("ol").ToList();
        Assert.That(lists, Is.Not.Empty);
        foreach (var list in lists)
        {
            Assert.That(list.Attribute("class")?.Value, Is.EqualTo("turn"));
            Assert.That(list.Attribute("role")?.Value, Is.EqualTo("list"));
        }

        var levels = Markup.Headings(root).ToList();
        Assert.That(levels[0], Is.EqualTo(1));
        for (var i = 1; i < levels.Count; i++)
            Assert.That(levels[i], Is.LessThanOrEqualTo(levels[i - 1] + 1));
    }

    /// <summary>
    /// A game contains its turns, and heading rank is the only thing that says so to
    /// anything not looking at the page.
    /// </summary>
    /// <remarks>
    /// The check above this one is why that needs asserting separately: "no level is
    /// skipped" is satisfied by every heading being an h2, which is what a three-game
    /// page rendered for as long as multi-game support existed — 1 h1, 56 h2, no h3, and
    /// so no way to tell a game from a turn or a turn from the game it belongs to.
    /// </remarks>
    [Test]
    public void A_game_heading_outranks_the_openings_and_turns_inside_it()
    {
        var root = Markup.Parse(GamePageRenderer.Render(Bo3()));

        var games = root.Descendants("h2").ToList();
        Assert.That(games, Is.Not.Empty);
        foreach (var h in games)
            Assert.That(h.Value, Does.StartWith("Game "),
                "an h2 on a multi-game page is a game and nothing else");

        var inner = root.Descendants("h3").Select(h => h.Value).ToList();
        Assert.That(inner, Is.Not.Empty);
        Assert.That(inner.Any(x => x.StartsWith("Turn ", StringComparison.Ordinal)));
        Assert.That(inner, Does.Contain("Opening"));
    }

    [Test]
    public void A_single_game_page_keeps_every_heading_at_the_same_rank()
    {
        // There is no game to be inside, so demoting turns would leave an h3 with no h2
        // above it — a skipped level, and a worse document than the flat one.
        var root = Markup.Parse(GamePageRenderer.Render(Bo1()));

        Assert.That(root.Descendants("h3"), Is.Empty);
        Assert.That(root.Descendants("h2"), Is.Not.Empty);
    }

    /// <summary>
    /// The copy button collects every heading level the narrator can emit.
    /// </summary>
    /// <remarks>
    /// It selected only h2. When games moved to h2 and their openings and turns were
    /// demoted to h3, the copy silently kept the three game headings and dropped all
    /// twenty-five turn boundaries — a Bo3 pasted into chat arrived as one unbroken run
    /// of beats. The page and the markdown export are meant to be the same document, and
    /// nothing was comparing them at more than one heading level.
    /// <para>
    /// Asserted against the levels the narrator actually produces rather than against a
    /// hardcoded pair, so adding a level fails here instead of silently going uncopied.
    /// </para>
    /// </remarks>
    [Test]
    public void The_copy_script_collects_every_heading_level_the_narrator_emits()
    {
        var levels = Lines().Where(l => l.IsTurnHeader).Select(l => l.Level)
                            .Distinct().Order().ToList();
        Assert.That(levels, Is.EqualTo(new[] { 2, 3 }),
            "a multi-game page nests its turns under its games");

        var selector = System.Text.RegularExpressions.Regex
            .Matches(GamePageRenderer.Render(Bo3()), @"querySelectorAll\('([^']+)'\)")
            .Select(m => m.Groups[1].Value)
            .Single(s => s.Contains("li.beat", StringComparison.Ordinal));

        foreach (var level in levels)
            Assert.That(selector, Does.Contain($"h{level}"),
                $"a heading the narrator emits at level {level} would not be copied");
    }

    [Test]
    public void The_markdown_export_carries_the_same_game_structure_as_the_page()
    {
        // The two are meant to be one document, and the boundary is the newest thing
        // either of them has to say.
        var md = MarkdownRenderer.Render(Bo3()).ReplaceLineEndings("\n");

        List<string> At(string hashes) => md.Split('\n')
            .Where(l => l.StartsWith(hashes + " ", StringComparison.Ordinal))
            .Select(l => l[(hashes.Length + 1)..])
            .ToList();

        // Markdown carries the same nesting the page does, for the same reason: a reader
        // pasting this into a document gets an outline with two games in it rather than
        // forty sibling turns.
        var games = At("##");
        Assert.That(games.Count(h => h == "Game 1"), Is.EqualTo(1));
        Assert.That(games.Count(h => h == "Game 2"), Is.EqualTo(1));
        // The card lists are preamble and belong beside the games rather than inside
        // one, so they are the only other things allowed at this rank. Turns and
        // openings are not.
        Assert.That(games.Where(h => !h.StartsWith("Game ", StringComparison.Ordinal)),
            Is.EqualTo(new[]
            {
                TranscriptSummary.DeckHeading(Bo3()),
                TranscriptSummary.OpponentHeading(Bo3())
            }));

        var inner = At("###");
        Assert.That(inner.Count(h => h == "Opening"), Is.EqualTo(2));
        Assert.That(inner.Count(h => h == "Turn 1 — Opponent  (You 20 · Opponent 20)")
                    + inner.Count(h => h == "Turn 1 — You  (You 20 · Opponent 20)"),
            Is.EqualTo(2), "both games open on a turn one");
    }

    // ---------- the fold ----------

    [Test]
    public void A_run_of_identical_lines_never_folds_across_a_game_boundary()
    {
        // Two games of the same deck really can end and begin on the same sentence.
        // Folding those into "×2" would report one thing happening twice in a row when
        // it was two things in two different games.
        var t = RendererTests.Sample() with
        {
            Games =
            [
                new GameRecord(1, null, 1, 2, null),
                new GameRecord(2, null, 1, 2, null),
            ],
            Events =
            [
                new GameEvent { Seq = 0, Kind = EventKind.TurnStart, GameNumber = 1,
                                Turn = 1, ActorSeat = 1 },
                new GameEvent { Seq = 1, Kind = EventKind.LandPlayed, GameNumber = 1,
                                Turn = 1, ActorSeat = 1, SourceName = "Plains" },
                new GameEvent { Seq = 2, Kind = EventKind.LandPlayed, GameNumber = 2,
                                Turn = 1, ActorSeat = 1, SourceName = "Plains" },
            ]
        };

        var texts = Narrator.Narrate(t, Density.Beats).Select(l => l.Text).ToList();

        Assert.That(texts.Count(x => x == "You play Plains"), Is.EqualTo(2));
        Assert.That(texts.Any(x => x.Contains("×2", StringComparison.Ordinal)), Is.False);
    }

    private static HashSet<int> SeenInstanceIds(int gameNumber)
    {
        var ids = new HashSet<int>();
        foreach (var e in Bo3().Events.Where(e => e.GameNumber == gameNumber))
        {
            if (e.SourceInstanceId is { } s and > 2) ids.Add(s);
            if (e.TargetInstanceId is { } target and > 2) ids.Add(target);
        }
        return ids;
    }
}
