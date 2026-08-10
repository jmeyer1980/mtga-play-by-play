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
