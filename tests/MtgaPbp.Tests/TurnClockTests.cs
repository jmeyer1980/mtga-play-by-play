using System.Xml.Linq;
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Covers the one figure in the transcript the log does not state outright, so these
/// tests lean on the arithmetic being visible: every fixture states its turn starts in
/// whole seconds, and every expectation is a subtraction a reader can check by eye.
/// </summary>
public class TurnClockTests
{
    // An arbitrary but real-shaped epoch-millisecond stamp, so nothing accidentally
    // passes by treating a timestamp as an offset from zero.
    private const long Epoch = 1786326812781;

    private static GameEvent Start(int seq, int turn, int atSecond, int game = 1) => new()
    {
        Seq = seq,
        Kind = EventKind.TurnStart,
        Turn = turn,
        GameNumber = game,
        ActorSeat = turn % 2 == 1 ? 1 : 2,
        TimestampMs = Epoch + atSecond * 1000L
    };

    /// <summary>
    /// A transcript carrying nothing but turn starts, which is all
    /// <see cref="TurnClock"/> reads. Built on the renderer's own sample so the seats
    /// still resolve to "You" and "Opponent" when the narrator gets hold of it.
    /// </summary>
    private static Transcript With(params GameEvent[] turns) =>
        RendererTests.Sample(opening: false) with { Events = turns };

    /// <summary>Turn starts at the given second offsets, one turn each, numbered from 1.</summary>
    private static Transcript At(params int[] seconds) =>
        With([.. seconds.Select((s, i) => Start(i, i + 1, s))]);

    // ---------- the model ----------

    [Test]
    public void A_turn_lasts_until_the_next_turn_starts()
    {
        // Turn 1 opens at 0s, turn 2 at 30s, turn 3 at 100s: 30 and 70.
        var durations = TurnClock.Durations(At(0, 30, 100));

        Assert.That(durations[0].TotalSeconds, Is.EqualTo(30));
        Assert.That(durations[1].TotalSeconds, Is.EqualTo(70));
    }

    [Test]
    public void The_last_turn_of_a_game_is_left_unmeasured()
    {
        // Its span would have to end at the last message of the game rather than at
        // another turn, which sweeps up the result screen and, between games, the whole
        // sideboarding period — the archive holds a final turn with 405 seconds of
        // silence after it. Three turns can therefore only ever yield two durations.
        var durations = TurnClock.Durations(At(0, 30, 100));

        Assert.That(durations, Has.Count.EqualTo(2));
        Assert.That(durations.ContainsKey(2), Is.False, "the third turn has no successor");
    }

    [Test]
    public void A_turn_is_never_measured_across_a_game_boundary()
    {
        // Between the last turn of game 1 and the first of game 2 sits sideboarding,
        // which is not a turn taking a long time.
        var durations = TurnClock.Durations(With(
            Start(0, 1, 0, game: 1),
            Start(1, 2, 20, game: 1),
            Start(2, 1, 400, game: 2),
            Start(3, 2, 430, game: 2)));

        Assert.That(durations.Keys, Is.EquivalentTo(new[] { 0, 2 }));
        Assert.That(durations[0].TotalSeconds, Is.EqualTo(20));
        Assert.That(durations[2].TotalSeconds, Is.EqualTo(30));
    }

    [Test]
    public void Turns_are_keyed_by_sequence_so_a_repeated_turn_number_stays_distinct()
    {
        // Turn numbers restart in a Bo3, and one archived match reports the same turn
        // number twice inside a single game. Keying by turn number would have collapsed
        // both cases into one entry and silently lost a duration.
        var durations = TurnClock.Durations(With(
            Start(0, 7, 0),
            Start(1, 7, 90),
            Start(2, 8, 100)));

        Assert.That(durations.Keys, Is.EquivalentTo(new[] { 0, 1 }));
        Assert.That(durations[0].TotalSeconds, Is.EqualTo(90));
        Assert.That(durations[1].TotalSeconds, Is.EqualTo(10));
    }

    [Test]
    public void Timestamps_that_do_not_advance_produce_no_duration()
    {
        // A match stitched back together across a log rotation is the kind of input
        // that could hand us two stamps out of order. A negative turn length is not a
        // fact about the game, and a zero-length one is not worth reporting either.
        var durations = TurnClock.Durations(With(
            Start(0, 1, 100),
            Start(1, 2, 40),
            Start(2, 3, 40),
            Start(3, 4, 200)));

        Assert.That(durations.ContainsKey(0), Is.False, "backwards");
        Assert.That(durations.ContainsKey(1), Is.False, "identical");
        Assert.That(durations[2].TotalSeconds, Is.EqualTo(160));
    }

    [Test]
    public void Only_turns_at_or_past_the_threshold_count_as_long()
    {
        // The boundary itself counts, so a turn measured at exactly the threshold is
        // reported rather than falling in a crack between the two rules.
        var durations = TurnClock.LongTurns(At(0, 59, 119, 300));

        Assert.That(durations.ContainsKey(0), Is.False, "59s is not a long turn");
        Assert.That(durations[1].TotalSeconds, Is.EqualTo(TurnClock.LongTurnSeconds));
        Assert.That(durations[2].TotalSeconds, Is.EqualTo(181));
    }

