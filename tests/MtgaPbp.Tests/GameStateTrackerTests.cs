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
