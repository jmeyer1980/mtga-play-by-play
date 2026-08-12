using MtgaPbp.Core;

namespace MtgaPbp.Render;

public enum Density { Beats, Verbose }

public sealed record Line(
    int Turn, int Indent, string Text, bool IsTurnHeader, bool IsBoard = false);

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
    /// Hand sizes as words. The rest of a line is prose, and "mulligan to 6" reads like
    /// a stat where "mulligan to six" reads like the sentence a player would say. It is
    /// also one less number for a synthesiser to run together with the die roll's.
    /// Indexed by cards kept, so it covers nought through a full opening hand.
    /// </summary>
    private static readonly string[] CardCounts =
        ["zero", "one", "two", "three", "four", "five", "six", "seven"];

    public static IReadOnlyList<Line> Narrate(Transcript t, Density density)
    {
        var lines = new List<Line>();

        // Both densities get the opening. Nothing about it is detail you would want
        // hidden, and the two views are meant to be the same match at two zoom levels.
        if (OpeningLines(t) is { Count: > 0 } opening)
        {
            lines.Add(new Line(0, 0, OpeningHeading, IsTurnHeader: true));
            foreach (var text in opening)
                lines.Add(new Line(0, 1, text, IsTurnHeader: false));
        }

        // Only the turns worth remarking on, so a header carries a duration where that
        // is the interesting thing about the turn and stays quiet everywhere else.
        var longTurns = TurnClock.LongTurns(t);

        foreach (var e in t.Events.OrderBy(x => x.Seq))
        {
            if (density == Density.Beats && VerboseOnly.Contains(e.Kind)) continue;
            if (density == Density.Beats && IsUnnamed(e)) continue;
            var text = Phrase(e, t);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Appended here rather than inside Phrase, which is a switch over how each
            // kind of event reads and has no business knowing the clock.
            if (e.Kind == EventKind.TurnStart) text += Elapsed(e, longTurns);

            lines.Add(new Line(
                e.Turn,
                e.Kind == EventKind.TurnStart ? 0 : 1,
                text,
                e.Kind == EventKind.TurnStart,
                e.Kind == EventKind.BoardSnapshot));
        }
        return Collapse(lines);
    }

    /// <summary>
    /// Folds runs of the identical line into one with a count. A single card can
    /// trigger nine times in a row or make four tokens back to back, and printing
    /// each is how a transcript turns into a wall. Turn headers are never folded.
    /// </summary>
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
                   lines[i + run].IsTurnHeader == line.IsTurnHeader)
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
    private static List<string> OpeningLines(Transcript t)
    {
        var lines = new List<string>();
        if (t.Opening is not { } o) return lines;

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
        EventKind.ZoneMove when e.SourceName is not null =>
            $"{e.SourceName} moves ({e.Detail})",

        _ => null
    };
}
