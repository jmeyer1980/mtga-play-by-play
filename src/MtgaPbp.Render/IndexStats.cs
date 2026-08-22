using MtgaPbp.Core;

namespace MtgaPbp.Render;

/// <summary>
/// A win-loss record over some set of matches, and the few things worth saying about it.
/// </summary>
/// <param name="Name">What the row is a record for — a format, a deck, or everything.</param>
/// <param name="Slug">A search token for the row, or null when it is not filterable.</param>
/// <param name="TurnsInWins">Median turns of the wins, or null when there are none.</param>
/// <param name="TurnsInLosses">Median turns of the losses, or null when there are none.</param>
/// <param name="OnThePlay">
/// How many of <paramref name="WithOpening"/> began on the play. Carried beside its own
/// denominator because the log did not record an opening for older matches, and dividing
/// by the match count would report those as having been on the draw.
/// </param>
public sealed record StatRow(
    string Name, string? Slug, int Won, int Lost, int Drawn,
    int? TurnsInWins = null, int? TurnsInLosses = null,
    int OnThePlay = 0, int WithOpening = 0)
{
    /// <summary>Matches counted here. Incomplete ones are not among them.</summary>
    public int Played => Won + Lost + Drawn;

    /// <summary>
    /// Wins over matches played, or null when nothing was played. A draw counts as a
    /// match played and not as a win, which is the only reading that keeps the three
    /// numbers and the percentage telling the same story.
    /// </summary>
    public double? WinRate => Played == 0 ? null : (double)Won / Played;
}

