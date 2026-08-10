using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class EventExtractorTests
{
    private sealed class FakeCardDb : ICardDb
    {
        public string? NameForLocId(int locId) => locId switch
        {
            648 => "Plains",
            1000 => "Lightning Bolt",
            1001 => "Llanowar Elves",
            _ => null
        };
        public CardInfo? CardForGrpId(int grpId) => null;
    }

    private static Transcript Run(params string[] lines) =>
        new EventExtractor(new FakeCardDb()).Extract("m1", lines);

    private const string RoomLine = """
    { "timestamp": "1000", "matchGameRoomStateChangedEvent": { "gameRoomInfo": {
        "gameRoomConfig": { "matchId": "m1", "reservedPlayers": [
          { "userId": "ME", "playerName": "PlayerOne", "systemSeatId": 1,
            "teamId": 1, "platformId": "SteamWindows", "eventId": "Ladder" },
          { "userId": "THEM", "playerName": "PlayerTwo", "systemSeatId": 2,
            "teamId": 2, "platformId": "iPhone", "eventId": "Ladder" } ] } } } }
    """;

    private const string MulliganLine = """
    { "timestamp": "1001", "greToClientEvent": { "greToClientMessages": [
      { "type": "GREMessageType_MulliganReq", "systemSeatIds": [ 1 ] } ] } }
    """;

    private static string Gre(string gsmBody) => $$"""
    { "timestamp": "1002", "greToClientEvent": { "greToClientMessages": [
      { "type": "GREMessageType_GameStateMessage", "gameStateMessage": {{gsmBody}} } ] } }
    """;

    [Test]
    public void Extract_reads_player_names_and_event_name()
    {
        var t = Run(RoomLine, MulliganLine);
        Assert.That(t.You!.ScreenName, Is.EqualTo("PlayerOne"));
        Assert.That(t.Opponent!.ScreenName, Is.EqualTo("PlayerTwo"));
        Assert.That(t.EventName, Is.EqualTo("Ladder"));
    }

    [Test]
    public void Extract_resolves_local_seat_from_mulligan_request()
    {
        var t = Run(RoomLine, MulliganLine);
        Assert.That(t.You!.Seat, Is.EqualTo(1));
        Assert.That(t.Opponent!.Seat, Is.EqualTo(2));
    }

    [Test]
    public void Extract_resolves_local_seat_from_actions_available_when_no_mulligan()
    {
        var actions = """
        { "timestamp": "1001", "greToClientEvent": { "greToClientMessages": [
          { "type": "GREMessageType_ActionsAvailableReq", "systemSeatIds": [ 2 ] } ] } }
        """;
        var t = Run(RoomLine, actions);
        Assert.That(t.You!.Seat, Is.EqualTo(2));
    }

    [Test]
    public void Extract_emits_land_played_from_zone_transfer_category()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "turnInfo": { "turnNumber": 1, "activePlayer": 1 },
          "gameObjects": [ { "instanceId": 430, "grpId": 96179, "name": 648,
                             "type": "GameObjectType_Card", "controllerSeatId": 1 } ],
          "annotations": [ { "id": 111, "affectedIds": [ 430 ],
            "type": [ "AnnotationType_ZoneTransfer" ], "details": [
              { "key": "zone_src",  "valueInt32": [ 35 ] },
              { "key": "zone_dest", "valueInt32": [ 28 ] },
              { "key": "category", "valueString": [ "PlayLand" ] } ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.LandPlayed);
        Assert.That(e.SourceName, Is.EqualTo("Plains"));
        Assert.That(e.Turn, Is.EqualTo(1));
    }

    [Test]
    public void Extract_maps_each_zone_transfer_category_to_its_kind()
    {
        foreach (var (category, expected) in new[]
        {
            ("CastSpell", EventKind.SpellCast),
            ("Resolve",   EventKind.Resolved),
            ("Draw",      EventKind.Drew),
            ("Discard",   EventKind.Discarded),
            ("Destroy",   EventKind.Destroyed),
            ("Sacrifice", EventKind.Sacrificed),
            ("Exile",     EventKind.Exiled),
            ("Return",    EventKind.Returned),
            ("Countered", EventKind.Countered),
            ("SBA_Damage", EventKind.StateBasedAction),
            ("Put",       EventKind.ZoneMove),
        })
        {
            var t = Run(RoomLine, MulliganLine, Gre($$"""
            { "type": "GameStateType_Full",
              "gameObjects": [ { "instanceId": 1, "grpId": 1, "name": 1000,
                                 "type": "GameObjectType_Card" } ],
              "annotations": [ { "id": 1, "affectedIds": [ 1 ],
                "type": [ "AnnotationType_ZoneTransfer" ], "details": [
                  { "key": "category", "valueString": [ "{{category}}" ] } ] } ] }
            """));
            Assert.That(t.Events.Select(x => x.Kind), Does.Contain(expected),
                $"category {category} should map to {expected}");
        }
    }

    [Test]
    public void Extract_emits_damage_with_source_and_amount()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 436, "grpId": 5, "name": 1000,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 248, "affectorId": 436, "affectedIds": [ 1 ],
            "type": [ "AnnotationType_DamageDealt" ], "details": [
              { "key": "damage", "valueInt32": [ 2 ] } ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.Damage);
        Assert.That(e.SourceName, Is.EqualTo("Lightning Bolt"));
        Assert.That(e.Amount, Is.EqualTo(2));
        Assert.That(e.TargetSeat, Is.EqualTo(1), "affectedIds 1 and 2 are player seats");
    }

    [Test]
    public void Extract_emits_life_change_with_delta()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 252, "affectedIds": [ 1 ], "type": [ "AnnotationType_ModifiedLife" ],
            "details": [ { "key": "life", "valueInt32": [ -2 ] } ] } ] }
        """));
        var e = t.Events.Single(x => x.Kind == EventKind.LifeChanged);
        Assert.That(e.Amount, Is.EqualTo(-2));
        Assert.That(e.TargetSeat, Is.EqualTo(1));
    }

    [Test]
    public void Extract_emits_turn_start()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "turnInfo": { "turnNumber": 3, "activePlayer": 2 },
          "annotations": [ { "id": 106, "affectorId": 2, "affectedIds": [ 2 ],
            "type": [ "AnnotationType_NewTurnStarted" ] } ] }
        """));
        var e = t.Events.Single(x => x.Kind == EventKind.TurnStart);
        Assert.That(e.Turn, Is.EqualTo(3));
        Assert.That(e.ActorSeat, Is.EqualTo(2));
    }

    [Test]
    public void Extract_records_unknown_annotations_without_dropping_them()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 1, "type": [ "AnnotationType_SomethingBrandNew" ] } ] }
        """));
        Assert.That(t.UnknownAnnotations["AnnotationType_SomethingBrandNew"], Is.EqualTo(1));
        Assert.That(t.Events.Any(x => x.Kind == EventKind.Unknown), Is.True);
    }

    [Test]
    public void Extract_reads_final_result_and_game_record()
    {
        var final = """
        { "timestamp": "2000", "matchGameRoomStateChangedEvent": { "gameRoomInfo": {
            "gameRoomConfig": { "matchId": "m1" },
            "finalMatchResult": { "matchId": "m1", "resultList": [
              { "scope": "MatchScope_Game",  "winningTeamId": 1 },
              { "scope": "MatchScope_Game",  "winningTeamId": 2 },
              { "scope": "MatchScope_Game",  "winningTeamId": 1 },
              { "scope": "MatchScope_Match", "winningTeamId": 1 } ] } } } }
        """;
        var t = Run(RoomLine, MulliganLine, final);
        Assert.That(t.WinningTeamId, Is.EqualTo(1));
        Assert.That(t.GamesWon, Is.EqualTo(2));
        Assert.That(t.GamesLost, Is.EqualTo(1));
        Assert.That(t.Events.Any(x => x.Kind == EventKind.GameEnd), Is.True);
    }

    [Test]
    public void Extract_collects_card_names_for_the_search_index()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 1, "grpId": 1, "name": 1000,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 1, "affectedIds": [ 1 ],
            "type": [ "AnnotationType_ZoneTransfer" ], "details": [
              { "key": "category", "valueString": [ "CastSpell" ] } ] } ] }
        """));
        Assert.That(t.CardsSeen, Does.Contain("Lightning Bolt"));
    }

    [Test]
    public void Extract_never_throws_on_malformed_annotations()
    {
        Assert.DoesNotThrow(() => Run(RoomLine, Gre("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 1 },
          { "id": 2, "type": "not-an-array" },
          { "id": 3, "type": [ "AnnotationType_ZoneTransfer" ] } ] }
        """)));
    }
}
