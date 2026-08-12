using MtgaPbp.Core;

namespace MtgaPbp.Render;

public enum Density { Beats, Verbose }

/// <summary>One narrated line, or one of the headings the lines hang under.</summary>
/// <param name="IsTurnHeader">
/// True for every heading the narrator emits, not only turn headings — the opening and,
/// in a multi-game match, the game headings are all headings to the renderers.
/// </param>
/// <param name="Game">
/// Which game this line belongs to, or zero on a transcript that carries no game
/// records. Only used to keep <see cref="Narrator.Collapse"/> from folding a line at the
/// end of one game into the identical line at the start of the next.
/// </param>
/// <param name="Anchor">
/// The heading's id, less the prefix that tells the two densities apart. Empty on
/// everything that is not a heading.
/// </param>
/// <param name="Level">
/// The heading's rank, and meaningless on anything that is not a heading. A single-game
/// match keeps every heading at 2, directly under the match title. A multi-game match
/// puts its games at 2 and demotes their openings and turns to 3, so that a game is the
/// container its divider already looks like.
/// <para>
/// This used to be flat — every heading an <c>h2</c>, game headings included — on the
/// grounds that a run of same-rank headings still walks past "Game 2" on the way into
/// the second game's turn one, and that the page shrinks <c>h2</c> below the browser
/// default so an unstyled <c>h3</c> would come out larger than the game heading above
/// it. Walking past a heading is not the same as being able to tell what contains what:
/// rank is the only thing that says a turn belongs to a game, and flattening it left 56
/// equal-rank headings on a three-game page. The styling objection was real and is
/// answered by styling <c>h3</c> rather than by leaving the structure wrong — single-game
/// pages emit no <c>h3</c> at all, so the added rule cannot reach them.
/// </para>
/// </param>
public sealed record Line(
    int Turn, int Indent, string Text, bool IsTurnHeader, bool IsBoard = false,
    int Game = 0, string Anchor = "", int Level = 2);

public static class Narrator
{
    private static readonly HashSet<EventKind> VerboseOnly =
        [EventKind.PhaseChange, EventKind.ManaPaid, EventKind.Unknown];

    /// <summary>
    /// The heading the opening sits under. Its own section rather than part of the
    /// turn-one header: the roll and the mulligans happen before turn one, not during
    /// it, so folding them into that header would file pre-game facts inside a turn and
    /// leave the header carrying three unrelated claims. It also survives a match that
    /// never reaches turn one — the archive has one, conceded during the mulligan —
    /// where a turn-one header has nothing to attach to.
    /// </summary>
    private const string OpeningHeading = "Opening";

    /// <summary>
    /// The anchor the opening takes in a single-game match. Turn zero's, which no turn
    /// can ever claim — Arena numbers turns from one — so it needs no scheme of its own.
    /// </summary>
    private const string OpeningAnchor = "t0";

    /// <summary>
    /// Hand sizes as words. The rest of a line is prose, and "mulligan to 6" reads like
    /// a stat where "mulligan to six" reads like the sentence a player would say. It is
    /// also one less number for a synthesiser to run together with the die roll's.
    /// Indexed by cards kept, so it covers nought through a full opening hand.
    /// </summary>
    private static readonly string[] CardCounts =
        ["zero", "one", "two", "three", "four", "five", "six", "seven"];

