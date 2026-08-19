namespace MtgaPbp.Core;

/// <summary>
/// One deck, as a set of matches that were played with substantially the same list.
/// </summary>
/// <param name="Label">What to call it. See <see cref="DeckIdentity"/> for how it is chosen.</param>
/// <param name="Slug">The same, reduced to a search token so a row can be filtered by it.</param>
/// <param name="MatchIds">Every match in the cluster, in the order they were given.</param>
public sealed record DeckCluster(string Label, string Slug, IReadOnlyList<string> MatchIds);

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
/// names, linked transitively. Both numbers here were read off the archive rather than
/// picked: across 426 matches carrying a decklist, which collapse to 34 distinct lists
/// and 561 pairs, 549 of those pairs sit below 0.50 similarity and **every pair above
/// 0.476 is the same deck edited**. The distribution has a hole in it, and the threshold
/// sits in the hole. The 0.85 that first suggested itself would have split one 213-match
/// deck into four and another into five — exactly the fragmentation the exact-hash
/// approach was rejected for.
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
    /// How much of their card names two lists must share to be one deck. Read off the
    /// archive, not chosen: see the remarks on <see cref="DeckIdentity"/>.
    /// </summary>
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
        // time re-deciding that a list is itself.
        var lists = new List<(HashSet<string> Names, List<int> Members)>();
        var byShape = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < played.Count; i++)
        {
            var shape = string.Join("|", played[i].Deck
                .Select(c => $"{c.Name}:{c.Count}")
                .OrderBy(s => s, StringComparer.Ordinal));

            if (byShape.TryGetValue(shape, out var at)) lists[at].Members.Add(i);
            else
            {
                byShape[shape] = lists.Count;
                lists.Add((played[i].Deck.Select(c => c.Name).ToHashSet(StringComparer.Ordinal), [i]));
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

        var named = new List<DeckCluster>();
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in clusters.Values)
        {
            var members = group.SelectMany(l => lists[l].Members).OrderBy(i => i).ToList();
            var label = Name(members.Select(i => played[i]));

            // Two clusters answering to one name would make the table unreadable and
            // the filter ambiguous, so the second one onward is numbered.
            var seen = used.GetValueOrDefault(label);
            used[label] = seen + 1;
            var unique = seen == 0 ? label : $"{label} ({seen + 1})";

            named.Add(new DeckCluster(unique, Slug(unique),
                members.Select(i => played[i].MatchId).ToList()));
        }

        return named.OrderByDescending(c => c.MatchIds.Count).ThenBy(c => c.Label, StringComparer.Ordinal).ToList();
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
    /// Pooled across the whole cluster rather than taken from one list, and that is the
    /// part that matters: computed per list it disagreed between lists of the same deck
    /// in 2 of 25 clusters, so the label would have changed as the deck was edited. A
    /// label that moves when a new match lands makes the panel look broken.
    /// </para>
    /// </remarks>
    private static string Name(
        IEnumerable<(string MatchId, IReadOnlyList<DeckEntry> Deck, string? Commander)> members)
    {
        var all = members.ToList();

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
        return slug.ToString().Trim('-');
    }
}
