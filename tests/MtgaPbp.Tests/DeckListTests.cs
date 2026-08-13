using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The local player's registered deck, which Arena sends once per match as
/// <c>connectResp.deckMessage.deckCards</c>.
/// </summary>
public class DeckListTests
{
    /// <summary>
    /// Two grpIds for "Banishing Light" because Arena numbers art variants separately,
    /// and one deck in the 24-match sample registers four of its cards that way.
    /// </summary>
    private sealed class Cards : ICardDb
    {
        public string? NameForLocId(int locId) => null;

        public CardInfo? CardForGrpId(int grpId) => grpId switch
        {
            7 => new CardInfo(7, "Plains", "Land", null, null, false),
            9 => new CardInfo(9, "Banishing Light", "Enchantment", null, null, false),
            10 => new CardInfo(10, "Banishing Light", "Enchantment", null, null, false),
            11 => new CardInfo(11, "Arcane Signet", "Artifact", null, null, false),
            _ => null
        };

        public string? EnumName(string type, int value) => null;
        public string? AbilityText(int abilityGrpId) => null;
    }

    private static IReadOnlyList<DeckEntry> Build(int[] grpIds, params int[] seen) =>
        DeckList.Build(grpIds, new Cards(), seen.ToHashSet());

    [Test]
    public void Build_counts_copies_and_sorts_by_name()
    {
        var deck = Build([7, 11, 7, 7]);

        Assert.That(deck.Select(d => d.Name), Is.EqualTo(new[] { "Arcane Signet", "Plains" }));
        Assert.That(deck.Select(d => d.Count), Is.EqualTo(new[] { 1, 3 }));
    }

    /// <summary>
    /// Grouped by name rather than by grpId. Printed by id, a deck holding two art
    /// variants of the same card reads as "3× Banishing Light" and "1× Banishing
    /// Light" on separate lines, which is not the deck the player built.
    /// </summary>
    [Test]
    public void Build_folds_art_variants_that_share_a_name_into_one_line()
    {
        var deck = Build([9, 9, 10]);

        Assert.That(deck, Has.Count.EqualTo(1));
        Assert.That(deck[0].Name, Is.EqualTo("Banishing Light"));
        Assert.That(deck[0].Count, Is.EqualTo(3));
    }

    [Test]
    public void Build_marks_a_card_seen_when_any_one_of_its_variants_was()
    {
        // Seeing the other printing is still seeing the card.
        Assert.That(Build([9, 10], seen: 10).Single().Seen, Is.True);
        Assert.That(Build([9, 10]).Single().Seen, Is.False);
    }

    [Test]
    public void Build_names_a_card_the_database_does_not_know()
    {
        // Same fallback the tracker uses, so an id missing from the card database
        // reads identically wherever it surfaces, and `stats` can still find it.
        Assert.That(Build([404]).Single().Name, Is.EqualTo("Card #404"));
    }

    // ---------- attribution, end to end through the extractor ----------

    private const string RoomLine = """
    { "timestamp": "1000", "matchGameRoomStateChangedEvent": { "gameRoomInfo": {
        "gameRoomConfig": { "matchId": "m1", "reservedPlayers": [
          { "userId": "ME", "playerName": "PlayerOne", "systemSeatId": 1,
            "teamId": 1, "platformId": "SteamWindows", "eventId": "Ladder" },
          { "userId": "THEM", "playerName": "PlayerTwo", "systemSeatId": 2,
            "teamId": 2, "platformId": "iPhone", "eventId": "Ladder" } ] } } } }
    """;

    /// <summary>Seat 1 is the local player, as far as the extractor is concerned.</summary>
    private const string MulliganLine = """
    { "timestamp": "1001", "greToClientEvent": { "greToClientMessages": [
      { "type": "GREMessageType_MulliganReq", "systemSeatIds": [ 1 ] } ] } }
    """;

    private static string Connect(int seat, params int[] grpIds) => $$"""
    { "timestamp": "999", "greToClientEvent": { "greToClientMessages": [
      { "type": "GREMessageType_ConnectResp", "systemSeatIds": [ {{seat}} ],
        "connectResp": { "deckMessage": {
          "deckCards": [ {{string.Join(", ", grpIds)}} ] } } } ] } }
    """;