    /// <summary>
    /// The whole transcript as lines. A single-game match narrates exactly as it always
    /// has: one opening, then its turns. A match with more than one game gets a heading
    /// per game, each with its own opening and its own turn one, and the result of every
    /// game but the last stated where that game ends — the match-end line already says
    /// how the last one went.
    /// </summary>
    public static IReadOnlyList<Line> Narrate(Transcript t, Density density)
    {
        var lines = new List<Line>();

        // More than one game is what changes the shape of the page, so it is what the
        // extra structure keys off. A Bo3 that only ever reached game one is a
        // single-game transcript, and reads like one.
        var multi = t.Games.Count > 1;

        // Everything below a game heading is one rank deeper than the game, and a match
        // with no game headings has nothing to be deeper than.
        var under = multi ? 3 : 2;

        // Both densities get the opening. Nothing about it is detail you would want
        // hidden, and the two views are meant to be the same match at two zoom levels.
        if (!multi) AppendOpening(lines, t, t.Opening, game: 0, OpeningAnchor, under);

        // Only the turns worth remarking on, so a header carries a duration where that
        // is the interesting thing about the turn and stays quiet everywhere else.
        var longTurns = TurnClock.LongTurns(t);

        var game = 0;

        foreach (var e in t.Events.OrderBy(x => x.Seq))
        {
            if (multi && e.GameNumber != game)
            {
                // The game that just ended says how it ended, where it ended. Only a game
                // with another after it ever reaches here, which is exactly right: the
                // last game's ending is the match's ending, and the match-end event says
                // it a few lines later.
                if (t.Games.FirstOrDefault(g => g.Number == game)?.ResultLine is { } ending)
                    lines.Add(new Line(0, 1, ending, IsTurnHeader: false, Game: game));

                game = e.GameNumber;
                var record = t.Games.FirstOrDefault(g => g.Number == game);
                lines.Add(new Line(0, 0, $"Game {game}", IsTurnHeader: true, Game: game,
                                   Anchor: $"g{game}", Level: 2));
                AppendOpening(lines, t, record?.Opening, game, $"g{game}-open", under);
            }

            if (density == Density.Beats && VerboseOnly.Contains(e.Kind)) continue;
            if (density == Density.Beats && IsUnnamed(e)) continue;
            var text = Phrase(e, t);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Appended here rather than inside Phrase, which is a switch over how each
            // kind of event reads and has no business knowing the clock.
            if (e.Kind == EventKind.TurnStart) text += Elapsed(e, longTurns);

            var header = e.Kind == EventKind.TurnStart;
            lines.Add(new Line(
                e.Turn,
                header ? 0 : 1,
                text,
                header,
                e.Kind == EventKind.BoardSnapshot,
                Game: e.GameNumber,
                Anchor: header ? (multi ? $"g{game}-t{e.Turn}" : $"t{e.Turn}") : "",
                Level: under));
        }
        return Collapse(lines);
    }

    /// <summary>
    /// The opening heading and its lines, or nothing at all when the log carried none of
    /// it — which is what keeps a game with no opening from growing an empty heading.
    /// </summary>
    private static void AppendOpening(
        List<Line> lines, Transcript t, Opening? opening, int game, string anchor, int level)
    {
        if (OpeningLines(t, opening) is not { Count: > 0 } texts) return;

        lines.Add(new Line(0, 0, OpeningHeading, IsTurnHeader: true, Game: game,
                           Anchor: anchor, Level: level));
        foreach (var text in texts)
            lines.Add(new Line(0, 1, text, IsTurnHeader: false, Game: game));
    }

    /// <summary>
    /// Folds runs of the identical line into one with a count. A single card can
    /// trigger nine times in a row or make four tokens back to back, and printing
    /// each is how a transcript turns into a wall. Turn headers are never folded.
    /// </summary>
    /// <remarks>
    /// A run never crosses a game boundary, even where the two games would supply
    /// identical text. Two games of the same deck really can end and begin on the same
    /// sentence, and "You play Plains ×2" spanning a game boundary would report one
    /// thing happening twice in a row when it was two things in two different games.
    /// The game headings between them already break every run in practice; the check is
    /// here so that stays true of the fold itself rather than of the layout around it.
    /// </remarks>
    private static List<Line> Collapse(List<Line> lines)
    {
        var result = new List<Line>(lines.Count);
        for (var i = 0; i < lines.Count;)
        {
            var line = lines[i];
            var run = 1;
            while (!line.IsTurnHeader &&
                   i + run < lines.Count &&
                   lines[i + run].Text == line.Text &&
                   lines[i + run].IsTurnHeader == line.IsTurnHeader &&
                   lines[i + run].Game == line.Game)
                run++;

            result.Add(run == 1 ? line : line with { Text = $"{line.Text} ×{run}" });
            i += run;
        }
        return result;
    }

    /// <summary>
    /// True when the event's subject resolved only to a bare instance id — a token
    /// that left play before the client ever described it, typically. "#332 is put
    /// into the graveyard" is noise, so beats drop it; verbose keeps it so the gap
    /// stays visible when debugging.
    /// </summary>
    private static bool IsUnnamed(GameEvent e) =>
        (e.SourceName is not null && CardNames.IsPlaceholder(e.SourceName))
        || (e.TargetName is not null && CardNames.IsPlaceholder(e.TargetName));

