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

        if (TranscriptSummary.TimingNote(t) is { } timing)
            sb.AppendLine($"*{timing}*").AppendLine();

        // Before the turns, not after: it is the thing you check while reading the
        // transcript, and it is what the copy button puts here too, so a pasted
        // transcript and the exported file stay the same document.
        if (t.Deck.Count > 0)
        {
            sb.AppendLine($"## {TranscriptSummary.DeckHeading(t)}").AppendLine();
            if (TranscriptSummary.CommanderLine(t) is { } commander)
                sb.AppendLine(commander).AppendLine();
            foreach (var card in t.Deck)
                sb.AppendLine($"- {TranscriptSummary.DeckLine(card)}");
            sb.AppendLine().AppendLine($"*{TranscriptSummary.DeckNote}*");
        }

        foreach (var line in Narrator.Narrate(t, Density.Beats))
        {
            if (line.IsTurnHeader)
                sb.AppendLine().AppendLine($"{new string('#', line.Level)} {line.Text}");
            else if (line.IsBoard) sb.AppendLine($"  *{line.Text}*");
            else sb.AppendLine($"- {line.Text}");
        }

        // Below a rule, so a transcript pasted into a chat carries which build wrote it
        // without the stamp reading as part of the match.
        sb.AppendLine().AppendLine("---").AppendLine().AppendLine($"*{BuildInfo.Line}*");
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
    /// <remarks>
    /// The count stays the library's count even in Brawl, where the whole deck is one
    /// card bigger: the commander is announced in words instead, because "(99 cards)"
    /// alone describes an illegal deck and "(100 cards)" would count a card that is
    /// not in the pile the number is read against. The heading has to carry the fact
    /// itself because it is also the collapsed disclosure's only visible line — the
    /// commander's name, which lives inside the body, would otherwise vanish with it.
    /// </remarks>
    public static string DeckHeading(Transcript t)
    {
        var cards = $"{t.Deck.Sum(d => d.Count)} cards";
        return t.Commanders.Count switch
        {
            0 => $"Your deck ({cards})",
            1 => $"Your deck ({cards} and a commander)",
            var n => $"Your deck ({cards} and {n} commanders)"
        };
    }

    /// <summary>
    /// The commanders by name, or null when the match recorded none. A sentence
    /// rather than a decklist row: a decklist row implies a card that could be
    /// drawn, and the commander sits in the command zone from the first shuffle.
    /// </summary>
    public static string? CommanderLine(Transcript t) => t.Commanders.Count switch
    {
        0 => null,
        1 => $"Commander: {t.Commanders[0]}",
        _ => $"Commanders: {string.Join(" and ", t.Commanders)}"
    };

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
    /// <para>
    /// Each cause names its mechanism, and the note closes by saying the loss is
    /// permanent (#15). A summarized update is Arena hitting its own size limit and
    /// writing a one-line summary instead — a decision, not damage — while a torn line
    /// is damage; a reader told only "missing" cannot tell which happened, and their
    /// natural next move is a re-run, which cannot help. The closing sentence is
    /// worded to be true of both kinds: the summarized body was never written, and
    /// the torn one was destroyed as it was.
    /// </para>
    /// </remarks>
    public static string? GapWarning(Transcript t)
    {
        if (t.Gaps.Count == 0) return null;

        var summarized = t.Gaps.Count(g => g.Kind == LogGapKind.Summarized);
        var torn = t.Gaps.Count - summarized;

        var causes = new List<string>();
        if (summarized > 0)
            causes.Add("Arena wrote a one-line summary in place of " +
                       $"{Count(summarized, "game-state update")} that grew too large");
        if (torn > 0)
            causes.Add($"{Count(torn, "log line")} ended mid-message");

        // No pronoun for the missing messages: "what happened in it" and "in them" both
        // need the count to agree, and one gap is as common as several.
        return $"Part of this match is missing — {string.Join(", and ", causes)}, " +
               "so this transcript is not a complete account of the match. " +
               "What is missing was lost as the log was written, and no re-scan can recover it.";
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    public static string Result(Transcript t)
    {
        if (t.Incomplete && t.WinningTeamId is null) return "Unfinished";
        // Before the won/lost coin flip: a draw has no WinningTeamId, and "no
        // winning team" otherwise reads as "you did not win" — which is how the
        // archive's first drawn match came to say "Lost 0-0".
        if (t.Drawn) return $"Drew {t.GamesWon}-{t.GamesLost}";
        var won = t.WinningTeamId is not null && t.WinningTeamId == t.You?.Seat;
        return $"{(won ? "Won" : "Lost")} {t.GamesWon}-{t.GamesLost}";
    }

    /// <summary>
    /// How long the match ran, folded into the turn count rather than added as another
    /// bare field. "13 turns in 4 minutes" says which number is which; "13 turns · 4
    /// minutes" leaves a reader to guess what the second one counts. An incomplete
    /// match keeps the turn count alone, because <see cref="TurnClock.MatchLength"/>
    /// declines to measure one and a subtitle is no place to explain why.
    /// </summary>
    /// <remarks>
    /// A multi-game match says how many games, because without it the turn count reads
    /// as one long game — and "30 turns" against a page whose highest turn heading says
    /// 17 looks like an error until you know there were two.
    /// </remarks>
    public static string Subtitle(Transcript t)
    {
        var turns = $"{Turns(t)} turns";
        if (t.Games.Count > 1) turns += $" across {t.Games.Count} games";
        if (TurnClock.MatchLength(t) is { } length)
            turns += $" in {TurnClock.Spoken(length)}";
        return $"{t.EventName} · {Date(t):yyyy-MM-dd HH:mm} · {Result(t)} · {turns}";
    }

    /// <summary>
    /// What a turn's elapsed time means, or null when no turn ran long enough to carry
    /// one and the note would be explaining something the reader cannot see.
    /// </summary>
    /// <remarks>
    /// It sits with the warnings rather than at the foot of the page because it is the
    /// same kind of sentence: read this number with a caveat. The caveat is the whole
    /// reason it exists — a duration on the opponent's turn invites being read as the
    /// opponent's thinking time, and it is not that. It cannot be, because the span
    /// also holds your own blocking decisions and every animation Arena played.
    /// <para>
    /// It also says which turns carry a time at all (#15). A mark that appears on one
    /// turn and not the next reads as a clock that works sometimes — two archived
    /// matches had turn one as their only slow turn, which reads exactly like "only
    /// the first turn is timed" — so the threshold and the never-timed final turn are
    /// stated rather than left for the reader to reverse-engineer. The threshold is
    /// interpolated from <see cref="TurnClock.LongTurnSeconds"/> so the sentence
    /// cannot drift from the rule it describes.
    /// </para>
    /// </remarks>
    public static string? TimingNote(Transcript t) =>
        TurnClock.LongTurns(t).Count > 0 ? TimingNoteText : null;

    public static readonly string TimingNoteText =
        "A turn's elapsed time is wall clock, from that turn starting to the next one " +
        "starting. It covers both players' decisions along with animation and network " +
        "time, so it is not any one player's thinking time. Only a turn that ran past " +
        $"{TurnClock.LongTurnSeconds} seconds is marked — quicker turns pass without " +
        "note — and the last turn of a game is never timed, because its end cannot be " +
        "told apart from the result screen after it.";

    /// <summary>
    /// Zone match times are rendered in. Local by default, because you want to see
    /// when you actually played. Settable so golden-file output does not depend on
    /// the timezone of the machine rendering it.
    /// </summary>
    public static TimeZoneInfo DisplayTimeZone { get; set; } = TimeZoneInfo.Local;

    public static DateTimeOffset Date(Transcript t) => Date(t.StartedAtMs);

    /// <summary>
    /// The same conversion from a bare timestamp, for callers that have the archive's
    /// ledger but not a transcript — the neighbour links, which know when the adjacent
    /// matches were played long before those matches are extracted.
    /// </summary>
    public static DateTimeOffset Date(long startedAtMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds(startedAtMs), DisplayTimeZone);

    /// <summary>
    /// How many turns were played, across every game of the match.
    /// </summary>
    /// <remarks>
    /// The sum and not the maximum. Turn numbers restart at one in each game of a Bo3,
    /// so the highest one on the page is the length of the longest game — it was
    /// reporting 17 for a match that ran 13 turns and then 17 more. The fallback covers
    /// a transcript built by hand rather than extracted, which carries no game records.
    /// </remarks>
    public static int Turns(Transcript t) =>
        t.Games.Count > 0 ? t.Games.Sum(g => g.Turns)
        : t.Events.Count == 0 ? 0 : t.Events.Max(e => e.Turn);
}
