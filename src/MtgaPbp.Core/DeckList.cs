using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>
/// One line of the local player's decklist: how many copies were registered, and
/// whether any of them was ever seen during the match.
/// </summary>
/// <remarks>
/// Copies are counted by name rather than by grpId. Arena numbers art variants
/// separately, and one deck in the 24-match sample registered four cards under two
/// grpIds each — printed by id that deck reads "3× Banishing Light" and "1× Banishing
/// Light" on two lines, which is not what the player built.
/// </remarks>
public sealed record DeckEntry(string Name, int Count, bool Seen);

/// <summary>
/// The local player's registered deck, which Arena sends once per match in
/// <c>connectResp.deckMessage</c>: the library as <c>deckCards</c> — a flat array of
/// grpIds with duplicates — and, in Brawl, the commander beside it as
/// <c>commanderCards</c>. Sizes vary by format, and the message's own
/// <c>deckConstraintInfo</c> is the authority on them; Brawl's says
/// <c>maxCommanderSize: 2</c>, which is why a commander is always a list here and
/// never a single card.
/// </summary>
/// <remarks>
/// It is only ever the local player's deck: <c>systemSeatIds</c> named the same seat as
/// the match's <c>MulliganReq</c> in all 35, and the opponent's list appears nowhere in
/// the log.
/// </remarks>
public static class DeckList
{
    /// <summary>
    /// True when this envelope carries a <c>ConnectResp</c>.
    /// </summary>
    /// <remarks>
    /// This is what a new game-engine connection looks like, and it is the reason
    /// <see cref="MatchSlicer"/> has to buffer: Arena writes it about three lines
    /// <em>before</em> it names the match it opens.
    /// </remarks>
    public static bool IsConnectResp(JsonElement root)
    {
        if (Json.Obj(root, "greToClientEvent") is not { } gre) return false;
        foreach (var m in Json.Array(gre, "greToClientMessages"))
            if (Json.Str(m, "type") == "GREMessageType_ConnectResp")
                return true;
        return false;
    }

    /// <summary>
    /// The deck one <c>ConnectResp</c> message announced, or null when it carried none.
    /// The seat is the one Arena addressed the message to; the caller is expected to
    /// check it against the seat it worked out for the local player independently.
    /// Commanders is empty when the message carried no <c>commanderCards</c>, which is
    /// every non-Brawl format — absence means "no commander recorded", never "no
    /// commander", because most archived matches predate the field being kept at all.
    /// </summary>
    public static (int? Seat, IReadOnlyList<int> GrpIds, IReadOnlyList<int> Commanders)?
        ReadMessage(JsonElement message)
    {
        if (Json.Obj(message, "connectResp") is not { } resp) return null;
        if (Json.Obj(resp, "deckMessage") is not { } deck) return null;

        var grpIds = new List<int>();
        foreach (var card in Json.Array(deck, "deckCards"))
            if (Json.Int(card) is { } id)
                grpIds.Add(id);

        if (grpIds.Count == 0) return null;

        var commanders = new List<int>();
        foreach (var card in Json.Array(deck, "commanderCards"))
            if (Json.Int(card) is { } id)
                commanders.Add(id);

        int? seat = null;
        foreach (var s in Json.Array(message, "systemSeatIds"))
            if (Json.Int(s) is { } iv) { seat = iv; break; }

        return (seat, grpIds, commanders);
    }

    /// <summary>
    /// True when this envelope carries a deck. Used to tell an archived copy that
    /// predates deck capture from one that has it, so a re-capture is worth taking.
    /// </summary>
    public static bool HasDeck(JsonElement root)
    {
        if (Json.Obj(root, "greToClientEvent") is not { } gre) return false;
        foreach (var m in Json.Array(gre, "greToClientMessages"))
            if (ReadMessage(m) is not null)
                return true;
        return false;
    }

    /// <summary>
    /// Turns a flat array of grpIds into a decklist, sorted by name.
    /// </summary>
    /// <param name="grpIds">The deck as Arena sent it — duplicates and all.</param>
    /// <param name="cards">Resolves grpIds to names.</param>
    /// <param name="seenGrpIds">
    /// Every grpId the local player was seen to own during the match. Matched by id
    /// rather than by name on purpose: the transcript's <c>CardsSeen</c> is both
    /// players' cards together, so name-matching against it would report six cards
    /// across the 24-match sample as drawn when only the opponent ever had them —
    /// and "did I draw this" is the entire point of the mark.
    /// </param>
    public static IReadOnlyList<DeckEntry> Build(
        IReadOnlyList<int> grpIds, ICardDb cards, IReadOnlySet<int> seenGrpIds)
    {
        var byName = new Dictionary<string, (int Count, bool Seen)>(StringComparer.Ordinal);

        foreach (var grpId in grpIds)
        {
            // The same fallback the tracker uses, so an id missing from the card
            // database reads the same wherever it surfaces.
            var name = cards.CardForGrpId(grpId)?.Name ?? $"Card #{grpId}";
            var seen = seenGrpIds.Contains(grpId);
            byName[name] = byName.TryGetValue(name, out var e)
                ? (e.Count + 1, e.Seen || seen)
                : (1, seen);
        }

        return byName
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new DeckEntry(p.Key, p.Value.Count, p.Value.Seen))
            .ToList();
    }

    /// <summary>
    /// The commanders as names, in the order they were registered. Not
    /// <see cref="DeckEntry"/> rows: a commander begins in the command zone and can
    /// never be drawn, so the entry's <c>Seen</c> mark — whose rendered wording is
    /// "stayed in your library all match" — would be a false claim either way it fell.
    /// </summary>
    /// <remarks>
    /// Folded by name for the same reason <see cref="Build"/> folds: two grpIds naming
    /// the same card are one card in two printings. Registration order rather than
    /// alphabetical because a partner pair is one choice, not a list to be resorted.
    /// </remarks>
    public static IReadOnlyList<string> CommanderNames(IReadOnlyList<int> grpIds, ICardDb cards)
    {
        var names = new List<string>();
        foreach (var grpId in grpIds)
        {
            var name = cards.CardForGrpId(grpId)?.Name ?? $"Card #{grpId}";
            if (!names.Contains(name)) names.Add(name);
        }
        return names;
    }
}
