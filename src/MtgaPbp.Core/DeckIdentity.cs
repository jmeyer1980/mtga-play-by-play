namespace MtgaPbp.Core;

/// <summary>
/// One deck, as a set of matches that were played with substantially the same list.
/// </summary>
/// <param name="Label">What to call it. See <see cref="DeckIdentity"/> for how it is chosen.</param>
/// <param name="Slug">The same, reduced to a search token so a row can be filtered by it.</param>
/// <param name="MatchIds">Every match in the cluster, in the order they were given.</param>
/// <param name="Versions">
/// The distinct lists this deck was played as, oldest first. See <see cref="DeckVersion"/>.
/// </param>
public sealed record DeckCluster(
    string Label, string Slug, IReadOnlyList<string> MatchIds,
    IReadOnlyList<DeckVersion> Versions);

/// <summary>
/// One list a deck was played as, and how it differs from the list before it.
/// </summary>
/// <remarks>
/// A deck's record pools every version of it, which answers "how is this deck doing" and
/// cannot answer "did that change help" — the question anyone asks the evening they edit
/// a decklist. The versions were always in the data and were simply thrown away once the
/// cluster had been formed.
/// <para>
/// Raising <see cref="DeckIdentity.SameDeck"/> so a rebuild became its own deck was the
/// obvious alternative and does not work. Measured across the archive, consecutive
/// versions of one Elspeth deck sit at 0.956, 0.816, 0.957 and 0.875; splitting the last
/// needs a bar near 0.9, which also splits four other decks whose versions are ordinary
/// edits. Every deck fragments and the top-level record stops existing. The clustering is
/// right; only the detail underneath it was missing.
/// </para>
/// <para>
/// Versions are the distinct <em>name sets</em>, so changing how many copies of a card a
/// deck runs does not begin one. <see cref="Added"/> and <see cref="Removed"/> are what a
/// version is for, and a count-only edit has nothing to put in either.
/// </para>
/// </remarks>
/// <param name="MatchIds">Every match played on this list.</param>
/// <param name="Added">Cards this version has that the one before it did not.</param>
/// <param name="Removed">Cards the version before it had that this one does not.</param>
public sealed record DeckVersion(
    IReadOnlyList<string> MatchIds,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed);

/// <summary>
/// Groups matches by the deck they were played with, when Arena never logs a deck name.
/// </summary>
/// <remarks>
/// Arena sends the contents of a deck, never its name, so "how is this deck doing" needs
/// an identity function. The obvious one — hashing the sorted list — is wrong: it
/// fragments on every edit, and a list tweaked four times in an evening becomes four
/// decks of five games each, which is worse than no statistic at all.
/// <para>
/// So two lists are the same deck when they share <see cref="SameDeck"/> of their card
/// names, linked transitively. The 0.85 that first suggested itself would have split one
/// 213-match deck into four and another into five — exactly the fragmentation the
/// exact-hash approach was rejected for.
/// </para>
/// <para>
/// 0.60 was read off the archive rather than picked, and the reading has since gone
/// stale. It said: across 426 matches carrying a decklist, collapsing to 34 distinct
/// lists and 561 pairs, 549 of those pairs sat below 0.50 similarity and every pair above
/// 0.476 was the same deck edited — a hole in the distribution, with the threshold in the
/// hole.
/// </para>
/// <para>
/// Re-measured at 1,122 matches with a decklist — 63 distinct lists, 1,433 comparable
/// pairs, 1,386 of them still below 0.50 — <strong>the hole has closed</strong>. Either
/// side of the bar now reads 0.511, 0.516, 0.567, 0.568, 0.579, 0.581, 0.584, 0.584 │
/// 0.616, 0.619, 0.647, 0.679, 0.688, 0.704, 0.712, 0.716. The nearest values straddling
/// 0.60 are 0.584 and 0.616, and comparable edits fall on both sides of it: one Sai pair
/// differing by 19 cards each way splits at 0.568, and another differing by 16 and 17
/// merges at 0.616. Whatever this number is now, it is not a value separating two
/// populations.
/// </para>
/// <para>
/// It stays at 0.60 anyway, and the reason is <see cref="DeckVersion"/>. The question the
/// threshold used to have to answer alone — "is this the same deck?" — was doing the work
/// of two: grouping a deck across its edits, and telling those edits apart. A single
/// number cannot do both, which is why moving it trades one complaint for the other.
/// Versions answer the second question directly, for every deck, whatever the bar does.
/// So the bar only has to be roughly right, and every value tried against this archive
/// makes something else worse.
/// </para>
/// <para>
/// Counts are deliberately ignored: comparing name-with-count makes a deck that went
/// from three copies to four look like a different deck, and measured against the same
/// archive it put more pairs in the ambiguous band rather than fewer.
/// </para>
/// </remarks>
public static class DeckIdentity
{
    /// <summary>
    /// How much of their card names two lists must share to be one deck.
    /// </summary>
    /// <remarks>
    /// Read off the archive rather than chosen, though the archive has since grown out of
    /// the reading — see the remarks on <see cref="DeckIdentity"/> for what it says now,
    /// and why the answer was to stop asking this number to do two jobs rather than to
    /// move it.
    /// </remarks>
    public const double SameDeck = 0.60;