    [Test]
    public void Match_length_is_the_last_timestamp_minus_the_first()
    {
        var t = At(0) with { StartedAtMs = Epoch, EndedAtMs = Epoch + 754_000 };
        Assert.That(TurnClock.MatchLength(t), Is.EqualTo(TimeSpan.FromSeconds(754)));
    }

    [Test]
    public void An_incomplete_match_reports_no_length()
    {
        // Last-minus-first measures how much of the match was captured, which is a real
        // number about a different thing. Reported under the same name it would read as
        // the match's length, so it is not reported at all.
        var t = At(0) with
        {
            Incomplete = true,
            StartedAtMs = Epoch,
            EndedAtMs = Epoch + 754_000
        };
        Assert.That(TurnClock.MatchLength(t), Is.Null);
    }

    // ---------- how durations are worded ----------

    [TestCase(9, "9s", "9 seconds")]
    [TestCase(1, "1s", "1 second")]
    [TestCase(60, "1m 0s", "1 minute 0 seconds")]
    [TestCase(61, "1m 1s", "1 minute 1 second")]
    [TestCase(108, "1m 48s", "1 minute 48 seconds")]
    [TestCase(3600, "1h 0m", "1 hour 0 minutes")]
    [TestCase(7565, "2h 6m", "2 hours 6 minutes")]
    public void A_duration_reads_the_same_abbreviated_and_aloud(
        int seconds, string shown, string spoken)
    {
        var d = TimeSpan.FromSeconds(seconds);
        Assert.That(TurnClock.Format(d), Is.EqualTo(shown));
        Assert.That(TurnClock.Spoken(d), Is.EqualTo(spoken));
    }

    [Test]
    public void Past_an_hour_the_seconds_are_dropped_rather_than_rounded_up()
    {
        // 1h 59m 59s is 1h 59m, not 2h. A length is allowed to lose precision at that
        // scale but never to name an hour the match did not reach.
        Assert.That(TurnClock.Format(TimeSpan.FromSeconds(7199)), Is.EqualTo("1h 59m"));
    }

    // ---------- what the transcript says ----------

    private static string Header(Transcript t) =>
        Narrator.Narrate(t, Density.Beats).First(l => l.IsTurnHeader).Text;

    [Test]
    public void A_long_turn_header_says_how_long_the_turn_ran()
    {
        Assert.That(Header(At(0, 108)), Is.EqualTo("Turn 1 — You · 1 minute 48 seconds elapsed"));
    }

    [Test]
    public void An_ordinary_turn_header_says_nothing_about_time()
    {
        // The same reasoning that keeps Narrator.Collapse and the bare-when-printed
        // statline rule: a duration on every turn is a duration on no turn, because the
        // one that mattered no longer stands out.
        Assert.That(Header(At(0, 12)), Is.EqualTo("Turn 1 — You"));
    }

    [Test]
    public void The_time_is_attributed_to_the_turn_and_never_to_a_player()
    {
        // The span covers the active player deciding, the opponent responding, and the
        // animations between them, and nothing in the log separates those. "Opponent
        // took 1m 48s" would be an accusation the data does not support.
        var header = Header(With(Start(0, 4, 0), Start(1, 5, 108)));

        Assert.That(header, Does.Contain("1 minute 48 seconds elapsed"));
        Assert.That(header, Does.Not.Contain("took"));
        Assert.That(header, Does.Not.Contain("thinks"));
        Assert.That(header, Does.Not.Contain("stall"));
    }

    [Test]
    public void The_duration_survives_the_run_folding_that_collapses_repeated_lines()
    {
        // Collapse compares whole line texts and never folds turn headers, but two
        // consecutive turns that happened to run the same length now produce headers
        // differing only in their number — worth pinning, because a fold here would
        // silently delete a turn.
        var lines = Narrator.Narrate(At(0, 100, 200, 300), Density.Beats);

        Assert.That(lines.Count(l => l.IsTurnHeader), Is.EqualTo(4));
        Assert.That(lines.Count(l => l.Text.Contains("elapsed", StringComparison.Ordinal)),
            Is.EqualTo(3), "every turn but the last is measurable and all three ran long");
    }

    // ---------- the caveat that has to travel with the number ----------

    private static Transcript LongMatch() => At(0, 108, 130);

    [Test]
    public void The_timing_note_appears_only_when_a_turn_actually_carries_a_duration()
    {
        Assert.That(TranscriptSummary.TimingNote(At(0, 12)), Is.Null,
            "nothing to explain when no turn is annotated");
        Assert.That(TranscriptSummary.TimingNote(LongMatch()),
            Is.EqualTo(TranscriptSummary.TimingNoteText));
    }

    [Test]
    public void The_note_says_the_time_is_not_one_players_thinking()
    {
        // This is the whole reason the note exists: a duration sitting on the
        // opponent's turn invites exactly the reading the measurement cannot support.
        Assert.That(TranscriptSummary.TimingNoteText, Does.Contain("both players"));
        Assert.That(TranscriptSummary.TimingNoteText, Does.Contain("not any one player's"));
    }