/// <summary>
/// The record the index reports above its table: overall, by format, and by deck.
/// </summary>
/// <remarks>
/// Kept apart from the renderer because it is arithmetic with four correctness traps in
/// it, and arithmetic can be tested without parsing HTML.
/// <para>
/// The traps, all of them ways to publish a confident wrong number: an incomplete match
/// has no result and must not be counted as a loss; a match whose log carried no
/// decklist still happened and still belongs in the overall and per-format records, so
/// it is reported as unattributed rather than dropped; the on-the-play split needs its
/// own denominator for the same reason; and a longest-streak is a claim about order, so
/// it has to be read in the order the matches were played rather than the order they
/// happen to be listed in.
/// </para>
/// </remarks>
public sealed record IndexStats(
    StatRow Overall,
    IReadOnlyList<StatRow> ByFormat,
    IReadOnlyList<StatRow> ByDeck,
    int LongestWinStreak,
    int Unattributed,
    int Excluded,
    /// <summary>
    /// Which deck each match belongs to, by slug. The table's rows carry this so one
    /// click on a deck in the panel can filter to that deck's matches, using the search
    /// the index already has rather than a page it does not.
    /// </summary>
    IReadOnlyDictionary<string, string> DeckOf,

    /// <summary>
    /// How each sitting went, newest first. Computed here rather than by whoever renders
    /// it so that the panel and the coach that watches a live session cannot disagree
    /// about where one night ends and the next begins.
    /// </summary>
    IReadOnlyList<SessionRow> Sessions)
{
    /// <summary>Nothing to report when nothing has a result yet.</summary>
    public bool Any => Overall.Played > 0;

    public static IndexStats From(IReadOnlyList<MatchSummary> rows)
    {
        // An unfinished match has no result. Counting it as a loss is the shape of
        // mistake issue #9 was about, so it is counted out here, once, and every record
        // below is built from what is left.
        var counted = rows.Where(r => !r.Incomplete && Outcome(r) is not null).ToList();

        var byFormat = counted
            .GroupBy(r => r.EventName, StringComparer.Ordinal)
            .Select(g => Row(g.Key, null, g.ToList()))
            .OrderByDescending(r => r.Played)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        // Clustered over every match carrying a decklist, not only the decided ones:
        // the panel counts what has a result, but "show only this deck's matches" has
        // to mean all of them or the control does not do what its label says.
        var clusters = DeckIdentity.Cluster(rows
            .Where(r => r.Deck is { Count: > 0 })
            .Select(r => (r.MatchId, r.Deck!, r.Commander)));

        // Grouped rather than keyed directly: a duplicate id cannot reach here through
        // the archive, which is keyed by id itself, but the cost of being wrong about
        // that is an unhandled exception that takes down the whole index build.
        var byId = counted.GroupBy(r => r.MatchId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byDeck = clusters
            .Select(c => Row(c.Label, c.Slug,
                c.MatchIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList()))
            .Where(r => r.Played > 0)
            .OrderByDescending(r => r.Played)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        var deckOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in clusters)
            foreach (var id in c.MatchIds)
                deckOf[id] = c.Slug;

        return new IndexStats(
            Row("Overall", null, counted),
            byFormat,
            byDeck,
            LongestStreak(counted),
            Unattributed: counted.Count(r => r.Deck is null or { Count: 0 }),
            Excluded: rows.Count - counted.Count,
            DeckOf: deckOf,
            // Over every match, not only the decided ones: an unfinished game still took
            // up part of the evening, and a session that reports two games when three
            // were played is describing a night that did not happen.
            //
            // Namespace-qualified because this record has a property called Sessions,
            // which shadows the type of the same name inside its own body — unqualified,
            // this reads as the property and fails with CS0120.
            Sessions: Render.Sessions.From(rows, deckOf,
                byDeck.Where(d => d.Slug is not null)
                      .ToDictionary(d => d.Slug!, d => d.Name, StringComparer.Ordinal)));
    }

    private static StatRow Row(string name, string? slug, IReadOnlyList<MatchSummary> rows)
    {
        var turns = rows.Where(r => r.Turns > 0).ToList();
        var opened = rows.Where(r => r.OnThePlay is not null).ToList();

        return new StatRow(
            name, slug,
            Won: rows.Count(r => Outcome(r) == 'W'),
            Lost: rows.Count(r => Outcome(r) == 'L'),
            Drawn: rows.Count(r => Outcome(r) == 'D'),
            TurnsInWins: Median(turns.Where(r => Outcome(r) == 'W').Select(r => r.Turns)),
            TurnsInLosses: Median(turns.Where(r => Outcome(r) == 'L').Select(r => r.Turns)),
            OnThePlay: opened.Count(r => r.OnThePlay is true),
            WithOpening: opened.Count);
    }

    /// <summary>
    /// W, L, D, or null for a match with no result. Read from the rendered result string
    /// because that is the one place the won/lost/drawn decision is made — see
    /// <c>TranscriptSummary.Result</c>, where a draw deliberately comes before the
    /// won/lost coin flip.
    /// </summary>
    private static char? Outcome(MatchSummary r) =>
        r.Result.StartsWith("Won", StringComparison.Ordinal) ? 'W'
        : r.Result.StartsWith("Lost", StringComparison.Ordinal) ? 'L'
        : r.Result.StartsWith("Drew", StringComparison.Ordinal) ? 'D'
        : null;

    private static int? Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;

        // The lower of the two middles on an even count, rather than their mean: these
        // are turn numbers, and half a turn is not one.
        return sorted[(sorted.Count - 1) / 2];
    }

    /// <summary>
    /// The longest run of wins, read oldest-first. A streak is a claim about the order
    /// the matches were played in, and the index is sorted newest-first, so reading it
    /// off the display order would report the longest run backwards.
    /// </summary>
    private static int LongestStreak(IReadOnlyList<MatchSummary> counted)
    {
        var best = 0;
        var run = 0;
        // Tie-broken by id: a match whose slice carried no parseable timestamp gets a
        // SortKey of zero, and any two of those would otherwise be read in file order,
        // which can change the answer by one.
        foreach (var r in counted.OrderBy(r => r.SortKey).ThenBy(r => r.MatchId, StringComparer.Ordinal))
        {
            run = Outcome(r) == 'W' ? run + 1 : 0;
            if (run > best) best = run;
        }
        return best;
    }
}