    /// <summary>
    /// The basics, as a fallback for a decklist that was built without a card database
    /// behind it — a hand-made one in a test, or an entry the database had no row for.
    /// </summary>
    /// <remarks>
    /// <see cref="DeckEntry.IsLand"/> is the real test and comes from the card type.
    /// This list existed alone first, on the reasoning that nonbasics top out at four
    /// copies and so could never win "most copies". Run against the archive that turned
    /// out to be wrong for small decks, where the most-played spell also has four: a
    /// three-match deck came out named "Dimir Guildgate". Cheap assumption, visible
    /// failure, and the card type was already being read two files away.
    /// </remarks>
    private static readonly HashSet<string> Basics = new(StringComparer.Ordinal)
    {
        "Plains", "Island", "Swamp", "Mountain", "Forest", "Wastes",
        "Snow-Covered Plains", "Snow-Covered Island", "Snow-Covered Swamp",
        "Snow-Covered Mountain", "Snow-Covered Forest", "Snow-Covered Wastes"
    };

    /// <summary>
    /// Whether a name is a basic land — the printed basics and their snow forms. The
    /// index leaves these out of its opponent-card tables (#137): a basic says which
    /// colours a match was against and nothing about the deck.
    /// </summary>
    public static bool IsBasic(string name) => Basics.Contains(name);

    private static bool IsLand(DeckEntry card) => card.IsLand || Basics.Contains(card.Name);

    /// <summary>
    /// Groups matches into decks. Matches whose log carried no decklist are left out
    /// entirely — they are not a deck, and the caller has to say so rather than let them
    /// vanish into a total that no longer adds up.
    /// </summary>
    public static IReadOnlyList<DeckCluster> Cluster(
        IEnumerable<(string MatchId, IReadOnlyList<DeckEntry> Deck, string? Commander)> matches)
    {
        var played = matches.Where(m => m.Deck.Count > 0).ToList();
        if (played.Count == 0) return [];

        // Identical lists collapse first. An archive is mostly the same deck played
        // again, and comparing every match against every other match would spend its
        // time re-deciding that a list is itself. The commander is part of what makes
        // a list itself: the same ninety-nine behind a different commander is a
        // different deck, and in Brawl it is the difference that matters most.
        var lists = new List<(HashSet<string> Names, string? Commander, List<int> Members)>();
        var byShape = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < played.Count; i++)
        {
            var shape = played[i].Commander + " " + string.Join("|", played[i].Deck
                .Select(c => $"{c.Name}:{c.Count}")
                .OrderBy(s => s, StringComparer.Ordinal));

            if (byShape.TryGetValue(shape, out var at)) lists[at].Members.Add(i);
            else
            {
                byShape[shape] = lists.Count;
                lists.Add((played[i].Deck.Select(c => c.Name).ToHashSet(StringComparer.Ordinal),
                    played[i].Commander, [i]));
            }
        }

