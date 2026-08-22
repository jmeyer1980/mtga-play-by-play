namespace MtgaPbp.Render;

/// <summary>
/// One thing that happened, for the rolling tail at the foot of the scoreboard.
/// </summary>
public sealed record Beat(string At, string Deck, string Result);

/// <summary>
/// The block `watch` pins to the bottom of the terminal and repaints in place.
/// </summary>
/// <remarks>
/// `watch` used to print one line per finished match and nothing else. Over an evening
/// that is 41 lines saying "report updated" and nothing saying how the evening went —
/// and the one line that did matter, a rotation nudge, sat 38 scrolls up by the time it
/// was wanted. The noise was not merely useless; it buried the signal.
/// <para>
/// So the standing state is drawn once and repainted, and only genuinely notable things
/// — nudges and verdicts — are printed above it where they scroll and stay. That is the
/// inverse of what it did before: the repetitive part no longer accumulates, and the
/// rare part is no longer lost.
/// </para>
/// <para>
/// Pure text in, pure text out. The cursor arithmetic lives in the caller, so everything
/// about what the block SAYS can be tested without a terminal.
/// </para>
/// </remarks>
public static class Scoreboard
{
    /// <summary>How many finished matches the rolling tail shows.</summary>
    public const int Recent = 3;

    /// <summary>
    /// The block, as lines, already clipped to <paramref name="width"/>.
    /// </summary>
    /// <param name="session">The sitting in progress, or null before the first match.</param>
    /// <param name="beats">Finished matches, newest first. Only the first few are shown.</param>
    /// <param name="playing">The deck the newest match was played with, marked in the list.</param>
    /// <param name="url">Where the live report is served.</param>
    /// <param name="updated">When the last match landed.</param>
    /// <param name="width">The terminal's width. Narrow terminals clip rather than wrap.</param>
    /// <param name="height">
    /// The terminal's height. The block never claims more than half of it: a scoreboard
    /// taller than its window fights the scrollback and both lose, and the thing that
    /// gets pushed off is whatever notable line was printed above it.
    /// </param>
    public static IReadOnlyList<string> Lines(
        SessionRow? session,
        IReadOnlyList<Beat> beats,
        string? playing,
        string url,
        DateTime updated,
        int width = 80,
        int height = 24)
    {
        var lines = new List<string> { new('-', Math.Clamp(width - 1, 10, 100)) };

        if (session is null)
        {
            lines.Add("  no matches yet this session");
        }
        else
        {
            var record = session.Drawn == 0
                ? $"{session.Won}-{session.Lost}"
                : $"{session.Won}-{session.Lost}-{session.Drawn}";
            lines.Add($"  TONIGHT   {session.Games} game{(session.Games == 1 ? "" : "s")} " +
                      $"· {record} · since {Time(session.Started)}");
            lines.Add("");

            // Sized to the names actually present so the records sit just past the
            // longest one, rather than against a fixed column that leaves a gulf when
            // every deck is called something short. Capped, because one absurd label
            // must not push the numbers off the edge — it clips instead.
            var decks = Fit(session.Decks, height);
            var room = decks.Count == 0
                ? 0
                : Math.Clamp(decks.Max(d => d.Name.Length), 8, Math.Max(8, Math.Min(34, width - 24)));
            foreach (var d in decks)
            {
                var name = Clip(d.Name, room).PadRight(room);
                var here = string.Equals(d.Name, playing, StringComparison.Ordinal) ? "  <- playing" : "";
                lines.Add($"    {name}  {d.Won}-{d.Lost}{here}");
            }
            if (decks.Count < session.Decks.Count)
                lines.Add($"    +{session.Decks.Count - decks.Count} more");
            if (session.Decks.Count > 0) lines.Add("");
        }

        foreach (var b in beats.Take(Recent))
            lines.Add($"  {b.At}  {Clip(b.Deck, 24).PadRight(24)}  {b.Result}");

        lines.Add("");
        lines.Add($"  updated {updated:HH:mm:ss} · live at {url} · Ctrl+C to stop");

        return lines.Select(l => Clip(l, Math.Max(10, width - 1))).ToList();
    }

    /// <summary>
    /// As many decks as fit in half the window, most-played first.
    /// </summary>
    private static IReadOnlyList<SessionDeck> Fit(IReadOnlyList<SessionDeck> decks, int height)
    {
        // Seven lines of the block are not decks: the rule, the header, two blanks, the
        // beats and the footer. What is left of half the window is what a deck list gets.
        var room = Math.Max(1, height / 2 - Recent - 5);
        return decks.Count <= room ? decks : decks.Take(Math.Max(1, room - 1)).ToList();
    }

    /// <summary>The time out of a "yyyy-MM-dd HH:mm" stamp, since the date is today's.</summary>
    private static string Time(string started) =>
        started.Length >= 16 ? started[11..16] : started;

    private static string Clip(string s, int width) =>
        s.Length <= width ? s : width <= 1 ? s[..Math.Max(0, width)] : s[..(width - 1)] + "…";
}
