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
}