    /// <summary>
    /// What happened before turn one, in the order it happened: the die roll, then who
    /// is on the play, then the opening hands. Empty when the log carried none of it,
    /// which is what keeps a match with no opening from growing an empty heading.
    /// </summary>
    /// <remarks>
    /// A game after the first opens differently and has to say so. There is no die roll
    /// — the loser of the previous game chooses who begins — so the sentence names the
    /// chooser and what they chose, in the same shape the roll winner's does. Nothing
    /// says what they lost, because the line that ends the previous game, two lines
    /// above, has just said it.
    /// </remarks>
    private static List<string> OpeningLines(Transcript t, Opening? opening)
    {
        var lines = new List<string>();
        if (opening is not { } o) return lines;

        if (o.WinnerSeat is { } winner)
        {
            // High first, whichever seat rolled it: the sentence is about a roll being
            // won, and "wins the die roll 8 to 20" reads as a loss.
            var high = o.Rolls.Max(r => r.Value);
            var low = o.Rolls.Min(r => r.Value);
            var roll =
                $"{Who(winner, t)} {Verb(winner, "win", "wins", t)} the die roll {high} to {low}";

            if (o.FirstPlayerSeat is not { } first)
                // The roll is known but the game never opened a turn, so who would have
                // gone first is genuinely unknown. Saying only what was seen.
                lines.Add(roll);
            else if (first == winner)
                lines.Add($"{roll} and {Verb(winner, "play", "plays", t)} first");
            else
            {
                // Two lines rather than one long sentence, so each starts with the
                // player it is about — the reader has to come away with the right
                // player on the play, and that is the clause that must not be skimmed.
                lines.Add($"{roll} and {Verb(winner, "choose", "chooses", t)} to draw");
                lines.Add(OnThePlay(first, t));
            }
        }
        else if (o.ChoosingSeat is { } chooser)
        {
            var chose = $"{Who(chooser, t)} {Verb(chooser, "choose", "chooses", t)}";

            if (o.FirstPlayerSeat is not { } first)
                // Somebody chose, but the game never opened a turn, so what they chose
                // is genuinely unknown. Saying only what was seen.
                lines.Add($"{chose} who begins");
            else if (first == chooser)
                lines.Add($"{chose} to play first");
            else
            {
                // Split for the same reason the die roll's is: the reader has to come
                // away with the right player on the play.
                lines.Add($"{chose} to draw");
                lines.Add(OnThePlay(first, t));
            }
        }
        else if (o.FirstPlayerSeat is { } only)
        {
            lines.Add(OnThePlay(only, t));
        }

        lines.AddRange(HandLines(o, t));
        return lines;
    }

    private static string OnThePlay(int seat, Transcript t) =>
        $"{Who(seat, t)} {Verb(seat, "play", "plays", t)} first";

    /// <summary>
    /// What each player did with their opening hand, you first as everywhere else.
    /// </summary>
    /// <remarks>
    /// A seat missing from the map is a seat whose pre-game state was never read, and it
    /// gets no line: "keeps seven" is a claim, and the evidence for it is having watched
    /// the mulligan phase go by without a count appearing.
    /// <para>
    /// When neither player mulliganed the two lines become one. Standing silent instead
    /// would leave a reader unable to tell "both kept" from "the log did not say", which
    /// is the whole thing this section exists to stop; but 132 of the 152 archived
    /// matches have no mulligan at all, and spending two lines to report that nothing
    /// happened, in seven transcripts out of eight, is how a section becomes furniture.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> HandLines(Opening o, Transcript t)
    {
        var seats = new List<int>();
        if (t.You?.Seat is { } you && o.Mulligans.ContainsKey(you)) seats.Add(you);
        if (t.Opponent?.Seat is { } them && o.Mulligans.ContainsKey(them)) seats.Add(them);

        if (seats.Count == 0) yield break;

        if (seats.Count == 2 && seats.TrueForAll(s => o.Mulligans[s] == 0))
        {
            yield return $"Both players keep {CardCounts[Opening.StartingHandSize]}";
            yield break;
        }

        foreach (var seat in seats)
            yield return o.Mulligans[seat] == 0
                ? $"{Who(seat, t)} {Verb(seat, "keep", "keeps", t)} {CardCounts[o.Kept(seat)]}"
                : $"{Who(seat, t)} {Verb(seat, "mulligan", "mulligans", t)} " +
                  $"to {CardCounts[o.Kept(seat)]}";
    }

