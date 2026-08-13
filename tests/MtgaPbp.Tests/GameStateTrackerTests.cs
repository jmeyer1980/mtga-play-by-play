using System.Text.Json;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class GameStateTrackerTests
{
    private sealed class FakeCardDb : ICardDb
    {
        public string? NameForLocId(int locId) => locId switch
        {
            648 => "Plains",
            44198 => "Temple of Plenty",
            _ => null
        };
        public CardInfo? CardForGrpId(int grpId) => grpId switch
        {
            94131 => new CardInfo(94131, "Temple of Plenty", "5", null, null, false),
            _ => null
        };
        public string? EnumName(string type, int value) => null;
        public string? AbilityText(int abilityGrpId) => null;
    }

    private static GameStateTracker NewTracker() => new(new FakeCardDb());
    private static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public void Apply_full_state_records_players_life_and_turn()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full",
          "gameInfo": { "gameNumber": 1 },
          "players": [ { "systemSeatNumber": 1, "lifeTotal": 20 },
                       { "systemSeatNumber": 2, "lifeTotal": 20 } ],
          "turnInfo": { "turnNumber": 1, "activePlayer": 1, "phase": 1, "step": 1 } }
        """));

        Assert.That(t.Life[1], Is.EqualTo(20));
        Assert.That(t.Turn, Is.EqualTo(1));
        Assert.That(t.ActiveSeat, Is.EqualTo(1));
        Assert.That(t.GameNumber, Is.EqualTo(1));
    }

    [Test]
    public void Apply_diff_updates_only_supplied_fields()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full",
          "players": [ { "systemSeatNumber": 1, "lifeTotal": 20 } ],
          "turnInfo": { "turnNumber": 1, "activePlayer": 1 } }
        """));
        t.Apply(Msg("""
        { "type": "GameStateType_Diff",
          "players": [ { "systemSeatNumber": 1, "lifeTotal": 18 } ] }
        """));

        Assert.That(t.Life[1], Is.EqualTo(18));
        Assert.That(t.Turn, Is.EqualTo(1), "turn must survive a diff that omits turnInfo");
    }

    [Test]
    public void NameOf_prefers_the_objects_own_name_loc_id()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 245, "grpId": 96179, "name": 648,
            "type": "GameObjectType_Card", "ownerSeatId": 1, "controllerSeatId": 1 } ] }
        """));
        Assert.That(t.NameOf(245), Is.EqualTo("Plains"));
    }

    [Test]
    public void NameOf_falls_back_to_source_card_for_an_ability()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 433, "grpId": 176406, "type": "GameObjectType_Ability",
            "objectSourceGrpId": 94131, "ownerSeatId": 1, "controllerSeatId": 1 } ] }
        """));
        Assert.That(t.NameOf(433), Is.EqualTo("Temple of Plenty's ability"));
    }

    /// <summary>
    /// Arena omits protobuf defaults, so an absent flag is the default and not silence.
    /// </summary>
    /// <remarks>
    /// Across the archive `isTapped` is true 14,967 times and false zero times, and
    /// `damage` is non-zero 1,415 times and zero zero times. Reading absence as
    /// "unchanged" latched both on: once a creature was tapped or damaged it stayed so on
    /// every later board line, and 671 of 1,946 board snapshots carried a claim that was
    /// no longer true, always in the same direction.
    /// </remarks>
    [Test]
    public void An_absent_tapped_or_damage_flag_means_untapped_and_undamaged()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 80, "grpId": 94131, "type": "GameObjectType_Card",
            "isTapped": true, "damage": 3, "cardTypes": ["CardType_Creature"] } ] }
        """));
        Assert.That(t.Get(80)!.IsTapped, Is.True);
        Assert.That(t.Get(80)!.Damage, Is.EqualTo(3));

        // The untap step and the cleanup step: the same object, described again with
        // both fields simply gone.
        t.Apply(Msg("""
        { "type": "GameStateType_Diff", "gameObjects": [
          { "instanceId": 80, "grpId": 94131, "type": "GameObjectType_Card",
            "cardTypes": ["CardType_Creature"] } ] }
        """));
        Assert.That(t.Get(80)!.IsTapped, Is.False, "it untapped");
        Assert.That(t.Get(80)!.Damage, Is.EqualTo(0), "damage wore off at cleanup");
    }

    /// <summary>
    /// An empty power object is a power of zero, which is how protobuf writes it.
    /// </summary>
    /// <remarks>
    /// Read as "unknown", the previous value stood and a creature whose buff had ended
    /// kept the buffed number: "Sazh's Chocobo 4/5 returns to 4/1" for a 0/1 Chocobo.
    /// The property being absent altogether still means unknown — a non-creature has no
    /// power at all.
    /// </remarks>
    [Test]
    public void An_empty_power_object_is_zero_rather_than_unknown()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 81, "grpId": 94131, "type": "GameObjectType_Card",
            "power": { "value": 4 }, "toughness": { "value": 5 } } ] }
        """));
        Assert.That(t.Get(81)!.Power, Is.EqualTo(4));

        t.Apply(Msg("""
        { "type": "GameStateType_Diff", "gameObjects": [
          { "instanceId": 81, "grpId": 94131, "type": "GameObjectType_Card",
            "power": {}, "toughness": { "value": 1 } } ] }
        """));
        Assert.That(t.Get(81)!.Power, Is.EqualTo(0), "an empty object is zero");
        Assert.That(t.Get(81)!.Toughness, Is.EqualTo(1));

        // Absent is still unknown, so a non-creature keeps whatever was known.
        t.Apply(Msg("""
        { "type": "GameStateType_Diff", "gameObjects": [
          { "instanceId": 81, "grpId": 94131, "type": "GameObjectType_Card" } ] }
        """));
        Assert.That(t.Get(81)!.Power, Is.EqualTo(0));
    }

    /// <summary>
    /// Per-player zones name their owner, which is who a card moving into one belongs to.
    /// </summary>
    /// <remarks>
    /// A card the client never saw has no game object and therefore no controller. The
    /// extractor used to fall straight through to the active player, which credited the
    /// opponent's draws to you — they draw on your turn too, and 40 draws across 22
    /// matches read "You draw Unknown card" when both zones belonged to seat two.
    /// </remarks>
    [Test]
    public void Zones_that_belong_to_a_player_say_so()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "zones": [
          { "zoneId": 35, "type": "ZoneType_Hand", "ownerSeatId": 2 },
          { "zoneId": 36, "type": "ZoneType_Library", "ownerSeatId": 2 },
          { "zoneId": 28, "type": "ZoneType_Battlefield" } ] }
        """));

        Assert.That(t.ZoneOwner(35), Is.EqualTo(2));
        Assert.That(t.ZoneOwner(36), Is.EqualTo(2));
        Assert.That(t.ZoneOwner(28), Is.Null, "the battlefield is shared and names no owner");
        Assert.That(t.ZoneOwner(999), Is.Null, "a zone the log never described");
    }

    [Test]
    public void NameOf_degrades_to_grpid_when_unresolvable()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 9, "grpId": 55555, "type": "GameObjectType_Card" } ] }
        """));
        Assert.That(t.NameOf(9), Is.EqualTo("Card #55555"));
    }

    /// <summary>
    /// An emblem's ability names no card anywhere in itself, and is reached through the
    /// emblem it hangs off.
    /// </summary>
    /// <remarks>
    /// Shaped after the real traffic. The ability's own <c>objectSourceGrpId</c> is 2 —
    /// the emblem's grpId, which is not a card — so the three links that used to be
    /// tried all miss and it printed as "Card #190846". The emblem carries the real
    /// source, and one hop up <c>parentId</c> reaches it.
    /// </remarks>
    [Test]
    public void NameOf_reaches_an_emblems_source_by_following_the_parent()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 522, "grpId": 2, "name": 1, "type": "GameObjectType_Emblem",
            "objectSourceGrpId": 94131, "parentId": 521 },
          { "instanceId": 523, "grpId": 190846, "type": "GameObjectType_Ability",
            "objectSourceGrpId": 2, "parentId": 522 } ]  }
        """));

        Assert.That(t.NameOf(522), Is.EqualTo("Temple of Plenty's emblem"),
            "an emblem belongs to its planeswalker rather than being its ability");
        Assert.That(t.NameOf(523), Is.EqualTo("Temple of Plenty's emblem's ability"));
    }

    /// <summary>
    /// An ability is never named from the Cards table, because its grpId is not a card id.
    /// </summary>
    /// <remarks>
    /// The two id spaces overlap. 96573 is both Sazh's Chocobo's landfall ability and the
    /// card Ureni of the Unwritten, so asking Cards for it answered with a 7/7 that was
    /// never in the game — "Escape Tunnel triggers Ureni of the Unwritten", 294 lines
    /// across 46 matches, and the same wrong names in the index's search text. The source
    /// and parent links resolve these correctly; the card lookup was reaching them first.
    /// </remarks>
    [Test]
    public void NameOf_never_reads_an_abilitys_grpid_as_a_card_id()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 700, "grpId": 94131, "type": "GameObjectType_Ability",
            "objectSourceGrpId": 94131, "ownerSeatId": 1, "controllerSeatId": 1 } ] }
        """));

        // 94131 is a real card in the fixture database. Read as this ability's own id it
        // would name that card outright; read as its source it names the ability.
        Assert.That(t.NameOf(700), Is.EqualTo("Temple of Plenty's ability"));
        Assert.That(t.NameOf(700), Is.Not.EqualTo("Temple of Plenty"));
    }

    [Test]
    public void NameOf_ignores_a_parent_it_cannot_name_either()
    {
        // "Card #2's ability" is no better than "Card #190846" and is longer.
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 40, "grpId": 2, "type": "GameObjectType_Emblem" },
          { "instanceId": 41, "grpId": 190846, "type": "GameObjectType_Ability",
            "parentId": 40 } ] }
        """));

        Assert.That(t.NameOf(41), Is.EqualTo("Card #190846"));
    }

    [Test]
    public void NameOf_survives_a_parent_chain_that_loops()
    {
        // Nothing in the archive has one. A hang here would be a hang mid-render.
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 60, "grpId": 111, "type": "GameObjectType_Ability", "parentId": 61 },
          { "instanceId": 61, "grpId": 222, "type": "GameObjectType_Ability", "parentId": 60 } ] }
        """));

        Assert.That(t.NameOf(60), Is.EqualTo("Card #111"));
    }

    [Test]
    public void NameOf_names_an_object_it_never_saw_instead_of_printing_its_id()
    {
        // Fog of war: Arena says object 348 changed zones without ever having sent
        // its state, so there is nothing to look up. The internal id is not a phrase
        // — on screen it means nothing, and a synthesiser reads "#348" as "number
        // three hundred forty-eight is put into the graveyard".
        var t = NewTracker();
        Assert.That(t.NameOf(348), Is.EqualTo("Unknown card"));
    }

    [Test]
    public void Resolve_follows_a_single_id_change()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 305, "grpId": 96179, "name": 648,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 110, "affectedIds": [ 305 ],
            "type": [ "AnnotationType_ObjectIdChanged" ],
            "details": [
              { "key": "orig_id", "valueInt32": [ 305 ] },
              { "key": "new_id",  "valueInt32": [ 430 ] } ] } ] }
        """));
        Assert.That(t.Resolve(305), Is.EqualTo(430));
    }

    [Test]
    public void Resolve_follows_a_multi_hop_chain()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 1, "type": [ "AnnotationType_ObjectIdChanged" ], "details": [
            { "key": "orig_id", "valueInt32": [ 100 ] },
            { "key": "new_id",  "valueInt32": [ 200 ] } ] },
          { "id": 2, "type": [ "AnnotationType_ObjectIdChanged" ], "details": [
            { "key": "orig_id", "valueInt32": [ 200 ] },
            { "key": "new_id",  "valueInt32": [ 300 ] } ] } ] }
        """));
        Assert.That(t.Resolve(100), Is.EqualTo(300));
        Assert.That(t.Resolve(200), Is.EqualTo(300));
        Assert.That(t.Resolve(300), Is.EqualTo(300));
    }

    [Test]
    public void Resolve_survives_a_cyclic_alias_without_hanging()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "annotations": [
          { "id": 1, "type": [ "AnnotationType_ObjectIdChanged" ], "details": [
            { "key": "orig_id", "valueInt32": [ 10 ] },
            { "key": "new_id",  "valueInt32": [ 20 ] } ] },
          { "id": 2, "type": [ "AnnotationType_ObjectIdChanged" ], "details": [
            { "key": "orig_id", "valueInt32": [ 20 ] },
            { "key": "new_id",  "valueInt32": [ 10 ] } ] } ] }
        """));
        Assert.That(t.Resolve(10), Is.AnyOf(10, 20));
    }

    [Test]
    public void NameOf_follows_alias_so_a_card_keeps_its_name_across_zones()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full",
          "gameObjects": [ { "instanceId": 430, "grpId": 96179, "name": 648,
                             "type": "GameObjectType_Card" } ],
          "annotations": [ { "id": 1, "type": [ "AnnotationType_ObjectIdChanged" ],
            "details": [
              { "key": "orig_id", "valueInt32": [ 305 ] },
              { "key": "new_id",  "valueInt32": [ 430 ] } ] } ] }
        """));
        Assert.That(t.NameOf(305), Is.EqualTo("Plains"));
    }

    [Test]
    public void Apply_tracks_object_stats_and_tapped_state()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 50, "grpId": 96179, "name": 648, "type": "GameObjectType_Card",
            "power": 3, "toughness": 4, "damage": 1, "isTapped": true,
            "ownerSeatId": 2, "controllerSeatId": 2, "zoneId": 28 } ] }
        """));
        var o = t.Get(50)!;
        Assert.That(o.Power, Is.EqualTo(3));
        Assert.That(o.Toughness, Is.EqualTo(4));
        Assert.That(o.Damage, Is.EqualTo(1));
        Assert.That(o.IsTapped, Is.True);
        Assert.That(o.ControllerSeat, Is.EqualTo(2));
    }

    /// <summary>
    /// JsonElement.TryGetInt32 THROWS when the value is not a number — it returns
    /// false only for numeric overflow. Arena sends some of these fields as strings,
    /// so every numeric read must check ValueKind first.
    /// </summary>
    [Test]
    public void Apply_survives_numeric_fields_arriving_as_strings_or_bools()
    {
        var t = NewTracker();
        Assert.DoesNotThrow(() => t.Apply(Msg("""
        { "type": "GameStateType_Diff",
          "gameInfo": { "gameNumber": "2" },
          "players": [ { "systemSeatNumber": "1", "lifeTotal": "18" },
                       { "systemSeatNumber": 2, "lifeTotal": 15 } ],
          "turnInfo": { "turnNumber": "4", "activePlayer": true, "phase": null },
          "zones": [ { "zoneId": "28", "type": "ZoneType_Battlefield" } ],
          "gameObjects": [ { "instanceId": "50", "grpId": "96179", "damage": "3" },
                           { "instanceId": 51, "grpId": 96179, "name": 648 } ],
          "annotations": [ { "id": 1, "type": [ "AnnotationType_ObjectIdChanged" ],
            "details": [ { "key": "orig_id", "valueInt32": [ "9" ] },
                         { "key": "new_id",  "valueInt32": [ 10 ] } ] } ] }
        """)));

        // The well-formed entries still land.
        Assert.That(t.Life[2], Is.EqualTo(15));
        Assert.That(t.NameOf(51), Is.EqualTo("Plains"));
    }

    [Test]
    public void Apply_reports_creatures_that_just_declared_an_attack()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Diff", "gameObjects": [
          { "instanceId": 377, "grpId": 94816, "name": 648, "controllerSeatId": 2,
            "attackState": "AttackState_Declared", "attackInfo": { "targetId": 1 } } ] }
        """));

        Assert.That(t.NewAttackers, Is.EquivalentTo(new[] { 377 }));
        Assert.That(t.Get(377)!.AttackTargetId, Is.EqualTo(1));
    }

    [Test]
    public void Apply_does_not_re_report_an_attacker_that_merely_stays_attacking()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Diff", "gameObjects": [
          { "instanceId": 377, "grpId": 1, "controllerSeatId": 2,
            "attackState": "AttackState_Declared", "attackInfo": { "targetId": 1 } } ] }
        """));
        t.Apply(Msg("""
        { "type": "GameStateType_Diff", "gameObjects": [
          { "instanceId": 377, "grpId": 1, "controllerSeatId": 2,
            "attackState": "AttackState_Attacking", "attackInfo": { "targetId": 1 } } ] }
        """));

        Assert.That(t.NewAttackers, Is.Empty, "attackers are reported once, at declaration");
    }

    [Test]
    public void Apply_reports_the_same_creature_attacking_again_on_a_later_turn()
    {
        var t = NewTracker();
        string Attack(string state) => $$"""
            { "type": "GameStateType_Diff", "gameObjects": [
              { "instanceId": 377, "grpId": 1, "controllerSeatId": 2,
                "attackState": "{{state}}", "attackInfo": { "targetId": 1 } } ] }
            """;

        t.Apply(Msg(Attack("AttackState_Declared")));
        Assert.That(t.NewAttackers, Is.EquivalentTo(new[] { 377 }), "first attack");

        t.Apply(Msg(Attack("AttackState_None")));       // combat ends
        Assert.That(t.NewAttackers, Is.Empty);

        t.Apply(Msg(Attack("AttackState_Declared")));   // attacks again next turn
        Assert.That(t.NewAttackers, Is.EquivalentTo(new[] { 377 }), "second attack");
    }

    /// <summary>
    /// Arena never sends AttackState_None — it just stops sending the field. A new
    /// turn is therefore the only signal that combat ended.
    /// </summary>
    [Test]
    public void A_new_turn_clears_combat_so_the_same_creature_can_attack_again()
    {
        var t = NewTracker();
        string Turn(int n) => $$"""{ "type": "GameStateType_Diff", "turnInfo": { "turnNumber": {{n}} } }""";
        const string Declare = """
            { "type": "GameStateType_Diff", "gameObjects": [
              { "instanceId": 299, "grpId": 1, "controllerSeatId": 2,
                "attackState": "AttackState_Declared", "attackInfo": { "targetId": 1 } } ] }
            """;

        t.Apply(Msg(Turn(5)));
        t.Apply(Msg(Declare));
        Assert.That(t.NewAttackers, Is.EquivalentTo(new[] { 299 }));

        t.Apply(Msg(Turn(7)));                 // no AttackState_None ever arrives
        t.Apply(Msg(Declare));
        Assert.That(t.NewAttackers, Is.EquivalentTo(new[] { 299 }),
            "the same creature attacking on a later turn must be reported again");
    }

    [Test]
    public void Apply_reports_creatures_that_just_declared_a_block()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Diff", "gameObjects": [
          { "instanceId": 448, "grpId": 105186, "controllerSeatId": 2,
            "blockState": "BlockState_Declared",
            "blockInfo": { "attackerIds": [ 388 ] } } ] }
        """));

        Assert.That(t.NewBlockers, Is.EquivalentTo(new[] { 448 }));
        Assert.That(t.Get(448)!.BlockedAttackerIds, Is.EquivalentTo(new[] { 388 }));
    }

    [Test]
    public void Combat_reports_are_cleared_between_messages()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Diff", "gameObjects": [
          { "instanceId": 1, "grpId": 1, "attackState": "AttackState_Declared" } ] }
        """));
        t.Apply(Msg("""{ "type": "GameStateType_Diff" }"""));
        Assert.That(t.NewAttackers, Is.Empty);
    }

    [Test]
    public void CreaturesOnBattlefield_returns_only_that_seats_creatures_in_play()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full",
          "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" },
                     { "zoneId": 35, "type": "ZoneType_Hand" },
                     { "zoneId": 31, "type": "ZoneType_Graveyard" } ],
          "gameObjects": [
            { "instanceId": 1, "grpId": 1, "name": 648, "controllerSeatId": 1,
              "zoneId": 28, "cardTypes": [ "CardType_Creature" ],
              "power": 2, "toughness": 3, "damage": 1, "isTapped": true },
            { "instanceId": 2, "grpId": 1, "name": 648, "controllerSeatId": 1,
              "zoneId": 28, "cardTypes": [ "CardType_Land" ] },
            { "instanceId": 3, "grpId": 1, "name": 648, "controllerSeatId": 2,
              "zoneId": 28, "cardTypes": [ "CardType_Creature" ] },
            { "instanceId": 4, "grpId": 1, "name": 648, "controllerSeatId": 1,
              "zoneId": 35, "cardTypes": [ "CardType_Creature" ] },
            { "instanceId": 5, "grpId": 1, "name": 648, "controllerSeatId": 1,
              "zoneId": 31, "cardTypes": [ "CardType_Creature" ] } ] }
        """));

        var mine = t.CreaturesOnBattlefield(1);
        Assert.That(mine.Select(o => o.InstanceId), Is.EqualTo(new[] { 1 }),
            "lands, the opponent's creatures, hand and graveyard must all be excluded");
        Assert.That(mine[0].Damage, Is.EqualTo(1));
        Assert.That(mine[0].IsTapped, Is.True);
        Assert.That(t.CreaturesOnBattlefield(2).Select(o => o.InstanceId), Is.EqualTo(new[] { 3 }));
    }

    [Test]
    public void CreaturesOnBattlefield_is_empty_when_zones_are_unknown()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "gameObjects": [
          { "instanceId": 1, "grpId": 1, "controllerSeatId": 1, "zoneId": 28,
            "cardTypes": [ "CardType_Creature" ] } ] }
        """));
        Assert.That(t.CreaturesOnBattlefield(1), Is.Empty,
            "without a zone table we cannot claim anything is on the battlefield");
    }

    [Test]
    public void Apply_records_every_statline_change_with_the_stamp_it_was_given()
    {
        // The history is what lets a line say what a creature was at the time rather
        // than what it ended the match as, so each sample has to carry the caller's
        // sequence number and whether the permanent was in play when it changed.
        var t = NewTracker();
        string Board(int power) => $$"""
            { "type": "GameStateType_Full",
              "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" } ],
              "gameObjects": [ { "instanceId": 50, "grpId": 1, "name": 648, "zoneId": 28,
                "cardTypes": [ "CardType_Creature" ],
                "power": {{power}}, "toughness": 1 } ] }
            """;

        t.Apply(Msg(Board(1)), stamp: 7);
        t.Apply(Msg(Board(1)), stamp: 9);   // unchanged: nothing new to record
        t.Apply(Msg(Board(5)), stamp: 12);

        var samples = t.StatHistory.Single(h => h.InstanceId == 50).Samples;
        Assert.That(samples.Select(s => (s.Stamp, s.Power, s.InPlay)), Is.EqualTo(new[]
        {
            (7, 1, true),
            (12, 5, true)
        }));
    }

    [Test]
    public void Apply_records_zone_types_by_id()
    {
        var t = NewTracker();
        t.Apply(Msg("""
        { "type": "GameStateType_Full", "zones": [
          { "zoneId": 28, "type": "ZoneType_Battlefield" },
          { "zoneId": 35, "type": "ZoneType_Hand" } ] }
        """));
        Assert.That(t.ZoneTypes[28], Is.EqualTo("ZoneType_Battlefield"));
        Assert.That(t.ZoneTypes[35], Is.EqualTo("ZoneType_Hand"));
    }
}
