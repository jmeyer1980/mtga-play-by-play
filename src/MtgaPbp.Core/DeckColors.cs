namespace MtgaPbp.Core;

/// <summary>
/// The colour identity of the deck the local player registered, as WUBRG letters.
/// </summary>
/// <remarks>
/// Arena never sends a deck <em>name</em>, so colour is the only thing that tells one
/// archived deck from another at a glance. This is the whole of the codebase's
/// knowledge of how the card database encodes colour: <c>Cards.ColorIdentity</c> is a
/// comma-separated list of ordinals, and so is <c>Cards.Types</c>, and neither is
/// documented anywhere Arena ships.
/// </remarks>
public static class DeckColors
{
    /// <summary>
    /// Colour ordinals in the database's own numbering, which is already WUBRG order —
    /// so sorting numerically sorts correctly and no lookup table is needed for it.
    /// Verified against the shipped database: Plains 1, Island 2, Swamp 3, Mountain 4,
    /// Forest 5.
    /// </summary>
    private static readonly char[] Letters = ['W', 'U', 'B', 'R', 'G'];

    private static readonly string[] Words = ["white", "blue", "black", "red", "green"];

    /// <summary>
    /// <c>CardType</c> ordinal 5, from the database's own <c>Enums</c> table. Named
    /// here because it is the one type this file has to recognise and a bare 5 in the
    /// filter below would say nothing.
    /// </summary>
    private const int LandType = 5;

    /// <summary>What a deck of no colours renders as, the same letter MTG itself uses.</summary>
    public const string Colorless = "C";

    /// <summary>
    /// The deck's colours as WUBRG letters, <see cref="Colorless"/> for a deck of no
    /// colours, or null when the log does not say — which is every match archived
    /// before the slicer started keeping <c>ConnectResp</c>, and which has to stay
    /// tellable apart from colourless rather than collapsing into it.
    /// </summary>
    /// <param name="deckGrpIds">The registered library, duplicates and all.</param>
    /// <param name="commanderGrpIds">
    /// The registered commanders, empty outside Brawl. When there is one it decides the
    /// answer on its own: Brawl defines a deck's colours as its commander's identity and
    /// constrains the library to match, so the commander is exact where the union below
    /// is only a reconstruction. Presence is also what identifies the format here —
    /// Arena sends <c>commanderCards</c> for Brawl and nothing else — which is steadier
    /// than matching on event-name strings that change every season.
    /// </param>
    /// <param name="cards">Resolves grpIds to their colour identity.</param>
    /// <remarks>
    /// Lands are left out of the union deliberately. A Golgari deck playing a Plains for
    /// a utility land is not a three-colour deck, and counting the mana base would drag
    /// every deck toward the lands it fetches with rather than the spells it casts.
    /// <para>
    /// No splash threshold: one off-colour card in sixty still widens the string. The
    /// registered deck is a fact, and any cutoff would be a judgment the log does not
    /// support — a deck that can cast a card is a deck of that colour, whether or not
    /// the card ever came up.
    /// </para>
    /// </remarks>
    public static string? Of(
        IReadOnlyList<int> deckGrpIds, IReadOnlyList<int> commanderGrpIds, ICardDb cards)
    {
        var commanded = commanderGrpIds.Count > 0;

        // Distinct, unlike DeckList.Build, which folds by name precisely so it can
        // count copies. A colour is a property of the card and not of the deck's
        // arrangement, so four Plains say exactly what one Plains says — and the
        // fourth copy would otherwise re-split a string to reach the same answer.
        var source = (commanded ? commanderGrpIds : deckGrpIds).Distinct();

        var found = new SortedSet<int>();
        var derivable = false;

        foreach (var grpId in source)
        {
            if (cards.CardForGrpId(grpId) is not { ColorIdentity: { } identity } card)
                continue;
            if (!commanded && IsLand(card)) continue;

            // Only cards that got as far as contributing count as evidence. A deck the
            // card database cannot resolve at all, and a deck of nothing but lands, are
            // both cases where excluding what was excluded leaves nothing to judge from
            // — and "no colours found" would then read as colourless, which is a claim.
            derivable = true;

            foreach (var part in identity.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out var ordinal)
                    && ordinal >= 1 && ordinal <= Letters.Length)
                    found.Add(ordinal);
        }

        if (!derivable) return null;
        return found.Count == 0
            ? Colorless
            : new string(found.Select(o => Letters[o - 1]).ToArray());
    }

    /// <summary>
    /// The same colours spelled out — "white and blue" for <c>WU</c>. The index shows
    /// the letters and speaks this, the same split <see cref="TurnClock.Format"/> and
    /// <see cref="TurnClock.Spoken"/> already make for a match length: a run of capitals
    /// is a column heading's worth of space, and a synthesiser reads <c>WUBRG</c> as a
    /// word.
    /// </summary>
    public static string Spoken(string letters)
    {
        if (letters == Colorless) return "colourless";

        var words = letters
            .Select(c => Array.IndexOf(Letters, c))
            .Where(i => i >= 0)
            .Select(i => Words[i])
            .ToList();

        return words.Count switch
        {
            0 => "colourless",
            1 => words[0],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1]
        };
    }

    /// <summary>
    /// True when the card is a land. Read from <c>Types</c>, which is ordinals in the
    /// same comma-separated shape as the colours above — a land is any card whose type
    /// line includes <see cref="LandType"/>, so an artifact land and a creature land
    /// both count.
    /// </summary>
    public static bool IsLand(CardInfo card) =>
        card.Types.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(t => int.TryParse(t, out var type) && type == LandType);
}