    /// <summary>Life totals entering the turn, always ordered you-first.</summary>
    private static string LifeScore(GameEvent e, Transcript t)
    {
        if (e.LifeSeat1 == 0 && e.LifeSeat2 == 0) return "";
        var yours = t.You?.Seat == 2 ? e.LifeSeat2 : e.LifeSeat1;
        var theirs = t.You?.Seat == 2 ? e.LifeSeat1 : e.LifeSeat2;
        return $"  (You {yours} · Opponent {theirs})";
    }

    private static string Who(int? seat, Transcript t) =>
        seat is null ? "Someone" : seat == t.You?.Seat ? "You" : "Opponent";

    private static string Verb(int? seat, string youForm, string theyForm, Transcript t) =>
        seat == t.You?.Seat ? youForm : theyForm;

    /// <summary>
    /// How long the turn ran, on the turns long enough for that to be the point. The
    /// sentence is about the turn and never about a player: the span covers whoever
    /// was deciding, whoever was responding, and the animations in between, and
    /// <see cref="TurnClock"/> cannot tell those apart. "Opponent took 1m 48s" would
    /// be an accusation the log does not support; "1 minute 48 seconds elapsed" is
    /// what was actually measured.
    /// </summary>
    private static string Elapsed(GameEvent e, IReadOnlyDictionary<int, TimeSpan> longTurns) =>
        longTurns.TryGetValue(e.Seq, out var d) ? $" · {TurnClock.Spoken(d)} elapsed" : "";

    /// <summary>
    /// A zone transfer whose category carries no verb of its own, said as where the card
    /// ended up.
    /// </summary>
    /// <remarks>
    /// These used to read "Forest moves (Put)" — the only place in the transcript where
    /// Arena's own vocabulary reached the reader, across 153 lines of the archive. The
    /// category is bookkeeping: "Put" covers a fetchland finding a Forest, a mulligan
    /// bottoming a card and a tutor putting one in hand, which have nothing in common
    /// except that the engine had no better word. The destination is the part worth
    /// reading, and it is in the same annotation.
    /// <para>
    /// A category that names a mechanic rather than the bare fact of moving is kept
    /// alongside it, so "Warp" is not flattened into a plain exile and a mechanic added
    /// in some future set surfaces rather than disappearing. A move that begins and ends
    /// in the same zone is a shuffle or a reorder and says nothing worth a line.
    /// </para>
    /// </remarks>
    private static string? ZoneMove(GameEvent e)
    {
        if (e.ToZone is null) return $"{e.SourceName} moves ({e.Detail})";
        if (e.ToZone == e.FromZone) return null;

        var where = e.ToZone switch
        {
            "ZoneType_Battlefield" => "is put onto the battlefield",
            "ZoneType_Hand" => "is put into hand",
            "ZoneType_Library" => "is put into the library",
            "ZoneType_Graveyard" => "is put into the graveyard",
            "ZoneType_Exile" => "is exiled",
            "ZoneType_Stack" => "goes on the stack",
            _ => null
        };
        if (where is null) return $"{e.SourceName} moves ({e.Detail})";

        // "Put" is the engine saying only that something moved, and "nil" is it saying
        // nothing at all. Neither adds to a sentence that already names the destination.
        var how = e.Detail is null or "" or "Put" or "nil" ? "" : $" ({e.Detail})";
        return $"{e.SourceName} {where}{how}";
    }