    /// <summary>
    /// A mark on one turn and not the next reads as a clock that works sometimes —
    /// two archived matches had turn one as their only slow turn, which reads exactly
    /// like "only the first turn is timed". The note has to state which turns get a
    /// mark, and its threshold is the rule's own constant so the sentence cannot say
    /// one number while the code applies another.
    /// </summary>
    [Test]
    public void The_note_says_which_turns_carry_a_time_at_all()
    {
        Assert.That(TranscriptSummary.TimingNoteText,
            Does.Contain($"{TurnClock.LongTurnSeconds} seconds"));
        Assert.That(TranscriptSummary.TimingNoteText,
            Does.Contain("the last turn of a game is never timed"));
    }

    [Test]
    public void The_page_and_the_markdown_export_carry_the_note_in_the_same_place()
    {
        var md = MarkdownRenderer.Render(LongMatch()).ReplaceLineEndings("\n");
        Assert.That(md, Does.Contain($"*{TranscriptSummary.TimingNoteText}*"));

        // Ahead of the first turn heading, so a reader meets the caveat before the
        // number it qualifies.
        Assert.That(md.IndexOf(TranscriptSummary.TimingNoteText, StringComparison.Ordinal),
            Is.LessThan(md.IndexOf("## Turn 1", StringComparison.Ordinal)));

        var note = Markup.Parse(GamePageRenderer.Render(LongMatch()))
            .Descendants("p").Single(p => p.Attribute("id")?.Value == "timing-note");
        Assert.That(note.Value, Is.EqualTo(TranscriptSummary.TimingNoteText));
    }

    [Test]
    public void Copying_the_page_reproduces_the_markdown_export_of_the_note()
    {
        // The clipboard and the .md file are meant to be the same document, so the
        // copy has to gather the note as well as the warnings above it.
        var html = GamePageRenderer.Render(LongMatch());
        Assert.That(html, Does.Contain("getElementById('timing-note')"));

        var note = Markup.Parse(html).Descendants("p")
            .Single(p => p.Attribute("id")?.Value == "timing-note");
        Assert.That($"*{Markup.Clipboard(note)}*",
            Is.EqualTo($"*{TranscriptSummary.TimingNoteText}*"));
    }

    [Test]
    public void A_turn_duration_reaches_speech_as_words_rather_than_as_an_abbreviation()
    {
        // "1m 48s" is a run of letters and digits sitting next to a turn number, which
        // is why the narrated text spells it out instead. The separator before it still
        // has to become a pause, or the life score and the duration run together.
        var heading = Markup.Parse(GamePageRenderer.Render(LongMatch()))
            .Descendants("h2").First(h => h.Value.Contains("Turn 1", StringComparison.Ordinal));

        Assert.That(Markup.Spoken(heading), Does.Contain(", 1 minute 48 seconds elapsed"));
        Assert.That(Markup.Spoken(heading), Does.Not.Contain("1m 48s"));
    }

    // ---------- match length on the index ----------

    /// <summary>
    /// The one row of the matches table. Named by the table's id rather than by being
    /// the only one on the page: the stats panel puts its own tables above it.
    /// </summary>
    private static XElement IndexRow(Transcript t) =>
        Markup.Parse(IndexRenderer.Render([IndexRenderer.Summarize(t)]))
            .Descendants("table").Single(x => x.Attribute("id")?.Value == "rows")
            .Descendants("tbody").Single().Descendants("tr").Single();

    [Test]
    public void The_index_shows_match_length_abbreviated_and_spells_it_out_for_speech()
    {
        var t = At(0) with { StartedAtMs = Epoch, EndedAtMs = Epoch + 754_000 };
        var cell = IndexRow(t).Descendants("td").Last();

        Assert.That(Markup.Clipboard(cell), Is.EqualTo("12m 34s"));
        Assert.That(Markup.Spoken(cell).Trim(), Is.EqualTo("12 minutes 34 seconds"));
    }

    [Test]
    public void An_incomplete_match_leaves_the_length_cell_empty()
    {
        // Under a column headed "Length", how much of the match was captured would be
        // read as how long the match ran.
        var t = At(0) with
        {
            Incomplete = true,
            StartedAtMs = Epoch,
            EndedAtMs = Epoch + 754_000
        };
        Assert.That(IndexRow(t).Descendants("td").Last().Value, Is.Empty);
    }

    [Test]
    public void The_subtitle_names_which_number_is_the_length()
    {
        // "13 turns · 4 minutes" leaves a reader working out what the second field
        // counts; "13 turns in 4 minutes" does not.
        var t = At(0, 30) with { StartedAtMs = Epoch, EndedAtMs = Epoch + 754_000 };
        Assert.That(TranscriptSummary.Subtitle(t), Does.Contain("2 turns in 12 minutes 34 seconds"));

        Assert.That(TranscriptSummary.Subtitle(t with { Incomplete = true }),
            Does.Contain("2 turns").And.Not.Contain(" in 12 minutes"));
    }
}