        // Single linkage: A and B are one deck, B and C are one deck, so all three are,
        // even where A and C alone would not have met the bar. That is the right shape
        // for a deck that was edited over weeks — each version resembles the one before
        // it, and the first and last need not resemble each other at all.
        var parent = Enumerable.Range(0, lists.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        for (var a = 0; a < lists.Count; a++)
            for (var b = a + 1; b < lists.Count; b++)
            {
                if (Find(a) == Find(b)) continue;

                // Two known and different commanders are two decks however much of the
                // library they share — which in one colour identity, built out of one
                // collection, is routinely most of it. Without this, two Brawl decks
                // merge into one row reporting one combined record under one of the two
                // names. The archive has not reached that yet: its closest pair of
                // different-commander decks sits at 0.43 against a bar of 0.60. Close
                // enough that the next deck in those colours could do it.
                if (lists[a].Commander is { Length: > 0 } first &&
                    lists[b].Commander is { Length: > 0 } second &&
                    !string.Equals(first, second, StringComparison.Ordinal)) continue;

                if (Similarity(lists[a].Names, lists[b].Names) >= SameDeck)
                    parent[Find(a)] = Find(b);
            }

        var clusters = new Dictionary<int, List<int>>();
        for (var i = 0; i < lists.Count; i++)
        {
            var root = Find(i);
            if (!clusters.TryGetValue(root, out var members)) clusters[root] = members = [];
            members.Add(i);
        }

        // Named in an order that does not depend on the order the matches arrived in.
        // The caller hands these over newest-first, so numbering them as encountered
        // gave the unnumbered name to whichever deck had been played most recently —
        // two decks swapping names, and swapping filter slugs, overnight.
        var grouped = clusters.Values
            .Select(group =>
            {
                var members = group.SelectMany(l => lists[l].Members).OrderBy(i => i).ToList();
                return (
                    Members: members,
                    Versions: VersionsOf(group, lists, played),
                    Label: Name(group.Select(l => (lists[l].Commander, played[lists[l].Members[0]].Deck))),
                    Anchor: members.Select(i => played[i].MatchId).Min(StringComparer.Ordinal)!);
            })
            .OrderBy(c => c.Label, StringComparer.Ordinal)
            .ThenBy(c => c.Anchor, StringComparer.Ordinal)
            .ToList();

        var named = new List<DeckCluster>();
        var shares = grouped.GroupBy(c => c.Label, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var c in grouped)
        {
            // When a name is shared, every claimant is numbered rather than only the
            // ones after the first. An unnumbered "Hare Apparent" beside a numbered
            // "Hare Apparent (2)" produces slugs where one is a prefix of the other,
            // and the table's filter matches on substrings.
            var nth = seen.GetValueOrDefault(c.Label) + 1;
            seen[c.Label] = nth;
            var unique = shares.Contains(c.Label) ? $"{c.Label} ({nth})" : c.Label;

            named.Add(new DeckCluster(unique, Slug(unique),
                c.Members.Select(i => played[i].MatchId).ToList(),
                c.Versions));
        }

        return named.OrderByDescending(c => c.MatchIds.Count).ThenBy(c => c.Label, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The distinct lists inside one cluster, oldest first, each diffed against the one
    /// before it.
    /// </summary>
    /// <remarks>
    /// Ordered by the caller's own ordering rather than by a timestamp this method is not
    /// given: matches arrive newest first (the same assumption the cluster anchor above
    /// rests on), so the version holding the highest index is the one played longest ago.
    /// <para>
    /// Grouped by name set rather than by the shape used for clustering, because the
    /// shape includes counts and two lists differing only in how many Swamps they run
    /// have nothing to show as added or removed.
    /// </para>
    /// </remarks>
    private static List<DeckVersion> VersionsOf(
        List<int> group,
        List<(HashSet<string> Names, string? Commander, List<int> Members)> lists,
        List<(string MatchId, IReadOnlyList<DeckEntry> Deck, string? Commander)> played)
    {
        var byNames = new List<(HashSet<string> Names, List<int> Members)>();
        foreach (var l in group)
        {
            var hit = byNames.FirstOrDefault(v => v.Names.SetEquals(lists[l].Names));
            if (hit.Members is null) byNames.Add((lists[l].Names, [.. lists[l].Members]));
            else hit.Members.AddRange(lists[l].Members);
        }

        // Oldest first: the newest match is index 0, so the version reaching furthest
        // down the input started earliest.
        var ordered = byNames.OrderByDescending(v => v.Members.Max()).ToList();

        var versions = new List<DeckVersion>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var previous = i == 0 ? null : ordered[i - 1].Names;
            versions.Add(new DeckVersion(
                ordered[i].Members.OrderBy(m => m).Select(m => played[m].MatchId).ToList(),
                previous is null ? [] : ordered[i].Names.Except(previous).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                previous is null ? [] : previous.Except(ordered[i].Names).OrderBy(n => n, StringComparer.Ordinal).ToList()));
        }
        return versions;
    }

    /// <summary>How much of their card names two lists share, ignoring how many of each.</summary>
    public static double Similarity(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count <= b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>
    /// What to call a cluster: its commander, or the card it runs most copies of.
    /// </summary>
    /// <remarks>
    /// In Brawl the commander is the deck's name in every practical sense, and it is
    /// what the log already carries. Only about a third of an archive is Brawl, so the
    /// rest fall back to the most-copied non-land — which is the card the deck is built
    /// around often enough to read as a name: "Hare Apparent", "Zahid, Djinn of the
    /// Lamp".
    /// <para>
    /// Pooled across the cluster rather than taken from one list, and pooled over its
    /// <em>distinct lists</em> rather than over its matches. Taken from one list the
    /// label disagreed between lists of the same deck in 2 of 25 clusters. Weighted by
    /// matches it would move whenever a game was played with a version already in the
    /// cluster — the deck renaming itself, and changing its filter slug, for having
    /// been played again. Either way a label that moves when a new match lands makes
    /// the panel look broken, which is the whole reason to pool.
    /// </para>
    /// </remarks>
    private static string Name(IEnumerable<(string? Commander, IReadOnlyList<DeckEntry> Deck)> lists)
    {
        var all = lists.ToList();

        var commander = all.Select(m => m.Commander)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        if (commander is not null) return commander;

        var pooled = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var card in all.SelectMany(m => m.Deck).Where(c => !IsLand(c)))
            pooled[card.Name] = pooled.GetValueOrDefault(card.Name) + card.Count;

        return pooled.Count == 0
            ? "Unnamed deck"
            : pooled.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
    }

    /// <summary>
    /// The label as a search token. Lowercase, and everything that is not a letter or a
    /// digit becomes a hyphen, so it survives being typed into a search box by hand.
    /// </summary>
    public static string Slug(string label)
    {
        var slug = new System.Text.StringBuilder(label.Length);
        foreach (var c in label.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        // Never empty: a blank slug becomes a bare "deck:" token, which matches every
        // row that carries any deck at all.
        var token = slug.ToString().Trim('-');
        return token.Length > 0 ? token : "deck";
    }
}