    private static string? Phrase(GameEvent e, Transcript t) => e.Kind switch
    {
        EventKind.TurnStart =>
            $"Turn {e.Turn} — {Who(e.ActorSeat ?? e.ActiveSeat, t)}{LifeScore(e, t)}",

        EventKind.BoardSnapshot when !string.IsNullOrWhiteSpace(e.Detail) =>
            $"{Who(e.ActorSeat, t)} control{(e.ActorSeat == t.You?.Seat ? "" : "s")}: {e.Detail}",

        EventKind.LandPlayed when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "play", "plays", t)} {e.SourceName}",

        EventKind.SpellCast when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "cast", "casts", t)} {e.SourceName}"
            + (e.TargetName is not null ? $", targeting {e.TargetName}" : ""),

        EventKind.Resolved when e.SourceName is not null => $"{e.SourceName} resolves",

        EventKind.Countered when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} counters {e.SourceName}"
                : $"{e.SourceName} is countered",

        EventKind.Drew when e.SourceName is not null && e.ActorSeat == t.You?.Seat =>
            $"You draw {e.SourceName}",
        EventKind.Drew => $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "draw", "draws", t)} a card",

        EventKind.Discarded when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "discard", "discards", t)} {e.SourceName}",

        // Naming what caused it is the difference between a list of things that
        // happened and a transcript you can follow.
        EventKind.Destroyed when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} destroys {e.SourceName}"
                : $"{e.SourceName} is destroyed",

        EventKind.Exiled when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} exiles {e.SourceName}"
                : $"{e.SourceName} is exiled",

        EventKind.Returned when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} returns {e.SourceName} to hand"
                : $"{e.SourceName} returns to hand",

        EventKind.Sacrificed when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "sacrifice", "sacrifices", t)} {e.SourceName}",

        EventKind.Milled when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} mills {e.SourceName}"
                : $"{e.SourceName} is milled",

        EventKind.Surveilled when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "surveil", "surveils", t)} {e.SourceName}",
        EventKind.StateBasedAction when e.SourceName is not null =>
            $"{e.SourceName} is put into the graveyard",

        EventKind.Damage when e.TargetSeat is not null =>
            $"{e.SourceName ?? "Something"} deals {e.Amount} damage to {Who(e.TargetSeat, t)}",
        EventKind.Damage when e.TargetName is not null =>
            $"{e.SourceName ?? "Something"} deals {e.Amount} damage to {e.TargetName}",

        EventKind.LifeChanged when e.Amount != 0 =>
            $"{Who(e.TargetSeat, t)} " +
            $"{Verb(e.TargetSeat, e.Amount > 0 ? "gain" : "lose", e.Amount > 0 ? "gains" : "loses", t)} " +
            $"{Math.Abs(e.Amount)} life",

        EventKind.TokenCreated when e.TargetName is not null =>
            $"{e.SourceName ?? "An effect"} creates {e.TargetName}",

        EventKind.CounterChanged when e.TargetName is not null && e.Amount != 0 =>
            $"{e.TargetName} {(e.Amount > 0 ? "gets" : "loses")} {Math.Abs(e.Amount)} " +
            $"{(e.Detail is null ? "" : e.Detail + " ")}counter" +
            $"{(Math.Abs(e.Amount) == 1 ? "" : "s")}",

        // Cause first and active, the same shape as "Split Up destroys Hare Apparent".
        // Without it a trigger line names the ability and stops, so a reader watching
        // three of them fire in a row has nothing to tell them apart or say what the
        // player did to set them off.
        EventKind.Triggered when e.SourceName is not null && e.CauseName is not null =>
            $"{e.CauseName} triggers {e.SourceName}",
        EventKind.Triggered when e.SourceName is not null => $"{e.SourceName} triggers",

        // Passive on purpose. The same annotation covers an aura arriving on a creature
        // and a player equipping a sword, and "Opponent equips" would be a claim about
        // who acted that the log does not make for the first of those.
        EventKind.Attached when e.SourceName is not null && e.TargetName is not null =>
            $"{e.SourceName} is attached to {e.TargetName}",

        // Arena's own wording, from the card: "When this Class becomes level 2, …".
        EventKind.LevelUp when e.SourceName is not null && e.Amount > 0 =>
            $"{e.SourceName} becomes level {e.Amount}",

        EventKind.Attack when e.SourceName is not null && e.TargetName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "attack", "attacks", t)} " +
            $"{e.TargetName} with {e.SourceName}",
        EventKind.Attack when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "attack", "attacks", t)} with {e.SourceName}",

        EventKind.Block when e.SourceName is not null && e.TargetName is not null =>
            $"{e.SourceName} blocks {e.TargetName}",
        EventKind.Block when e.SourceName is not null => $"{e.SourceName} blocks",

        EventKind.Scry when e.Detail is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "scry", "scries", t)} {e.Amount}, " +
            $"putting {e.Detail}",
        EventKind.Scry => $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "scry", "scries", t)}",
        EventKind.Revealed when e.SourceName is not null => $"{e.SourceName} is revealed",

        EventKind.ManaPaid when e.SourceName is not null => $"taps {e.SourceName} for mana",
        EventKind.PhaseChange when !string.IsNullOrWhiteSpace(e.Detail) => $"— {e.Detail} —",
        EventKind.PhaseChange => null,
        EventKind.Unknown => $"[unhandled: {e.RawType}]",

        EventKind.GameEnd => e.Detail,
        EventKind.ZoneMove when e.SourceName is not null => ZoneMove(e),

        _ => null
    };
}
