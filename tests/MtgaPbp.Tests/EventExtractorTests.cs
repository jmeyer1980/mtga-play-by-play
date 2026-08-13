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
        public string? EnumName(string type, int value) => (type, value) switch
        {
            ("Phase", 3) => "Combat",
            ("Step", 5) => "Declare Attackers",
            _ => null
        };
        public string? AbilityText(int abilityGrpId) => abilityGrpId switch
        {
            6 => "First strike",
            8 => "Flying",
            10 => "Hexproof",
            12 => "Lifelink",
            500 => "When this Class becomes level 2, create a token.",
            _ => null
        };
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

    /// <summary>
    /// A name that could not be resolved used to be dropped on the floor: SawCard
    /// filtered placeholders out of CardsSeen, and every consumer then looked for
    /// them *in* CardsSeen — so `stats` reported "(none)" while 79 of 111 real pages
    /// carried a raw id. Counting them is what makes the diagnostic able to fail.
    /// </summary>
    [Test]
    public void Extract_counts_a_name_it_could_not_resolve()
    {
        var t = UnseenObjectDies();
        Assert.That(t.UnresolvedNames, Does.ContainKey("Unknown card"));
        Assert.That(t.UnresolvedNames["Unknown card"], Is.GreaterThan(0));
    }

    [Test]
    public void Extract_keeps_an_unresolved_name_out_of_the_search_index()
    {
        // Searching for "unknown" should not match every match that ever had a card
        // the client could not see.
        Assert.That(UnseenObjectDies().CardsSeen, Is.Empty);
    }

    private static Transcript UnseenObjectDies() =>
        Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "gameObjects": [],
          "annotations": [ { "id": 1, "affectedIds": [ 348 ],
            "type": [ "AnnotationType_ZoneTransfer" ], "details": [
              { "key": "category", "valueString": [ "SBA_Damage" ] } ] } ] }
        """));

    [Test]
    public void Extract_emits_an_attack_when_a_creature_is_declared()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "turnInfo": { "turnNumber": 5, "activePlayer": 2 },
          "gameObjects": [ { "instanceId": 377, "grpId": 9, "name": 1001,
            "controllerSeatId": 2, "attackState": "AttackState_Declared",
            "attackInfo": { "targetId": 1 } } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.Attack);
        Assert.That(e.SourceName, Is.EqualTo("Llanowar Elves"));
        Assert.That(e.ActorSeat, Is.EqualTo(2));
        Assert.That(e.TargetSeat, Is.EqualTo(1));
    }

    [Test]
    public void Extract_emits_a_block_naming_the_attacker()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "turnInfo": { "turnNumber": 5, "activePlayer": 1 },
          "gameObjects": [
            { "instanceId": 388, "grpId": 8, "name": 1000, "controllerSeatId": 1 },
            { "instanceId": 448, "grpId": 9, "name": 1001, "controllerSeatId": 2,
              "blockState": "BlockState_Declared",
              "blockInfo": { "attackerIds": [ 388 ] } } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.Block);
        Assert.That(e.SourceName, Is.EqualTo("Llanowar Elves"));
        Assert.That(e.TargetName, Is.EqualTo("Lightning Bolt"));
    }

    [Test]
    public void Extract_attributes_a_hidden_draw_to_the_active_player()
    {
        // The opponent's drawn card has no gameObject, so the controller is unknown;
        // the drawer is whoever's turn it is.
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "turnInfo": { "turnNumber": 3, "activePlayer": 2 },
          "annotations": [ { "id": 1, "affectedIds": [ 9999 ],
            "type": [ "AnnotationType_ZoneTransfer" ], "details": [
              { "key": "category", "valueString": [ "Draw" ] } ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.Drew);
        Assert.That(e.ActorSeat, Is.EqualTo(2));
    }

    [Test]
    public void Extract_numbers_the_first_turn_as_one_not_zero()
    {
        // NewTurnStarted for turn 1 arrives before turnInfo carries a turnNumber.
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "turnInfo": { "activePlayer": 1 },
          "annotations": [ { "id": 1, "affectorId": 1, "affectedIds": [ 1 ],
            "type": [ "AnnotationType_NewTurnStarted" ] } ] }
        """));

        Assert.That(t.Events.Single(x => x.Kind == EventKind.TurnStart).Turn, Is.EqualTo(1));
    }

    [Test]
    public void Phase_changes_are_named_from_the_annotations_own_details()
    {
        // turnInfo usually omits phase and step, so reading tracker state here
        // produced "phase 0, step 0" for every line in the verbose stream.
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "turnInfo": { "turnNumber": 5 },
          "annotations": [ { "id": 107, "affectedIds": [ 2 ],
            "type": [ "AnnotationType_PhaseOrStepModified" ], "details": [
              { "key": "phase", "valueInt32": [ 3 ] },
              { "key": "step",  "valueInt32": [ 5 ] } ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.PhaseChange);
        Assert.That(e.Phase, Is.EqualTo(3));
        Assert.That(e.Step, Is.EqualTo(5));
        Assert.That(e.Detail, Is.EqualTo("Combat · Declare Attackers"));
    }

    [Test]
    public void Nameless_phase_changes_are_dropped_entirely()
    {
        // Phase 0 and Step 0 both have blank labels in Arena's own enum table.
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 107, "type": [ "AnnotationType_PhaseOrStepModified" ], "details": [
              { "key": "phase", "valueInt32": [ 0 ] },
              { "key": "step",  "valueInt32": [ 0 ] } ] } ] }
        """));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.PhaseChange), Is.False);
        Assert.That(t.UnknownAnnotations, Is.Empty, "dropping it is not the same as not knowing it");
    }

    [Test]
    public void Extract_snapshots_the_board_at_each_turn_boundary()
    {
        const string zones = """
            "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" } ],
            "gameObjects": [ { "instanceId": 50, "grpId": 1, "name": 1001,
              "controllerSeatId": 2, "zoneId": 28, "cardTypes": [ "CardType_Creature" ],
              "power": 2, "toughness": 2 } ],
            """;
        string NewTurn(int n, int seat) => Gre($$"""
            { "type": "GameStateType_Full",
              {{zones}}
              "players": [ { "systemSeatNumber": 1, "lifeTotal": 20 },
                           { "systemSeatNumber": 2, "lifeTotal": 17 } ],
              "turnInfo": { "turnNumber": {{n}}, "activePlayer": {{seat}} },
              "annotations": [
                { "id": 0, "type": [ "AnnotationType_PhaseOrStepModified" ], "details": [
                    { "key": "phase", "valueInt32": [ 3 ] },
                    { "key": "step",  "valueInt32": [ 5 ] } ] },
                { "id": 1, "affectorId": {{seat}}, "affectedIds": [ {{seat}} ],
                  "type": [ "AnnotationType_NewTurnStarted" ] } ] }
            """);

        var t = Run(RoomLine, MulliganLine, NewTurn(1, 1), NewTurn(2, 2));

        var board = t.Events.SingleOrDefault(x => x.Kind == EventKind.BoardSnapshot);
        Assert.That(board, Is.Not.Null, "one snapshot for the turn that just ended");
        Assert.That(board!.ActorSeat, Is.EqualTo(2));
        Assert.That(board.Detail, Is.EqualTo("Llanowar Elves 2/2"));
        Assert.That(board.Turn, Is.EqualTo(1), "it describes the turn that ended, not the new one");
    }

    [Test]
    public void Board_snapshots_are_skipped_while_the_board_is_unchanged()
    {
        string Turn(int n, int seat, string toughness) => Gre($$"""
            { "type": "GameStateType_Full",
              "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" } ],
              "gameObjects": [ { "instanceId": 50, "grpId": 1, "name": 1001,
                "controllerSeatId": 2, "zoneId": 28, "cardTypes": [ "CardType_Creature" ],
                "power": 2, "toughness": {{toughness}} } ],
              "turnInfo": { "turnNumber": {{n}}, "activePlayer": {{seat}} },
              "annotations": [ { "id": 1, "affectorId": {{seat}}, "affectedIds": [ {{seat}} ],
                "type": [ "AnnotationType_NewTurnStarted" ] } ] }
            """);

        var t = Run(RoomLine, MulliganLine,
            Turn(1, 1, "2"),   // board appears
            Turn(2, 2, "2"),   // unchanged
            Turn(3, 1, "2"),   // still unchanged
            Turn(4, 2, "5"));  // grew

        var boards = t.Events.Where(x => x.Kind == EventKind.BoardSnapshot).ToList();
        Assert.That(boards.Select(b => b.Detail), Is.EqualTo(new[]
        {
            "Llanowar Elves 2/2",
            "Llanowar Elves 2/5"
        }), "an unchanged board should stay quiet until it actually moves");
    }

    [Test]
    public void Turn_start_carries_the_life_totals_entering_the_turn()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "players": [ { "systemSeatNumber": 1, "lifeTotal": 18 },
                       { "systemSeatNumber": 2, "lifeTotal": 13 } ],
          "turnInfo": { "turnNumber": 6, "activePlayer": 1 },
          "annotations": [ { "id": 1, "affectorId": 1, "affectedIds": [ 1 ],
            "type": [ "AnnotationType_NewTurnStarted" ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.TurnStart);
        Assert.That(e.LifeSeat1, Is.EqualTo(18));
        Assert.That(e.LifeSeat2, Is.EqualTo(13));
    }

    [Test]
    public void Zone_transfers_record_what_caused_them()
    {
        // Every Destroy, Exile, Return, Mill and Countered in the sample archive
        // carries affectorId. This is the effect's source, not a declared target.
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [
            { "instanceId": 700, "grpId": 8, "name": 1000, "type": "GameObjectType_Card" },
            { "instanceId": 800, "grpId": 9, "name": 1001, "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 1, "affectorId": 700, "affectedIds": [ 800 ],
            "type": [ "AnnotationType_ZoneTransfer" ], "details": [
              { "key": "category", "valueString": [ "Destroy" ] } ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.Destroyed);
        Assert.That(e.SourceName, Is.EqualTo("Llanowar Elves"), "the card that moved");
        Assert.That(e.CauseName, Is.EqualTo("Lightning Bolt"), "what moved it");
        Assert.That(t.CardsSeen, Does.Contain("Lightning Bolt"));
    }

    [Test]
    public void A_player_seat_is_not_recorded_as_a_cause()
    {
        // affectorId 1 and 2 are players, not objects; "You discards X" is nonsense.
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 800, "grpId": 9, "name": 1001,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 1, "affectorId": 2, "affectedIds": [ 800 ],
            "type": [ "AnnotationType_ZoneTransfer" ], "details": [
              { "key": "category", "valueString": [ "Discard" ] } ] } ] }
        """));

        Assert.That(t.Events.Single(x => x.Kind == EventKind.Discarded).CauseName, Is.Null);
    }

    [Test]
    public void Mill_and_surveil_are_recognised_rather_than_generic_zone_moves()
    {
        foreach (var (category, expected) in new[]
        {
            ("Mill", EventKind.Milled),
            ("Surveil", EventKind.Surveilled),
        })
        {
            var t = Run(RoomLine, MulliganLine, Gre($$"""
            { "type": "GameStateType_Full",
              "gameObjects": [ { "instanceId": 800, "grpId": 9, "name": 1001,
                                 "type": "GameObjectType_Card" } ],
              "annotations": [ { "id": 1, "affectedIds": [ 800 ],
                "type": [ "AnnotationType_ZoneTransfer" ], "details": [
                  { "key": "category", "valueString": [ "{{category}}" ] } ] } ] }
            """));
            Assert.That(t.Events.Select(x => x.Kind), Does.Contain(expected));
        }
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

    /// <summary>
    /// A gap is found while scanning, but the transcript is rebuilt from the archive
    /// long afterwards — so it has to survive being written to a gzip file and read
    /// back as text, or the warning would exist only on the run that discovered it.
    /// </summary>
    [Test]
    public void Extract_carries_a_gap_from_the_archive_into_the_transcript()
    {
        var line = LogGaps.ToEnvelope(
            new LogGap(LogGapKind.Summarized, 10486, 77, 3, ["GameStateMessage"])).GetRawText();

        var t = Run(RoomLine, line);

        Assert.That(t.Gaps, Has.Count.EqualTo(1));
        Assert.That(t.Gaps[0].GameObjects, Is.EqualTo(77));
        Assert.That(t.Gaps[0].Messages, Is.EqualTo(new[] { "GameStateMessage" }));
    }

    [Test]
    public void Extract_does_not_read_a_gap_as_something_that_happened()
    {
        // It records an absence. Letting it reach the tracker would turn "we do not
        // know what happened here" into a made-up event, which is the lie in miniature.
        var line = LogGaps.ToEnvelope(new LogGap(LogGapKind.Torn, 5, 0, 0, [])).GetRawText();

        var t = Run(RoomLine, line);

        Assert.That(t.Events, Is.Empty);
        Assert.That(t.Gaps.Single().Kind, Is.EqualTo(LogGapKind.Torn));
    }

    /// <summary>
    /// Equipment is the case that earns the line. Equip is an activated ability, not a
    /// cast, so nothing in the transcript ever names the creature carrying the sword —
    /// only the statline moves, with no visible cause.
    /// </summary>
    [Test]
    public void Equipment_reports_the_creature_it_went_onto()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [
            { "instanceId": 500, "grpId": 5, "name": 1000, "type": "GameObjectType_Card" },
            { "instanceId": 600, "grpId": 6, "name": 1001, "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 1, "affectorId": 500, "affectedIds": [ 600 ],
            "type": [ "AnnotationType_AttachmentCreated" ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.Attached);
        Assert.That(e.SourceName, Is.EqualTo("Lightning Bolt"), "the thing that attached");
        Assert.That(e.TargetName, Is.EqualTo("Llanowar Elves"), "what it went onto");
    }

    /// <summary>
    /// An aura is cast at its host, and the cast line already reads "You cast Ethereal
    /// Armor, targeting Rabbit (1/1 → 5/5)". Saying it again immediately underneath is
    /// the same fact twice, so a target already on file suppresses the line. This is
    /// what separates the 136 auras in the archive from the 23 equips.
    /// </summary>
    [Test]
    public void An_attachment_the_cast_line_already_names_is_not_repeated()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [
            { "instanceId": 500, "grpId": 5, "name": 1000, "type": "GameObjectType_Card" },
            { "instanceId": 600, "grpId": 6, "name": 1001, "type": "GameObjectType_Card" } ],
          "persistentAnnotations": [ { "id": 9, "affectorId": 500, "affectedIds": [ 600 ],
            "type": [ "AnnotationType_TargetSpec" ] } ],
          "annotations": [ { "id": 1, "affectorId": 500, "affectedIds": [ 600 ],
            "type": [ "AnnotationType_AttachmentCreated" ] } ] }
        """));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.Attached), Is.False);
    }

    /// <summary>
    /// An attachment whose host or attachment cannot be named would read "Unknown card
    /// is attached to Unknown card", which is a line about nothing.
    /// </summary>
    [Test]
    public void An_attachment_nobody_can_name_produces_no_line()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 500, "grpId": 5, "name": 1000,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 1, "affectorId": 500, "affectedIds": [ 4242 ],
            "type": [ "AnnotationType_AttachmentCreated" ] } ] }
        """));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.Attached), Is.False);
    }

    /// <summary>
    /// A Class's level is a standing fact Arena re-sends with every message for the rest
    /// of the game, not an event — so only the move to a new level is worth a line, and
    /// the several hundred restatements after it must stay silent.
    /// </summary>
    [Test]
    public void A_class_reports_each_level_once_however_often_it_is_restated()
    {
        string Level(int level) => Gre($$"""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 700, "grpId": 7, "name": 1001,
                             "type": "GameObjectType_Card", "controllerSeatId": 1 } ],
          "persistentAnnotations": [ { "id": 3, "affectorId": 700,
            "type": [ "AnnotationType_ClassLevel" ], "details": [
              { "key": "Level", "valueInt32": [ {{level}} ] } ] } ] }
        """);

        var t = Run(RoomLine, MulliganLine,
            Level(2), Level(2), Level(2), Level(3), Level(3));

        var levels = t.Events.Where(x => x.Kind == EventKind.LevelUp).ToList();
        Assert.That(levels.Select(x => x.Amount), Is.EqualTo(new[] { 2, 3 }));
        Assert.That(levels[0].SourceName, Is.EqualTo("Llanowar Elves"));
        Assert.That(levels[0].ActorSeat, Is.EqualTo(1));
    }

    /// <summary>
    /// Arena hands out instance ids afresh for each game of a match, so a level
    /// remembered from game one would silence the same card levelling in game two.
    /// </summary>
    [Test]
    public void A_class_levelling_again_in_the_next_game_is_reported_again()
    {
        string Game(int number) => Gre($$"""
        { "type": "GameStateType_Full",
          "gameInfo": { "gameNumber": {{number}} },
          "gameObjects": [ { "instanceId": 700, "grpId": 7, "name": 1001,
                             "type": "GameObjectType_Card", "controllerSeatId": 1 } ],
          "persistentAnnotations": [ { "id": 3, "affectorId": 700,
            "type": [ "AnnotationType_ClassLevel" ], "details": [
              { "key": "Level", "valueInt32": [ 2 ] } ] } ] }
        """);

        var t = Run(RoomLine, MulliganLine, Game(1), Game(1), Game(2));

        Assert.That(t.Events.Count(x => x.Kind == EventKind.LevelUp), Is.EqualTo(2));
    }

    /// <summary>
    /// A designation carries nothing but a numeric DesignationType, and that enum is in
    /// no table of Arena's card database — so there is no honest sentence to build from
    /// one. Both halves are dropped rather than left to surface as "[unhandled: …]".
    /// </summary>
    [Test]
    public void Designations_are_dropped_rather_than_reported_as_unhandled()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 800, "grpId": 9, "name": 1001,
                             "type": "GameObjectType_Card" } ],
          "annotations": [
            { "id": 1, "affectedIds": [ 800 ],
              "type": [ "AnnotationType_GainDesignation" ], "details": [
                { "key": "DesignationType", "valueInt32": [ 19 ] } ] },
            { "id": 2, "affectorId": 800, "affectedIds": [ 800 ],
              "type": [ "AnnotationType_LoseDesignation" ], "details": [
                { "key": "DesignationType", "valueInt32": [ 24 ] } ] } ] }
        """));

        Assert.That(t.UnknownAnnotations, Is.Empty);
        Assert.That(t.Events.Any(x => x.Kind == EventKind.Unknown), Is.False);
    }

    [Test]
    public void A_match_with_nothing_withheld_reports_no_gaps()
    {
        // The default has to be silence: 150 of the 152 archived matches are clean, and
        // a banner on any of them would teach the reader to ignore all of them.
        Assert.That(Run(RoomLine, MulliganLine).Gaps, Is.Empty);
    }

    /// <summary>
    /// One message carrying an ability that triggered and the object that set it off.
    /// Shaped after the archive: the TriggeringObject names the ability as its affector
    /// and the cause among its affected ids, which is the opposite way round from the
    /// AbilityInstanceCreated beside it.
    /// </summary>
    private static string TriggerMessage(int abilitySource, int cause) => Gre($$"""
    { "type": "GameStateType_Full",
      "gameObjects": [
        { "instanceId": {{abilitySource}}, "grpId": 5, "name": 1001,
          "type": "GameObjectType_Card", "controllerSeatId": 1 },
        { "instanceId": {{cause}}, "grpId": 6, "name": 1000,
          "type": "GameObjectType_Card", "controllerSeatId": 1 },
        { "instanceId": 900, "grpId": 7, "name": 1001,
          "type": "GameObjectType_Ability", "controllerSeatId": 1 } ],
      "persistentAnnotations": [
        { "id": 40, "affectorId": 900, "affectedIds": [ {{cause}} ],
          "type": [ "AnnotationType_TriggeringObject" ] } ],
      "annotations": [
        { "id": 41, "affectorId": {{abilitySource}}, "affectedIds": [ 900 ],
          "type": [ "AnnotationType_AbilityInstanceCreated" ] } ] }
    """);

    /// <summary>
    /// The direction was established from the archive: the affector of a
    /// TriggeringObject is a GameObjectType_Ability in 2,389 of 2,394 cases and an id
    /// AbilityInstanceCreated had already announced in all 2,394, while the affector
    /// never changed zones in the same message and the affected object did so 528 times
    /// out of 890. Reading it backwards would put the wrong card at the front of the
    /// sentence, so the direction gets a test of its own.
    /// </summary>
    [Test]
    public void A_trigger_names_the_object_that_set_it_off()
    {
        // Ability source 800, cause 801 — two different permanents.
        var t = Run(RoomLine, MulliganLine, TriggerMessage(abilitySource: 800, cause: 801));

        var e = t.Events.Single(x => x.Kind == EventKind.Triggered);
        Assert.That(e.SourceName, Is.EqualTo("Llanowar Elves"), "the ability's own source");
        Assert.That(e.CauseName, Is.EqualTo("Lightning Bolt"), "what set it off");
        Assert.That(e.CauseInstanceId, Is.EqualTo(801));
    }

    /// <summary>
    /// 996 of the archive's 2,394 triggering objects name the ability's own permanent —
    /// a creature's enters-the-battlefield trigger setting itself off. "Llanowar Elves
    /// triggers Llanowar Elves's ability" reads as though a second permanent were
    /// involved, so the cause is dropped and the plain line stands.
    /// </summary>
    [Test]
    public void A_trigger_that_set_itself_off_says_nothing_about_a_cause()
    {
        var t = Run(RoomLine, MulliganLine, TriggerMessage(abilitySource: 800, cause: 800));

        var e = t.Events.Single(x => x.Kind == EventKind.Triggered);
        Assert.That(e.CauseName, Is.Null);
        Assert.That(e.CauseInstanceId, Is.Null);
    }

    /// <summary>
    /// One message carrying an ability being created and the player's own act of
    /// activating it. Shaped after the archive: UserActionTaken's affector is the seat —
    /// on all 450 activations, unlike most affectors — and its affected id is the
    /// ability instance the AbilityInstanceCreated beside it announces. The ability
    /// object names its permanent through parentId alone, the way a real one does.
    /// </summary>
    private static string ActivationObjects => """
        { "instanceId": 800, "grpId": 5, "name": 1001,
          "type": "GameObjectType_Card", "controllerSeatId": 2 },
        { "instanceId": 900, "grpId": 7, "parentId": 800,
          "type": "GameObjectType_Ability", "controllerSeatId": 2 }
    """;

    private static string CreationMessage => Gre($$"""
    { "type": "GameStateType_Full",
      "gameObjects": [ {{ActivationObjects}} ],
      "annotations": [
        { "id": 41, "affectorId": 800, "affectedIds": [ 900 ],
          "type": [ "AnnotationType_AbilityInstanceCreated" ] } ] }
    """);

    private static string ActivationMessage(int abilityInstance, int actionType = 2) => Gre($$"""
    { "type": "GameStateType_Full",
      "annotations": [
        { "id": 42, "affectorId": 2, "affectedIds": [ {{abilityInstance}} ],
          "type": [ "AnnotationType_UserActionTaken" ], "details": [
            { "key": "actionType", "valueInt32": [ {{actionType}} ] },
            { "key": "abilityGrpId", "valueInt32": [ 7 ] } ] } ] }
    """);

    /// <summary>
    /// Issue #3: a deliberate play was reported as "X's ability triggers" — the wrong
    /// verb, hiding both the decision and the cost paid. The trigger line is replaced,
    /// not accompanied: 318 of these on the archive's pages would otherwise be said
    /// twice. The line names the permanent and the player, and drops any triggering
    /// object Arena also sent — the activation is the player's own act.
    /// </summary>
    [Test]
    public void An_activated_ability_is_an_activation_not_a_trigger()
    {
        var t = Run(RoomLine, MulliganLine, Gre($$"""
        { "type": "GameStateType_Full",
          "gameObjects": [ {{ActivationObjects}},
            { "instanceId": 801, "grpId": 6, "name": 1000,
              "type": "GameObjectType_Card", "controllerSeatId": 2 } ],
          "persistentAnnotations": [
            { "id": 40, "affectorId": 900, "affectedIds": [ 801 ],
              "type": [ "AnnotationType_TriggeringObject" ] } ],
          "annotations": [
            { "id": 41, "affectorId": 800, "affectedIds": [ 900 ],
              "type": [ "AnnotationType_AbilityInstanceCreated" ] },
            { "id": 42, "affectorId": 2, "affectedIds": [ 900 ],
              "type": [ "AnnotationType_UserActionTaken" ], "details": [
                { "key": "actionType", "valueInt32": [ 2 ] },
                { "key": "abilityGrpId", "valueInt32": [ 7 ] } ] } ] }
        """));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.Triggered), Is.False,
            "the trigger line must be replaced, not doubled");
        var e = t.Events.Single(x => x.Kind == EventKind.Activated);
        Assert.That(e.SourceName, Is.EqualTo("Llanowar Elves"), "the permanent, not its ability");
        Assert.That(e.SourceInstanceId, Is.EqualTo(800));
        Assert.That(e.ActorSeat, Is.EqualTo(2), "the seat UserActionTaken names");
        Assert.That(e.CauseName, Is.Null, "an activation is nobody's trigger");
    }

    /// <summary>
    /// The activation and the ability's creation arrive in different messages for 102
    /// of the archive's 450 activations, in either order — the two annotations share
    /// only the ability's instance id. Correlating per message would leave every one of
    /// those said with the wrong verb.
    /// </summary>
    [Test]
    public void An_activation_finds_its_ability_across_messages()
    {
        var t = Run(RoomLine, MulliganLine, CreationMessage, ActivationMessage(900));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.Triggered), Is.False);
        var e = t.Events.Single(x => x.Kind == EventKind.Activated);
        Assert.That(e.SourceName, Is.EqualTo("Llanowar Elves"));
        Assert.That(e.ActorSeat, Is.EqualTo(2));
    }

    /// <summary>
    /// A Class levelling up is an activation too, but "Caretaker's Talent becomes
    /// level 2" — emitted a message later — is the same fact in Arena's own words.
    /// 126 of the archive's 130 level lines sat directly under a wrong-verb trigger
    /// line saying it a second time; the activation's line goes away entirely rather
    /// than staying to say it with a better verb.
    /// </summary>
    [Test]
    public void A_class_level_up_keeps_only_its_level_line()
    {
        var creation = Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [
            { "instanceId": 700, "grpId": 7, "name": 1001,
              "type": "GameObjectType_Card", "controllerSeatId": 1 },
            { "instanceId": 900, "grpId": 8, "parentId": 700,
              "type": "GameObjectType_Ability", "controllerSeatId": 1 } ],
          "annotations": [
            { "id": 41, "affectorId": 700, "affectedIds": [ 900 ],
              "type": [ "AnnotationType_AbilityInstanceCreated" ] },
            { "id": 42, "affectorId": 1, "affectedIds": [ 900 ],
              "type": [ "AnnotationType_UserActionTaken" ], "details": [
                { "key": "actionType", "valueInt32": [ 2 ] },
                { "key": "abilityGrpId", "valueInt32": [ 8 ] } ] } ] }
        """);
        var level = Gre("""
        { "type": "GameStateType_Full",
          "persistentAnnotations": [ { "id": 3, "affectorId": 700,
            "type": [ "AnnotationType_ClassLevel" ], "details": [
              { "key": "Level", "valueInt32": [ 2 ] } ] } ] }
        """);

        var t = Run(RoomLine, MulliganLine, creation, level);

        Assert.That(t.Events.Single(x => x.Kind == EventKind.LevelUp).Amount, Is.EqualTo(2));
        Assert.That(t.Events.Any(x => x.Kind == EventKind.Triggered), Is.False,
            "the wrong-verb line must not survive");
        Assert.That(t.Events.Any(x => x.Kind == EventKind.Activated && x.SourceName is not null),
            Is.False, "and no activation line may replace it");
    }

    /// <summary>
    /// Classes are not legendary, so two copies of the same Class can be in play at
    /// once. When one copy levels up, only its own activation may be claimed: the
    /// permanents share a printed name, and matching by name while the instance ids
    /// disagree would let one copy's level line swallow the other copy's genuine
    /// activation — reproducing the very doubling the claim exists to remove.
    /// </summary>
    [Test]
    public void A_level_up_cannot_claim_a_same_named_siblings_activation()
    {
        // Copy A (700) levels with no announced activation of its own; copy B (750)
        // genuinely activates through ability 900. Same printed name, different
        // permanents.
        var activation = Gre("""
        { "type": "GameStateType_Full",
          "gameObjects": [
            { "instanceId": 700, "grpId": 7, "name": 1001,
              "type": "GameObjectType_Card", "controllerSeatId": 1 },
            { "instanceId": 750, "grpId": 7, "name": 1001,
              "type": "GameObjectType_Card", "controllerSeatId": 1 },
            { "instanceId": 900, "grpId": 8, "parentId": 750,
              "type": "GameObjectType_Ability", "controllerSeatId": 1 } ],
          "annotations": [
            { "id": 41, "affectorId": 750, "affectedIds": [ 900 ],
              "type": [ "AnnotationType_AbilityInstanceCreated" ] },
            { "id": 42, "affectorId": 1, "affectedIds": [ 900 ],
              "type": [ "AnnotationType_UserActionTaken" ], "details": [
                { "key": "actionType", "valueInt32": [ 2 ] },
                { "key": "abilityGrpId", "valueInt32": [ 8 ] } ] } ] }
        """);
        var level = Gre("""
        { "type": "GameStateType_Full",
          "persistentAnnotations": [ { "id": 3, "affectorId": 700,
            "type": [ "AnnotationType_ClassLevel" ], "details": [
              { "key": "Level", "valueInt32": [ 2 ] } ] } ] }
        """);

        var t = Run(RoomLine, MulliganLine, activation, level);

        Assert.That(t.Events.Single(x => x.Kind == EventKind.LevelUp).SourceInstanceId,
            Is.EqualTo(700));
        var activated = t.Events.Single(x => x.Kind == EventKind.Activated);
        Assert.That(activated.SourceName, Is.Not.Null,
            "copy B's activation must survive copy A's level-up");
        Assert.That(activated.SourceInstanceId, Is.EqualTo(750));
    }

    /// <summary>
    /// Only actionType 2 is an activation — 1 is a cast, 3 a land drop, 4 a mana
    /// ability. A mana ability's UserActionTaken naming the same instance must not
    /// turn a genuine trigger into a claim the player activated it.
    /// </summary>
    [Test]
    public void A_user_action_that_is_not_an_activation_leaves_the_trigger_alone()
    {
        var t = Run(RoomLine, MulliganLine, CreationMessage,
            ActivationMessage(900, actionType: 4));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.Activated), Is.False);
        Assert.That(t.Events.Single(x => x.Kind == EventKind.Triggered).SourceName,
            Is.EqualTo("Llanowar Elves's ability"));
    }

    /// <summary>
    /// Arena renames instances mid-game, and an activation can arrive under a later id
    /// than the creation it belongs to. Both sides fold through the alias map — which
    /// is only complete when the game closes, and is why the match is made then.
    /// </summary>
    [Test]
    public void An_activation_under_a_renamed_id_still_finds_its_ability()
    {
        var rename = Gre("""
        { "type": "GameStateType_Full",
          "annotations": [
            { "id": 43, "affectorId": 900, "affectedIds": [ 910 ],
              "type": [ "AnnotationType_ObjectIdChanged" ], "details": [
                { "key": "orig_id", "valueInt32": [ 900 ] },
                { "key": "new_id",  "valueInt32": [ 910 ] } ] } ] }
        """);
        var t = Run(RoomLine, MulliganLine, CreationMessage, rename, ActivationMessage(910));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.Triggered), Is.False);
        Assert.That(t.Events.Single(x => x.Kind == EventKind.Activated).SourceName,
            Is.EqualTo("Llanowar Elves"));
    }

    /// <summary>
    /// The blind spot this closes: persistentAnnotations is a second annotation array,
    /// and because nothing counted it, TargetSpec and ClassLevel both sat unread in it
    /// while `stats` reported a clean bill.
    /// </summary>
    [Test]
    public void Persistent_annotations_nobody_reads_are_counted_not_silently_dropped()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "persistentAnnotations": [
          { "id": 1, "affectorId": 300, "affectedIds": [ 301 ],
            "type": [ "AnnotationType_SomethingPersistentAndNew" ] } ] }
        """));

        Assert.That(t.UnknownPersistentAnnotations["AnnotationType_SomethingPersistentAndNew"],
            Is.EqualTo(1));

        // Diagnostic only. The streamed side emits an Unknown event that narrates as
        // "[unhandled: …]"; doing that here would put hundreds of lines of engine
        // bookkeeping into every transcript.
        Assert.That(t.Events.Any(x => x.Kind == EventKind.Unknown), Is.False);
        Assert.That(t.UnknownAnnotations, Is.Empty, "the two surfaces are counted apart");
    }

    /// <summary>
    /// Types something reads, and types examined and deliberately dropped, both have to
    /// stay out of the inventory — otherwise the list a future session works from is
    /// mostly settled questions and the unmined ones are lost in it.
    /// </summary>
    [Test]
    public void Handled_and_ignored_persistent_types_stay_out_of_the_inventory()
    {
        var t = Run(RoomLine, MulliganLine, Gre("""
        { "type": "GameStateType_Full", "persistentAnnotations": [
          { "id": 1, "affectorId": 300, "affectedIds": [ 301 ],
            "type": [ "AnnotationType_TargetSpec" ] },
          { "id": 2, "affectorId": 300, "affectedIds": [ 301 ],
            "type": [ "AnnotationType_EnteredZoneThisTurn" ] } ] }
        """));

        Assert.That(t.UnknownPersistentAnnotations, Is.Empty);
    }

    /// <summary>
    /// Arena re-sends the whole persistent set with almost every message — 10,481
    /// arrivals across the archive describe 10,339 distinct EnteredZoneThisTurn facts,
    /// and per match the ratio is roughly ten to one. Counting arrivals would report how
    /// chatty the log is rather than how much of it is unmined.
    /// </summary>
    [Test]
    public void A_persistent_annotation_re_sent_every_message_is_counted_once()
    {
        const string body = """
        { "type": "GameStateType_Full", "persistentAnnotations": [
          { "id": 7, "affectorId": 300, "affectedIds": [ 301 ],
            "type": [ "AnnotationType_SomethingPersistentAndNew" ] } ] }
        """;
        var t = Run(RoomLine, MulliganLine, Gre(body), Gre(body), Gre(body));

        Assert.That(t.UnknownPersistentAnnotations["AnnotationType_SomethingPersistentAndNew"],
            Is.EqualTo(1));
    }

    /// <summary>
    /// The id alone is not a fact. Arena hands the same id back describing different
    /// objects once what it stands for changes — the "entered this turn" set for a zone
    /// keeps its id and swaps its members every turn — so the objects belong in the key.
    /// </summary>
    [Test]
    public void The_same_persistent_id_describing_different_objects_is_counted_twice()
    {
        string Body(int affected) => Gre($$"""
        { "type": "GameStateType_Full", "persistentAnnotations": [
          { "id": 7, "affectorId": 300, "affectedIds": [ {{affected}} ],
            "type": [ "AnnotationType_SomethingPersistentAndNew" ] } ] }
        """);
        var t = Run(RoomLine, MulliganLine, Body(301), Body(302));

        Assert.That(t.UnknownPersistentAnnotations["AnnotationType_SomethingPersistentAndNew"],
            Is.EqualTo(2));
    }

    /// <summary>
    /// Persistent annotation ids are handed out afresh per game: 96 of the 121 distinct
    /// ids in the archive's one Bo3 appear in both of its games. A set carried across the
    /// boundary would report game two's persistent surface as already seen.
    /// </summary>
    [Test]
    public void A_second_game_reusing_a_persistent_id_is_counted_again()
    {
        string Body(int game) => Gre($$"""
        { "type": "GameStateType_Full", "gameInfo": { "gameNumber": {{game}} },
          "persistentAnnotations": [
            { "id": 7, "affectorId": 300, "affectedIds": [ 301 ],
              "type": [ "AnnotationType_SomethingPersistentAndNew" ] } ] }
        """);
        var t = Run(RoomLine, MulliganLine, Body(1), Body(2));

        Assert.That(t.UnknownPersistentAnnotations["AnnotationType_SomethingPersistentAndNew"],
            Is.EqualTo(2));
    }

    /// <summary>
    /// The creature and the spell that will grant to it. 800 is Llanowar Elves; 801 is
    /// the granter, named Lightning Bolt because the fixture db knows that name — the
    /// shape matches issue #5's live case, where 431 was Enter the Avatar State.
    /// </summary>
    private static string GrantObjects => """
        { "instanceId": 800, "grpId": 5, "name": 1001,
          "type": "GameObjectType_Card", "controllerSeatId": 1 },
        { "instanceId": 801, "grpId": 6, "name": 1000,
          "type": "GameObjectType_Card", "controllerSeatId": 2 },
        { "instanceId": 805, "grpId": 9, "name": 648,
          "type": "GameObjectType_Card", "controllerSeatId": 1 }
    """;

    private static string GrantMessage(string grpids, string affected = "[ 800 ]", int annId = 90) =>
        Gre($$"""
        { "type": "GameStateType_Full",
          "gameObjects": [ {{GrantObjects}} ],
          "persistentAnnotations": [
            { "id": {{annId}}, "affectorId": 801, "affectedIds": {{affected}},
              "type": [ "AnnotationType_AddAbility", "AnnotationType_LayeredEffect" ],
              "details": [ { "key": "grpid", "valueInt32": {{grpids}} } ] } ] }
        """);

    /// <summary>
    /// Issue #5: a spell resolves, a creature fights differently, and nothing on the
    /// page says what changed. The grant arrives only as a persistent AddAbility whose
    /// details name the ability's grpid, so the line has to come from there — target,
    /// granter and the ability in words.
    /// </summary>
    [Test]
    public void A_granted_ability_is_named_with_its_granter()
    {
        var t = Run(RoomLine, MulliganLine, GrantMessage("[ 6 ]"));

        var e = t.Events.Single(x => x.Kind == EventKind.AbilityGained);
        Assert.That(e.TargetName, Is.EqualTo("Llanowar Elves"));
        Assert.That(e.TargetInstanceId, Is.EqualTo(800));
        Assert.That(e.CauseName, Is.EqualTo("Lightning Bolt"));
        Assert.That(e.Detail, Is.EqualTo("first strike"), "lowercased to sit mid-sentence");
    }

    /// <summary>
    /// Enter the Avatar State grants four keywords in one annotation — grpid is a
    /// parallel array. Four lines saying "gains" four times is the same fact told
    /// worse, so the grants of one granter to one creature are one line.
    /// </summary>
    [Test]
    public void Several_abilities_granted_at_once_make_one_line()
    {
        var t = Run(RoomLine, MulliganLine, GrantMessage("[ 8, 6, 12, 10 ]"));

        var e = t.Events.Single(x => x.Kind == EventKind.AbilityGained);
        Assert.That(e.Detail, Is.EqualTo("flying, first strike, lifelink and hexproof"));
    }

    /// <summary>
    /// The annotation is persistent: Arena re-sends it with every message for as long
    /// as the grant stands. Only its first appearance is news. But a new member joining
    /// the same annotation's affectedIds IS news — that creature just gained the
    /// ability — and must be the only thing the second message adds.
    /// </summary>
    [Test]
    public void A_standing_grant_is_said_once_and_a_new_member_once_more()
    {
        var t = Run(RoomLine, MulliganLine,
            GrantMessage("[ 12 ]"),
            GrantMessage("[ 12 ]"),                              // verbatim re-send
            GrantMessage("[ 12 ]", affected: "[ 800, 805 ]"));   // 805 joins the grant

        var gains = t.Events.Where(x => x.Kind == EventKind.AbilityGained).ToList();
        Assert.That(gains, Has.Count.EqualTo(2), "one per creature, never per re-send");
        Assert.That(gains[0].TargetInstanceId, Is.EqualTo(800));
        Assert.That(gains[1].TargetInstanceId, Is.EqualTo(805));
    }

    /// <summary>
    /// A whole-rule grant is a quotation, not a keyword: it keeps its capitals and
    /// gains quotes, because lowercasing "When this Class becomes level 2, …" would
    /// present a sentence as a name.
    /// </summary>
    [Test]
    public void A_granted_rule_is_quoted_not_lowercased()
    {
        var t = Run(RoomLine, MulliganLine, GrantMessage("[ 500 ]"));

        var e = t.Events.Single(x => x.Kind == EventKind.AbilityGained);
        Assert.That(e.Detail,
            Is.EqualTo("“When this Class becomes level 2, create a token.”"));
    }

    /// <summary>
    /// One grant in the archive (grpid 1000001) indexes no Abilities row at all. The
    /// same bargain AbilityInstanceCreated strikes applies: a line is only worth
    /// emitting when there are words to put on it, and "gains something" is not words.
    /// </summary>
    [Test]
    public void A_grant_the_database_cannot_name_is_dropped()
    {
        var t = Run(RoomLine, MulliganLine, GrantMessage("[ 999 ]"));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.AbilityGained), Is.False);
    }

    /// <summary>
    /// An ability riding on a counter — indestructible from Season of the Burrow — is
    /// dual-typed AddAbility and Counter, and the streamed CounterAdded already put
    /// "gets 1 Indestructible counter" on the page. The counter line is the better of
    /// the two: it names the kind, and it is what the reader watches leave later.
    /// </summary>
    [Test]
    public void A_counter_backed_grant_keeps_only_its_counter_line()
    {
        var t = Run(RoomLine, MulliganLine, Gre($$"""
        { "type": "GameStateType_Full",
          "gameObjects": [ {{GrantObjects}} ],
          "persistentAnnotations": [
            { "id": 90, "affectorId": 4002, "affectedIds": [ 800 ],
              "type": [ "AnnotationType_AddAbility", "AnnotationType_Counter" ],
              "details": [ { "key": "grpid", "valueInt32": [ 6 ] },
                           { "key": "count", "valueInt32": [ 1 ] } ] } ] }
        """));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.AbilityGained), Is.False);
    }

    /// <summary>
    /// A Class levelling up grants itself its new level's ability in the same message
    /// that moves the level — affector, affected and the levelling class are all one
    /// instance. "Caretaker's Talent becomes level 2" already says it in Arena's own
    /// words; the quoted grant under it is the machinery restating the fact.
    /// </summary>
    [Test]
    public void A_class_grant_in_its_level_message_is_claimed_by_the_level_line()
    {
        var t = Run(RoomLine, MulliganLine, Gre($$"""
        { "type": "GameStateType_Full",
          "gameObjects": [ {{GrantObjects}} ],
          "persistentAnnotations": [
            { "id": 91, "affectorId": 800,
              "type": [ "AnnotationType_ClassLevel" ], "details": [
                { "key": "Level", "valueInt32": [ 2 ] } ] },
            { "id": 92, "affectorId": 800, "affectedIds": [ 800 ],
              "type": [ "AnnotationType_AddAbility", "AnnotationType_LayeredEffect" ],
              "details": [ { "key": "grpid", "valueInt32": [ 500 ] } ] } ] }
        """));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.LevelUp), Is.True);
        Assert.That(t.Events.Any(x => x.Kind == EventKind.AbilityGained), Is.False,
            "the level line owns the fact");
    }

    /// <summary>
    /// A conditional ability switching itself on — menace as long as some condition
    /// holds — grants with the creature as its own granter. "X gives X menace" names
    /// one permanent as though it were two, so a self-grant keeps no cause.
    /// </summary>
    [Test]
    public void A_self_grant_reads_as_gaining_not_giving()
    {
        var t = Run(RoomLine, MulliganLine, Gre($$"""
        { "type": "GameStateType_Full",
          "gameObjects": [ {{GrantObjects}} ],
          "persistentAnnotations": [
            { "id": 93, "affectorId": 800, "affectedIds": [ 800 ],
              "type": [ "AnnotationType_AddAbility", "AnnotationType_LayeredEffect" ],
              "details": [ { "key": "grpid", "valueInt32": [ 6 ] } ] } ] }
        """));

        var e = t.Events.Single(x => x.Kind == EventKind.AbilityGained);
        Assert.That(e.TargetName, Is.EqualTo("Llanowar Elves"));
        Assert.That(e.CauseName, Is.Null);
        Assert.That(e.CauseInstanceId, Is.Null);
    }

    /// <summary>
    /// A grant as Arena states one in full: the creature on the battlefield with the
    /// granted grpid in its own <c>uniqueAbilities</c>, beside the AddAbility
    /// annotation. Wear-off tests need the object's description because that is the
    /// surface a wear-off is read from — the annotation is sampled in and out of
    /// messages while the ability stands, so its absence proves nothing.
    /// </summary>
    private static string BattlefieldGrant(string grpids, string uniq, int annId = 90) =>
        Gre($$"""
        { "type": "GameStateType_Full",
          "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" } ],
          "gameObjects": [
            { "instanceId": 800, "grpId": 5, "name": 1001, "zoneId": 28,
              "type": "GameObjectType_Card", "controllerSeatId": 1,
              "uniqueAbilities": [ {{uniq}} ] } ],
          "persistentAnnotations": [
            { "id": {{annId}}, "affectorId": 801, "affectedIds": [ 800 ],
              "type": [ "AnnotationType_AddAbility", "AnnotationType_LayeredEffect" ],
              "details": [ { "key": "grpid", "valueInt32": {{grpids}} } ] } ] }
        """);

    /// <summary>
    /// The creature described again, with no grant annotation in sight. An empty
    /// <c>uniq</c> omits <c>uniqueAbilities</c> entirely, because that is how the
    /// wear-off actually arrives for a creature with no other abilities: a complete
    /// snapshot in which the omitted list is the protobuf default, not a patch.
    /// </summary>
    private static string Resend(int zone = 28, string uniq = "") => Gre($$"""
        { "type": "GameStateType_Full",
          "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" },
                     { "zoneId": 29, "type": "ZoneType_Graveyard" } ],
          "gameObjects": [
            { "instanceId": 800, "grpId": 5, "name": 1001, "zoneId": {{zone}},
              "type": "GameObjectType_Card", "controllerSeatId": 1
              {{(uniq.Length == 0 ? "" : $", \"uniqueAbilities\": [ {uniq} ]")}} } ] }
        """);

    /// <summary>
    /// Issue #7: the page says a creature gained menace and never says the menace
    /// left. The wear-off is the granted grpid leaving the object's own description
    /// while the creature stands on the battlefield.
    /// </summary>
    [Test]
    public void A_grant_leaving_the_objects_description_reads_as_a_wear_off()
    {
        var t = Run(RoomLine, MulliganLine,
            BattlefieldGrant("[ 6 ]", uniq: """{ "id": 55, "grpId": 6 }"""),
            Resend());

        var e = t.Events.Single(x => x.Kind == EventKind.AbilityExpired);
        Assert.That(e.TargetName, Is.EqualTo("Llanowar Elves"));
        Assert.That(e.TargetInstanceId, Is.EqualTo(800));
        Assert.That(e.Detail, Is.EqualTo("first strike"));
        Assert.That(e.CauseName, Is.Null, "a wear-off has no actor");
    }

    /// <summary>
    /// Four keywords granted in one annotation all end together, and four lines
    /// saying "loses" four times is the same fact told worse — the exact mirror of
    /// the grant side's one-line rule.
    /// </summary>
    [Test]
    public void Several_abilities_wearing_off_at_once_make_one_line()
    {
        var t = Run(RoomLine, MulliganLine,
            BattlefieldGrant("[ 8, 6, 12, 10 ]", uniq:
                """{ "id": 55, "grpId": 8 }, { "id": 56, "grpId": 6 },""" +
                """{ "id": 57, "grpId": 12 }, { "id": 58, "grpId": 10 }"""),
            Resend());

        var e = t.Events.Single(x => x.Kind == EventKind.AbilityExpired);
        Assert.That(e.Detail, Is.EqualTo("flying, first strike, lifelink and hexproof"));
    }

    /// <summary>
    /// The trap that shaped the whole feature. Across the archive an AddAbility
    /// annotation goes missing from the persistent surface and returns under the same
    /// id 115 times — up to 86 messages later, the creature on the battlefield with
    /// the ability the whole while. The annotation's absence is sampling, not expiry;
    /// only the object's own description losing the grpid is the wear-off.
    /// </summary>
    [Test]
    public void A_sampled_out_annotation_is_not_a_wear_off()
    {
        var t = Run(RoomLine, MulliganLine,
            BattlefieldGrant("[ 6 ]", uniq: """{ "id": 55, "grpId": 6 }"""),
            Resend(uniq: """{ "id": 55, "grpId": 6 }"""),   // annotation gone, ability not
            Resend());                                       // now the ability leaves too

        Assert.That(t.Events.Count(x => x.Kind == EventKind.AbilityExpired), Is.EqualTo(1),
            "the wear-off is where the ability left the object, not where the annotation blinked");
        Assert.That(t.Events.Count(x => x.Kind == EventKind.AbilityGained), Is.EqualTo(1),
            "and the returning annotation is not a fresh grant");
    }

    /// <summary>
    /// A creature that died did not "lose trample". The ability leaves the object's
    /// description when the creature leaves play too, and the death or exile line
    /// already owns that fact — same which-line-owns-the-fact rule as the grant
    /// side's counter and level suppressions.
    /// </summary>
    [Test]
    public void A_creature_leaving_play_does_not_lose_its_grant()
    {
        var t = Run(RoomLine, MulliganLine,
            BattlefieldGrant("[ 6 ]", uniq: """{ "id": 55, "grpId": 6 }"""),
            Resend(zone: 29));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.AbilityExpired), Is.False);
    }

    /// <summary>
    /// A grant standing when the log stops is not an expiry. With no later
    /// description of the object there is no diff, so an incomplete transcript —
    /// which already says it is incomplete — manufactures nothing at end-of-log.
    /// </summary>
    [Test]
    public void A_grant_standing_at_end_of_log_is_not_a_wear_off()
    {
        var t = Run(RoomLine, MulliganLine,
            BattlefieldGrant("[ 6 ]", uniq: """{ "id": 55, "grpId": 6 }"""));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.AbilityExpired), Is.False);
    }

    /// <summary>
    /// Only a grpid the tracker saw granted can wear off. A printed ability leaving
    /// an object's description — a transform, a face-down flip — is a different fact
    /// with a different owner, and "Llanowar Elves loses flying" about an ability
    /// nothing ever granted would be the parser inventing a story.
    /// </summary>
    [Test]
    public void A_printed_ability_leaving_is_not_a_wear_off()
    {
        var t = Run(RoomLine, MulliganLine,
            // Described with flying it was never granted; no annotation anywhere.
            Resend(uniq: """{ "id": 55, "grpId": 8 }"""),
            Resend());

        Assert.That(t.Events.Any(x => x.Kind == EventKind.AbilityExpired), Is.False);
    }

    /// <summary>
    /// The Battlesong Berserker cycle from issue #7: gains menace, loses it, gains it
    /// again under a fresh annotation id, loses it again. Every grant and every
    /// wear-off is its own line — the registry re-arms on the re-grant.
    /// </summary>
    [Test]
    public void A_worn_off_ability_regranted_wears_off_again()
    {
        var t = Run(RoomLine, MulliganLine,
            BattlefieldGrant("[ 6 ]", uniq: """{ "id": 55, "grpId": 6 }""", annId: 90),
            Resend(),
            BattlefieldGrant("[ 6 ]", uniq: """{ "id": 71, "grpId": 6 }""", annId: 94),
            Resend());

        Assert.That(t.Events.Count(x => x.Kind == EventKind.AbilityGained), Is.EqualTo(2));
        Assert.That(t.Events.Count(x => x.Kind == EventKind.AbilityExpired), Is.EqualTo(2));
    }

    /// <summary>
    /// A creature with printed menace granted menace still has menace when the grant
    /// ends. The diff is set membership, not entry count: the grpid never leaves the
    /// object's description, so no line — which is what the reader would say too.
    /// </summary>
    [Test]
    public void A_grant_duplicating_a_printed_ability_never_reads_as_lost()
    {
        var t = Run(RoomLine, MulliganLine,
            // Printed first strike (id 40) plus the granted copy (id 55).
            BattlefieldGrant("[ 6 ]",
                uniq: """{ "id": 40, "grpId": 6 }, { "id": 55, "grpId": 6 }"""),
            Resend(uniq: """{ "id": 40, "grpId": 6 }"""));

        Assert.That(t.Events.Any(x => x.Kind == EventKind.AbilityExpired), Is.False);
    }
}
