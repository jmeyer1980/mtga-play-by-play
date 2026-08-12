using System.Text;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public static class MarkdownRenderer
{
    public static string Render(Transcript t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {TranscriptSummary.Title(t)}");
        sb.AppendLine();
        sb.AppendLine($"*{TranscriptSummary.Subtitle(t)}*");
        sb.AppendLine();
        if (t.Incomplete)
            sb.AppendLine("> This match is incomplete — the log was rotated before it finished.")
              .AppendLine();

        if (TranscriptSummary.GapWarning(t) is { } gap)
            sb.AppendLine($"> {gap}").AppendLine();

        // Before the turns, not after: it is the thing you check while reading the
        // transcript, and it is what the copy button puts here too, so a pasted
        // transcript and the exported file stay the same document.
        if (t.Deck.Count > 0)
        {
            sb.AppendLine($"## {TranscriptSummary.DeckHeading(t)}").AppendLine();
            foreach (var card in t.Deck)
                sb.AppendLine($"- {TranscriptSummary.DeckLine(card)}");
            sb.AppendLine().AppendLine($"*{TranscriptSummary.DeckNote}*");
        }

        foreach (var line in Narrator.Narrate(t, Density.Beats))
        {
            if (line.IsTurnHeader) sb.AppendLine().AppendLine($"## {line.Text}");
            else if (line.IsBoard) sb.AppendLine($"  *{line.Text}*");
            else sb.AppendLine($"- {line.Text}");
        }
        return sb.ToString();
    }
}

public static class TranscriptSummary
{
    public static string Title(Transcript t) =>
        $"{t.You?.ScreenName ?? "You"} vs {t.Opponent?.ScreenName ?? "Opponent"}";

    /// <summary>
    /// The decklist's heading. It counts cards rather than naming the deck, because
    /// the log carries a list of ids and nothing else — any name would be a guess.
    /// </summary>
    public static string DeckHeading(Transcript t) =>
        $"Your deck ({t.Deck.Sum(d => d.Count)} cards)";

    /// <summary>
    /// One decklist line. Shared with the game page so a copied transcript and the
    /// exported markdown say the same words; the page adds the spoken forms of the
    /// glyphs on top and takes them back off again when copying.
    /// </summary>
    public static string DeckLine(DeckEntry d) =>
        $"{d.Count}× {d.Name}{(d.Seen ? "" : " · not seen")}";

    /// <summary>
    /// What the mark means. "Seen" is whether the client ever held a game object for
    /// the card, so its absence really does mean the card sat in the library from the
    /// first shuffle to the last turn — worth saying, because a reader could otherwise
    /// read it as "not played".
    /// </summary>
    public const string DeckNote = "A card marked \"not seen\" stayed in your library all match.";

    /// <summary>
    /// What to tell a reader when the log did not account for part of the match, or
    /// null when it accounted for all of it.
    /// </summary>
    /// <remarks>
    /// Kept separate from the incomplete-match warning rather than folded into it.
    /// "The log was cut off before the end" and "the log ran to the end but skipped
    /// things in the middle" are different failures with different consequences: the
    /// first tells you the ending is missing, the second tells you the ending may be
    /// there but the reason for it may not. A reader told the wrong one goes looking in
    /// the wrong place, which is worse than a warning they can act on.
    ///
    /// It says what is missing and not how much detail was lost, because "77 game
    /// objects" is Arena's vocabulary, not a player's, and the number a player can act
    /// on is simply "some of this is not here". The counts are kept on the events and
    /// reported by <c>mtga-pbp stats</c>, where a diagnostic audience wants them.
    /// </remarks>
    public static string? GapWarning(Transcript t)
    {
        if (t.Gaps.Count == 0) return null;

        var summarized = t.Gaps.Count(g => g.Kind == LogGapKind.Summarized);
        var torn = t.Gaps.Count - summarized;

        var causes = new List<string>();
        if (summarized > 0)
            causes.Add($"Arena left {Count(summarized, "game-state update")} out of the log");
        if (torn > 0)
            causes.Add($"{Count(torn, "log line")} ended mid-message");

        // No pronoun for the missing messages: "what happened in it" and "in them" both
        // need the count to agree, and one gap is as common as several.
        return $"Part of this match is missing — {string.Join(", and ", causes)}, " +
               "so this transcript is not a complete account of the match.";
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    public static string Result(Transcript t)
    {
        if (t.Incomplete && t.WinningTeamId is null) return "Unfinished";
        var won = t.WinningTeamId is not null && t.WinningTeamId == t.You?.Seat;
        return $"{(won ? "Won" : "Lost")} {t.GamesWon}-{t.GamesLost}";
    }

    public static string Subtitle(Transcript t) =>
        $"{t.EventName} · {Date(t):yyyy-MM-dd HH:mm} · {Result(t)} · {Turns(t)} turns";

    /// <summary>
    /// Zone match times are rendered in. Local by default, because you want to see
    /// when you actually played. Settable so golden-file output does not depend on
    /// the timezone of the machine rendering it.
    /// </summary>
    public static TimeZoneInfo DisplayTimeZone { get; set; } = TimeZoneInfo.Local;

    public static DateTimeOffset Date(Transcript t) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds(t.StartedAtMs), DisplayTimeZone);

    public static int Turns(Transcript t) => t.Events.Count == 0 ? 0 : t.Events.Max(e => e.Turn);
}