    /// <summary>A game object the named seat owns, which is what "seen" means.</summary>
    private static string Owned(int seat, int grpId, int instanceId) => $$"""
    { "timestamp": "1002", "greToClientEvent": { "greToClientMessages": [
      { "type": "GREMessageType_GameStateMessage", "gameStateMessage": {
        "gameObjects": [ { "instanceId": {{instanceId}}, "grpId": {{grpId}},
                           "ownerSeatId": {{seat}}, "controllerSeatId": {{seat}} } ] } } ] } }
    """;

    private static Transcript Run(params string[] lines) =>
        new EventExtractor(new Cards()).Extract("m1", lines);

    [Test]
    public void Extract_reads_the_deck_addressed_to_the_local_seat()
    {
        var t = Run(Connect(1, 7, 7, 11), RoomLine, MulliganLine);

        Assert.That(t.You!.Seat, Is.EqualTo(1));
        Assert.That(t.Deck.Select(d => $"{d.Count}x {d.Name}"),
            Is.EqualTo(new[] { "1x Arcane Signet", "2x Plains" }));
    }

    /// <summary>
    /// The seat is checked rather than trusted. It named the local player in all 35
    /// occurrences across the current logs, but the one archived match where it did not
    /// had been mis-sliced — and showing a reader the wrong deck is worse than showing
    /// them none.
    /// </summary>
    [Test]
    public void Extract_refuses_a_deck_addressed_to_the_other_seat()
    {
        Assert.That(Run(Connect(2, 7, 7), RoomLine, MulliganLine).Deck, Is.Empty);
    }

    /// <summary>
    /// And refuses one addressed to nobody, for the same reason. Every occurrence in
    /// the logs names a seat, so a message that does not is Arena behaving in a way
    /// this was never measured against — which is not the moment to start guessing.
    /// </summary>
    [Test]
    public void Extract_refuses_a_deck_that_names_no_seat_at_all()
    {
        const string unaddressed = """
        { "timestamp": "999", "greToClientEvent": { "greToClientMessages": [
          { "type": "GREMessageType_ConnectResp",
            "connectResp": { "deckMessage": { "deckCards": [ 7 ] } } } ] } }
        """;

        Assert.That(Run(unaddressed, RoomLine, MulliganLine).Deck, Is.Empty);
    }

    [Test]
    public void Extract_has_no_deck_when_the_log_carried_none()
    {
        // Every match archived before the slicer stopped discarding ConnectResp.
        Assert.That(Run(RoomLine, MulliganLine).Deck, Is.Empty);
    }

    [Test]
    public void Extract_takes_the_last_deck_message_that_reaches_the_match()
    {
        // A slice carries one today. Where two could ever arrive, the later one is the
        // nearer to the match it opens.
        var t = Run(Connect(1, 7), Connect(1, 11), RoomLine, MulliganLine);
        Assert.That(t.Deck.Single().Name, Is.EqualTo("Arcane Signet"));
    }

    [Test]
    public void Extract_marks_a_card_that_never_left_the_library()
    {
        var t = Run(Connect(1, 7, 11), RoomLine, MulliganLine, Owned(1, 7, 100));

        Assert.That(t.Deck.Single(d => d.Name == "Plains").Seen, Is.True);
        Assert.That(t.Deck.Single(d => d.Name == "Arcane Signet").Seen, Is.False);
    }

    /// <summary>
    /// Matched on the owner's id, not on the name. The transcript's <c>CardsSeen</c> is
    /// both players' cards together, so name-matching would report six cards across the
    /// 24-match sample as drawn when only the opponent ever had one — and "did I draw
    /// this" is the whole point of the mark.
    /// </summary>
    [Test]
    public void Extract_does_not_credit_you_with_a_card_only_the_opponent_played()
    {
        var t = Run(Connect(1, 7), RoomLine, MulliganLine, Owned(2, 7, 200));
        Assert.That(t.Deck.Single().Seen, Is.False);
    }

    /// <summary>
    /// The deck stays out of the search index. A card you registered but never drew is
    /// not a card this match is about, and matching it in the index's card search would
    /// turn every search into "games where I owned this", which is every game.
    /// </summary>
    [Test]
    public void Extract_keeps_never_drawn_cards_out_of_the_cards_seen_set()
    {
        var t = Run(Connect(1, 7, 11), RoomLine, MulliganLine, Owned(1, 7, 100));
        Assert.That(t.CardsSeen, Does.Not.Contain("Arcane Signet"));
    }
}
