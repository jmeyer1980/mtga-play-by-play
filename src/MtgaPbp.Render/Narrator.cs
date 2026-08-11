using MtgaPbp.Core;

namespace MtgaPbp.Render;

public enum Density { Beats, Verbose }

public sealed record Line(
    int Turn, int Indent, string Text, bool IsTurnHeader, bool IsBoard = false);

public static class Narrator
{
    private static readonly HashSet<EventKind> VerboseOnly =
        [EventKind.PhaseChange, EventKind.ManaPaid, EventKind.Unknown];

    public static IReadOnlyList<Line> Narrate(Transcript t, Density density)
    {
        var lines = new List<Line>();
        foreach (var e in t.Events.OrderBy(x => x.Seq))
        {
            if (density == Density.Beats && VerboseOnly.Contains(e.Kind)) continue;
            if (density == Density.Beats && IsUnnamed(e)) continue;
            var text = Phrase(e, t);
            if (string.IsNullOrWhiteSpace(text)) continue;
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
