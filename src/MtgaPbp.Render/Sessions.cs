using MtgaPbp.Core;

namespace MtgaPbp.Render;

/// <summary>
/// One deck's share of one sitting.
/// </summary>
/// <remarks>
/// Carried per session rather than looked up from the archive-wide record, because the
/// question a scoreboard answers is "how is this going tonight" — a deck at 57% lifetime
/// can be 0-4 this evening, and the lifetime number is no comfort at all in that moment.
/// </remarks>
/// <param name="Streak">
/// Losses in a row with this deck, counting back from its most recent game of the
/// sitting. Zero the moment it wins.
/// <para>
/// Carried so a board can show where a deck stands against the rotation rule rather than
/// only shouting when it crosses it. The rule fired twice in one evening, hours apart,
/// into a terminal window that a rebuild later destroyed — and the only way to know
/// where you stood was to have been watching at the instant it spoke. A number that is
/// simply there can be looked at.
/// </para>
/// </param>
public sealed record SessionDeck(string Name, int Won, int Lost, int Streak = 0)
{
    public int Played => Won + Lost;
}

/// <summary>
/// One sitting: the matches played in a single unbroken run, and how it went.
/// </summary>
/// <param name="StartedAtMs">When the first match of the session began.</param>
/// <param name="Started">The same, formatted the way the index formats a match date.</param>
/// <param name="Decks">
/// The decks played and how each did, most-played first, by the labels
/// <see cref="DeckIdentity"/> gives them. Empty for a session whose matches all predate
/// deck capture.
/// </param>
/// <param name="MatchIds">Every match in the session, oldest first.</param>
public sealed record SessionRow(
    long StartedAtMs,
    string Started,
    int Games,
    int Won,
    int Lost,
    int Drawn,
    IReadOnlyList<SessionDeck> Decks,
    IReadOnlyList<string> MatchIds)
{
    /// <summary>
    /// How many of the session's matches reached a result. Smaller than
    /// <see cref="Games"/> when the log was rotated mid-match: those still happened and
    /// still belong to the sitting, but they are not a win and not a loss.
    /// </summary>
    public int Decided => Won + Lost + Drawn;

    public double? WinRate => Decided == 0 ? null : (double)Won / Decided;

    /// <summary>
    /// The record in words, for a synthesiser and for anyone who wants the sentence
    /// rather than the shorthand. "7 wins and 8 losses" says what "7-8" only implies,
    /// and a bare "7 8" is what a screen reader would otherwise read out.
    /// </summary>
    public string Spoken =>
        Decided == 0
            ? $"{Games} game{(Games == 1 ? "" : "s")}, none finished"
            : string.Join(", ",
                new[]
                {
                    $"{Won} win{(Won == 1 ? "" : "s")}",
                    $"{Lost} loss{(Lost == 1 ? "" : "es")}",
                    Drawn > 0 ? $"{Drawn} draw{(Drawn == 1 ? "" : "s")}" : null
                }.Where(s => s is not null));
}

/// <summary>
/// Groups matches into the sittings they were played in.
/// </summary>
/// <remarks>
/// The report lists every match and never says how a night went, so the last row of an
/// evening is the whole impression it leaves — and if that row is a loss, a winning
/// session reads as a losing one. This is the arithmetic that fixes that, kept apart from
/// the renderer so it can be tested without parsing HTML, the same bargain
/// <see cref="IndexStats"/> strikes.
/// <para>
/// It is also the single definition of "one night" for the whole program. The coach that
/// watches a live session and the panel that summarises past ones both read it here,
/// because two implementations of the same boundary would eventually disagree and the
/// notification would contradict the page.
/// </para>
/// </remarks>
public static class Sessions
{
    /// <summary>
    /// How long a break has to be before the next match belongs to a new sitting.
    /// </summary>
    /// <remarks>
    /// Two hours, and the number was read off the archive rather than chosen — the same
    /// way <see cref="DeckIdentity.SameDeck"/> was, and for the same reason: a boundary
    /// picked by taste is a boundary nobody can argue with.
    /// <para>
    /// Of the 671 gaps between consecutive matches, 601 are under half an hour and are
    /// plainly one sitting. 31 more fall in the next half hour, which is somebody making
    /// a sandwich. Then the distribution empties out: <b>only 12 gaps in the whole
    /// archive sit between one and two hours</b>, and the count rises again past three.
    /// The threshold is placed in that hole, so it is insensitive — 90 minutes yields 33
    /// sessions, two hours 28, and 150 minutes 26, all describing the same nights.
    /// </para>
    /// <para>
    /// It also separates the two runs on 2026-08-18 — 07:00 to 08:42, then 17:03 onward
    /// — which a longer threshold merges into one 79-game session whose record describes
    /// neither half of the day.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Gap = TimeSpan.FromHours(2);

