namespace MtgaPbp.Core;

/// <summary>
/// How long turns took, measured from the log's own envelope timestamps.
/// </summary>
/// <remarks>
/// This is the one number in the transcript that the log does not state outright, so
/// it is worth being exact about what it is. A turn's duration here is the wall-clock
/// gap between that turn starting and the next one starting — nothing more derived
/// than a subtraction of two epoch-millisecond stamps Arena wrote itself.
/// <para>
/// It is deliberately <em>not</em> built from <c>GREMessageType_TimerStateMessage</c>,
/// which is the obvious source and the wrong one. Those messages do carry a per-seat
/// <c>elapsedMs</c> on the active player's timer, it does reset at each turn and it
/// does climb monotonically within one — across the 152 archived matches it went
/// backwards once. But Arena only emits the message when it feels like it: a third of
/// turns (678 of 1951) carry no reading at all, and where a reading exists it is a
/// sample taken mid-turn rather than a total, accounting for a median 48% of the
/// turn's wall clock. Fifteen turns that ran over thirty seconds had no reading
/// whatsoever, so the metric would have gone quiet on exactly the turns worth
/// surfacing. A number that is silent when it matters and half-sized when it speaks is
/// worse than no number, because a reader cannot tell which they are looking at.
/// </para>
/// <para>
/// The timer readings were still useful as a check. Where both exist they agree in
/// direction (Pearson r = 0.71) and the timer exceeded the wall clock in 1 turn out of
/// 1273 — that is, it behaves like a strict subset of the span measured here, which is
/// what it should be if both are measuring the same turn.
/// </para>
/// <para>
/// What this cannot do is say <em>who</em> was thinking. The span covers the active
/// player's decisions, the opponent's responses to them, and time nobody was thinking
/// at all — animation and network. Arena does report the non-active player's timer, so
/// the split is occasionally visible, but only on 114 of 1951 turns, which is far too
/// rare to build a claim on. Every word this feature renders therefore has to be about
/// the turn, never about a player.
/// </para>
/// </remarks>
public static class TurnClock
{
    /// <summary>
    /// How long a turn has to run before it is worth remarking on, in seconds.
    /// </summary>
    /// <remarks>
    /// Annotating every turn would be noise for the same reason the bare statline and
    /// <c>Narrator.Collapse</c> exist: most turns are unremarkable and saying so on
    /// each one buries the turns that are not. Across the archive's 1800 measurable
    /// turns the median is 16 seconds and the 90th percentile 43, so a threshold has
    /// to sit well above both to mean anything.
    /// <para>
    /// Sixty is not a round number picked for looking tidy. It is Arena's own base
    /// allowance for an active player — <c>TimerType_ActivePlayer</c> starts each turn
    /// at <c>durationSec: 61</c> in 2635 of the 2680 fresh samples in the archive — so
    /// a turn that outruns it is one where somebody used a whole unhurried decision
    /// window rather than simply playing on. Empirically that lands at the 95.7th
    /// percentile: 78 turns across 43 of 151 matches, at most 6 in any one match and
    /// usually 1. Dropping to 45 seconds would fire 165 times and put 10 marks on a
    /// single game, which is the wall this threshold exists to prevent.
    /// </para>
    /// </remarks>
    public const int LongTurnSeconds = 60;

    /// <summary>
    /// How long each turn took, keyed by the sequence number of its
    /// <see cref="EventKind.TurnStart"/> — unique per event, and unambiguous across a
    /// Bo3 where turn numbers restart.
    /// </summary>
    /// <remarks>
    /// The last turn of every game is absent by design, not by oversight. Its span
    /// would have to end at the last message of the game rather than at another turn,
    /// which sweeps up the result screen and, between games, the whole sideboarding
    /// period — the archive holds a final turn whose trailing silence runs 405 seconds.
    /// A turn needs a successor in the same game to be measurable, and one without a
    /// successor is left unmeasured rather than guessed at.
    /// </remarks>
    public static IReadOnlyDictionary<int, TimeSpan> Durations(Transcript t)
    {
        var starts = t.Events
            .Where(e => e.Kind == EventKind.TurnStart)
            .OrderBy(e => e.Seq)
            .ToList();

        var durations = new Dictionary<int, TimeSpan>();
        for (var i = 0; i + 1 < starts.Count; i++)
        {
            var (from, to) = (starts[i], starts[i + 1]);
            if (from.GameNumber != to.GameNumber) continue;

            // Timestamps should only ever climb, but a match stitched back together
            // across a log rotation is the kind of input that could hand us two that
            // do not. A negative turn length is not a fact about the game.
            var ms = to.TimestampMs - from.TimestampMs;
            if (ms <= 0) continue;

            durations[from.Seq] = TimeSpan.FromMilliseconds(ms);
        }
        return durations;
    }

    /// <summary>
    /// Only the turns that ran past <see cref="LongTurnSeconds"/>, keyed the same way.
    /// </summary>
    public static IReadOnlyDictionary<int, TimeSpan> LongTurns(Transcript t) =>
        Durations(t)
            .Where(p => p.Value.TotalSeconds >= LongTurnSeconds)
            .ToDictionary(p => p.Key, p => p.Value);

    /// <summary>
    /// How long the match ran, or null when the log does not know.
    /// </summary>
    /// <remarks>
    /// Last envelope minus first, which is the match's length only when the log holds
    /// the whole match. When it does not, that subtraction measures how much of the
    /// match was captured — a different quantity that would read as the same one — so
    /// an incomplete match reports nothing rather than something true of the wrong
    /// thing.
    /// </remarks>
    public static TimeSpan? MatchLength(Transcript t)
    {
        if (t.Incomplete) return null;
        var ms = t.EndedAtMs - t.StartedAtMs;
        return ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;
    }

    /// <summary>
    /// A duration abbreviated, for places where a column has to stay narrow. Seconds
    /// alone below a minute, minutes and seconds below an hour, and hours and minutes
    /// above it — at that length the seconds are noise, and a match that ran an hour is
    /// not one anyone is timing to the second.
    /// </summary>
    /// <remarks>
    /// Anything rendering this owes a screen reader <see cref="Spoken"/> alongside it,
    /// exactly as the decklist's "4×" does: a synthesiser reads "1m 12s" as a run of
    /// letters and digits, which next to a turn number is indistinguishable from more
    /// of the turn number.
    /// </remarks>
    public static string Format(TimeSpan d)
    {
        var (h, m, s) = Parts(d);
        if (h > 0) return $"{h}h {m}m";
        return m > 0 ? $"{m}m {s}s" : $"{s}s";
    }

    /// <summary>
    /// A duration as words, for text that is read aloud as often as it is looked at.
    /// </summary>
    /// <remarks>
    /// The narrated transcript uses this rather than <see cref="Format"/>, for the same
    /// reason hand sizes are spelled out: one line of a transcript is prose that a
    /// synthesiser has to get through, and "1m 12s" is not a phrase. It also keeps the
    /// markdown export, the clipboard and the page carrying one string between them,
    /// which is only possible while that string reads correctly on its own.
    /// </remarks>
    public static string Spoken(TimeSpan d)
    {
        var (h, m, s) = Parts(d);
        if (h > 0) return $"{Count(h, "hour")} {Count(m, "minute")}";
        return m > 0 ? $"{Count(m, "minute")} {Count(s, "second")}" : Count(s, "second");
    }

    private static (int Hours, int Minutes, int Seconds) Parts(TimeSpan d)
    {
        var total = (int)Math.Round(d.TotalSeconds);
        return (total / 3600, total % 3600 / 60, total % 60);
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
