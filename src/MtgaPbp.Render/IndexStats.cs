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
/// One version of a deck, with its own record and what changed to produce it.
/// </summary>
/// <param name="Slug">The deck this is a version of.</param>
/// <param name="Number">Which version, counting from the oldest at 1.</param>
/// <param name="Record">Its own win-loss, over its own matches only.</param>
/// <param name="Added">Cards this version gained over the one before it.</param>
/// <param name="Removed">Cards it lost.</param>
public sealed record DeckVersionRow(
    string Slug, int Number, StatRow Record,
    IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

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

    /// <summary>
    /// The record against each opposing commander (#118) — the closest thing to a
    /// matchup table the fog of war allows. Only matches whose command zone named a
    /// commander appear; the rest are simply absent rather than pooled into a row
    /// that would mean nothing.
    /// </summary>
    IReadOnlyList<StatRow> ByOpponentDeck,

    /// <summary>
    /// The cards the opponent showed most over the decided matches, each with the record
    /// in the matches it was seen in (#137). A match counts once for every card it
    /// showed, so these add up past the match count. Capped at
    /// <see cref="OpponentCardRows"/>; basic lands left out; empty when no match recorded
    /// any.
    /// </summary>
    IReadOnlyList<StatRow> OpponentCardsMostSeen,

    /// <summary>
    /// The cards you fall shortest against: wins short of par, scaled to the sample,
    /// most short first, among cards seen in at least <see cref="OpponentCardMinimum"/>
    /// decided matches, and only those actually short of it. See
    /// <see cref="WinsVsPar"/> for why not raw losses and <see cref="ShortfallScaled"/>
    /// for why not the raw shortfall.
    /// </summary>
    IReadOnlyList<StatRow> OpponentCardsLosingTo,
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
    /// What each deck cluster is called, by slug. Taken from the clustering itself
    /// rather than from <see cref="ByDeck"/>, which keeps only decks with a decided
    /// match in them: a deck whose only game so far is unfinished has a cluster and a
    /// name but no record, and anything reading the name off the records finds nothing.
    /// </summary>
    IReadOnlyDictionary<string, string> LabelOf,

    /// <summary>
    /// Every deck's versions, oldest first, so that "did that change help" has somewhere
    /// to be asked. A deck played as only one list contributes none — there is nothing
    /// to compare it against, and a lone version restating the deck's own record would
    /// be noise on every row that never changed.
    /// </summary>
    IReadOnlyList<DeckVersionRow> DeckVersions,

    /// <summary>
    /// How each sitting went, newest first. Computed here rather than by whoever renders
    /// it so that the panel and the coach that watches a live session cannot disagree
    /// about where one night ends and the next begins.
    /// </summary>
    IReadOnlyList<SessionRow> Sessions)
{
    /// <summary>Nothing to report when nothing has a result yet.</summary>
    public bool Any => Overall.Played > 0;

    /// <summary>
    /// The fewest decided matches a card must have been seen in before it is ranked as
    /// one you lose to. Below this one result moves the rate by twenty points or more,
    /// and every 0-1 would top the list. Against the archive this was set on (1,455
    /// decided matches, 2026-09-06) it leaves 822 of 4,852 non-basic cards eligible.
    /// </summary>
    public const int OpponentCardMinimum = 5;

    /// <summary>
    /// Rows per card table. The same archive holds 4,852 distinct non-basic cards the
    /// opponent showed, and a table of all of them would be most of the page.
    /// </summary>
    public const int OpponentCardRows = 25;

    /// <summary>
    /// Wins in the matches a card was seen in, minus what <paramref name="overallRate"/>
    /// predicts for that many matches. Negative is bad news.
    /// </summary>
    /// <remarks>
    /// Ranked by this rather than by losses minus wins because the raw differential is
    /// the base rate wearing a card's name: with a losing record overall, every staple
    /// the opponent shows often comes out ahead on losses just by being seen often. On
    /// the archive this was built against, Arcane Signet (37-59) and Command Tower
    /// (44-63) topped the raw ranking — the two cards in every Brawl deck — while Day of
    /// Judgment at 3-20 came third. Against par, the staples sit near zero and the
    /// sweeper leads.
    /// </remarks>
    public static double WinsVsPar(StatRow r, double overallRate) => r.Won - r.Played * overallRate;

    /// <summary>
    /// <see cref="WinsVsPar"/> divided by the square root of the matches the card was
    /// seen in — what the losing-to table is ranked by.
    /// </summary>
    /// <remarks>
    /// The raw shortfall grows with the sample: a staple a little under par over a
    /// hundred games is ten wins short, and outranks a card that beat you nearly every
    /// time over ten. On the archive this was set against, Arcane Signet at 37-59 came
    /// second by raw shortfall, above Bloom Tender at 2-17. Dividing by the square root
    /// of the sample is the shape of a proportion's standard error, so this compares how
    /// far below par a card sits in units the sample size sets rather than in wins:
    /// Arcane Signet drops to the middle of the table and Day of Judgment 3-20, Bloom
    /// Tender 2-17, Get Lost 1-14 and Mana Drain 0-10 lead, which is the list a reader
    /// would have written. Sorting by win rate instead put nothing but 0-N cards at the
    /// top — 38 of the 822 eligible had no win at all.
    /// </remarks>
    public static double ShortfallScaled(StatRow r, double overallRate) =>
        r.Played == 0 ? 0 : WinsVsPar(r, overallRate) / Math.Sqrt(r.Played);

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

        // Only where there is more than one: a single version is the deck, and saying so
        // again under every unchanged deck would be a row of noise per deck.
        var deckVersions = clusters
            .Where(c => c.Versions.Count > 1)
            .SelectMany(c => c.Versions.Select((v, i) => new DeckVersionRow(
                c.Slug, i + 1,
                Row(c.Label, c.Slug, v.MatchIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList()),
                v.Added, v.Removed)))
            .ToList();

        // Grouped by the full commander line rather than the first name: a partner
        // pair is one deck, and two pairs sharing a commander are two decks — folding
        // either way would publish a record for a deck nobody was playing.
        var byOpponentDeck = counted
            .Where(r => r.OpponentCommanders is { Count: > 0 })
            .GroupBy(r => string.Join(" and ", r.OpponentCommanders!), StringComparer.Ordinal)
            .Select(g => Row(g.Key, null, g.ToList()))
            .OrderByDescending(r => r.Played)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        // Per card the opponent showed, over the same counted set (#137). A match
        // contributes once to every card it showed — Distinct guards a list that
        // repeats a name — so the rows add up past the match count by design, and the
        // panel's note says so. Basics are left out: Swamp was seen in 459 decided
        // matches of the archive this was built against, and all it says is which
        // colour the loss was to, which ByOpponentDeck already says better.
        var byOpponentCard = counted
            .Where(r => r.OpponentCards is { Count: > 0 })
            .SelectMany(r => r.OpponentCards!
                .Where(c => !DeckIdentity.IsBasic(c))
                .Distinct(StringComparer.Ordinal)
                .Select(c => (Card: c, Match: r)))
            .GroupBy(x => x.Card, StringComparer.Ordinal)
            .Select(g => Row(g.Key, null, g.Select(x => x.Match).ToList()))
            .ToList();

        var overall = Row("Overall", null, counted);
        var rate = overall.WinRate ?? 0;

        var mostSeen = byOpponentCard
            .OrderByDescending(r => r.Played)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(OpponentCardRows)
            .ToList();

        // Strictly short of par: a card exactly at par is not bad news, and one ahead
        // of it is the opposite. Ranked by the shortfall scaled to the sample — see
        // ShortfallScaled for why not the raw one. Ties go to the larger sample.
        var losingTo = byOpponentCard
            .Where(r => r.Played >= OpponentCardMinimum && WinsVsPar(r, rate) < 0)
            .OrderBy(r => ShortfallScaled(r, rate))
            .ThenByDescending(r => r.Played)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(OpponentCardRows)
            .ToList();

        var deckOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var labelOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in clusters)
        {
            labelOf[c.Slug] = c.Label;
            foreach (var id in c.MatchIds)
                deckOf[id] = c.Slug;
        }

        return new IndexStats(
            overall,
            byFormat,
            byDeck,
            byOpponentDeck,
            mostSeen,
            losingTo,
            LongestStreak(counted),
            Unattributed: counted.Count(r => r.Deck is null or { Count: 0 }),
            Excluded: rows.Count - counted.Count,
            DeckOf: deckOf,
            DeckVersions: deckVersions,
            LabelOf: labelOf,
            // Over every match, not only the decided ones: an unfinished game still took
            // up part of the evening, and a session that reports two games when three
            // were played is describing a night that did not happen.
            //
            // Namespace-qualified because this record has a property called Sessions,
            // which shadows the type of the same name inside its own body — unqualified,
            // this reads as the property and fails with CS0120.
            Sessions: Render.Sessions.From(rows, deckOf, labelOf));
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