    /// <summary>
    /// The sessions in <paramref name="rows"/>, newest first — the order the index lists
    /// matches in, so the two read the same way round.
    /// </summary>
    /// <param name="rows">Matches, in any order.</param>
    /// <param name="deckOf">
    /// Which deck cluster each match belongs to, by slug — <see cref="IndexStats.DeckOf"/>.
    /// </param>
    /// <param name="labelOf">Slug to display label, for naming the decks a session used.</param>
    public static IReadOnlyList<SessionRow> From(
        IReadOnlyList<MatchSummary> rows,
        IReadOnlyDictionary<string, string>? deckOf = null,
        IReadOnlyDictionary<string, string>? labelOf = null)
    {
        // Oldest first, because a session boundary is a claim about the order things
        // were played in. Tie-broken by id for the same reason LongestStreak is: a slice
        // with no parseable timestamp sorts to zero, and two of those would otherwise
        // fall in whatever order the file system returned them.
        var ordered = rows
            .OrderBy(r => r.SortKey)
            .ThenBy(r => r.MatchId, StringComparer.Ordinal)
            .ToList();

        var sessions = new List<SessionRow>();
        var current = new List<MatchSummary>();

        foreach (var row in ordered)
        {
            if (current.Count > 0 && Apart(current[^1], row) > Gap)
            {
                sessions.Add(Build(current, deckOf, labelOf));
                current = [];
            }
            current.Add(row);
        }
        if (current.Count > 0) sessions.Add(Build(current, deckOf, labelOf));

        sessions.Reverse();
        return sessions;
    }

    /// <summary>
    /// The session a given match belongs to, or null when it is in none of them.
    /// </summary>
    public static SessionRow? Containing(IReadOnlyList<SessionRow> sessions, string matchId) =>
        sessions.FirstOrDefault(s => s.MatchIds.Contains(matchId, StringComparer.Ordinal));

    private static TimeSpan Apart(MatchSummary a, MatchSummary b) =>
        TimeSpan.FromMilliseconds(Math.Abs(b.SortKey - a.SortKey));

    private static SessionRow Build(
        IReadOnlyList<MatchSummary> games,
        IReadOnlyDictionary<string, string>? deckOf,
        IReadOnlyDictionary<string, string>? labelOf)
    {
        // Named by when it started, never by the date alone: a run from 22:00 to 02:00
        // is one sitting, and labelling it by date would split it across two days or
        // file the whole thing under the day it ended.
        var first = games[0];

        var decks = new List<SessionDeck>();
        if (deckOf is not null)
        {
            foreach (var group in games
                         .Where(g => deckOf.ContainsKey(g.MatchId))
                         .GroupBy(g => deckOf[g.MatchId], StringComparer.Ordinal)
                         .OrderByDescending(g => g.Count())
                         .ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                // Counted back from this deck's own last game of the sitting, so games
                // played with something else in between neither break the run nor extend
                // it. Unfinished games are neither a win nor a loss and stop the count
                // without resetting it, which is what they do everywhere else here.
                var streak = 0;
                foreach (var g in group.Reverse())
                {
                    if (Outcome(g) != 'L') break;
                    streak++;
                }

                decks.Add(new SessionDeck(
                    labelOf?.GetValueOrDefault(group.Key) ?? group.Key,
                    Won: group.Count(g => Outcome(g) == 'W'),
                    Lost: group.Count(g => Outcome(g) == 'L'),
                    Streak: streak));
            }
        }

        return new SessionRow(
            first.SortKey,
            first.Date,
            games.Count,
            // An incomplete match has no result and must not be read as a loss — the
            // mistake issue #9 was about. It stays in Games, because it was played.
            Won: games.Count(g => Outcome(g) == 'W'),
            Lost: games.Count(g => Outcome(g) == 'L'),
            Drawn: games.Count(g => Outcome(g) == 'D'),
            Decks: decks,
            MatchIds: games.Select(g => g.MatchId).ToList());
    }

    /// <summary>
    /// W, L, D, or null. Reads the rendered result for the same reason
    /// <see cref="IndexStats"/> does: that string is the one place the won/lost/drawn
    /// decision is made, and a draw is deliberately settled there before the won/lost
    /// coin flip.
    /// </summary>
    internal static char? Outcome(MatchSummary r) =>
        r.Incomplete ? null
        : r.Result.StartsWith("Won", StringComparison.Ordinal) ? 'W'
        : r.Result.StartsWith("Lost", StringComparison.Ordinal) ? 'L'
        : r.Result.StartsWith("Drew", StringComparison.Ordinal) ? 'D'
        : null;
}
