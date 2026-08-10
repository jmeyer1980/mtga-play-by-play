using MtgaPbp.Core;

namespace MtgaPbp.Render;

public enum Density { Beats, Verbose }

public sealed record Line(int Turn, int Indent, string Text, bool IsTurnHeader);

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
            var text = Phrase(e, t);
            if (string.IsNullOrWhiteSpace(text)) continue;
            lines.Add(new Line(e.Turn, e.Kind == EventKind.TurnStart ? 0 : 1, text,
                               e.Kind == EventKind.TurnStart));
        }
        return lines;
    }

    private static string Who(int? seat, Transcript t) =>
        seat is null ? "Someone" : seat == t.You?.Seat ? "You" : "Opponent";

    private static string Verb(int? seat, string youForm, string theyForm, Transcript t) =>
        seat == t.You?.Seat ? youForm : theyForm;

    private static string? Phrase(GameEvent e, Transcript t) => e.Kind switch
    {
        EventKind.TurnStart =>
            $"Turn {e.Turn} — {Who(e.ActorSeat ?? e.ActiveSeat, t)}",

        EventKind.LandPlayed when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "play", "plays", t)} {e.SourceName}",

        EventKind.SpellCast when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "cast", "casts", t)} {e.SourceName}",

        EventKind.Resolved when e.SourceName is not null => $"{e.SourceName} resolves",
        EventKind.Countered when e.SourceName is not null => $"{e.SourceName} is countered",

        EventKind.Drew when e.SourceName is not null && e.ActorSeat == t.You?.Seat =>
            $"You draw {e.SourceName}",
        EventKind.Drew => $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "draw", "draws", t)} a card",

        EventKind.Discarded when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "discard", "discards", t)} {e.SourceName}",

        EventKind.Destroyed when e.SourceName is not null => $"{e.SourceName} is destroyed",
        EventKind.Sacrificed when e.SourceName is not null => $"{e.SourceName} is sacrificed",
        EventKind.Exiled when e.SourceName is not null => $"{e.SourceName} is exiled",
        EventKind.Returned when e.SourceName is not null => $"{e.SourceName} returns to hand",
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
            $"{e.TargetName} {(e.Amount > 0 ? "gets" : "loses")} {Math.Abs(e.Amount)} counter" +
            $"{(Math.Abs(e.Amount) == 1 ? "" : "s")}",

        EventKind.Attack when e.SourceName is not null && e.TargetName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "attack", "attacks", t)} " +
            $"{e.TargetName} with {e.SourceName}",
        EventKind.Attack when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "attack", "attacks", t)} with {e.SourceName}",

        EventKind.Block when e.SourceName is not null && e.TargetName is not null =>
            $"{e.SourceName} blocks {e.TargetName}",
        EventKind.Block when e.SourceName is not null => $"{e.SourceName} blocks",

        EventKind.Scry => $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "scry", "scries", t)}",
        EventKind.Revealed when e.SourceName is not null => $"{e.SourceName} is revealed",

        EventKind.ManaPaid when e.SourceName is not null => $"taps {e.SourceName} for mana",
        EventKind.PhaseChange => $"— phase {e.Phase}, step {e.Step} —",
        EventKind.Unknown => $"[unhandled: {e.RawType}]",

        EventKind.GameEnd => e.Detail,
        EventKind.ZoneMove when e.SourceName is not null =>
            $"{e.SourceName} moves ({e.Detail})",

        _ => null
    };
}
