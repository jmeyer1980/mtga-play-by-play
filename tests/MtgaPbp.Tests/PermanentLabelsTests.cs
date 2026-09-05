using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The rule that decides how much of a permanent's identity a line has to spell out.
/// Driven through <see cref="EventExtractor"/> because the answer depends on the whole
/// match — which is the entire reason the decision is deferred to a second pass — and
/// asserted on narrated lines, because line count is what the rule is trading against.
/// </summary>
public class PermanentLabelsTests
{
    private const int Rabbit = 1;        // token, printed 1/1
    private const int HareApparent = 2;  // printed 2/2
    private const int EtherealArmor = 3;
    private const int Wildcard = 4;      // printed */*
    private const int Businessperson = 5; // a name only ever reached by being renamed

    private sealed class FakeCardDb : ICardDb
    {
        public string? NameForLocId(int locId) => locId switch
        {
            Rabbit => "Rabbit",
            HareApparent => "Hare Apparent",
            EtherealArmor => "Ethereal Armor",
            Wildcard => "Wildcard",
            Businessperson => "Legitimate Businessperson",
            _ => null
        };

        public CardInfo? CardForGrpId(int grpId) => grpId switch
        {
            Rabbit => new CardInfo(Rabbit, "Rabbit", "2", "1", "1", IsToken: true),
            HareApparent => new CardInfo(HareApparent, "Hare Apparent", "2", "2", "2", false),
            EtherealArmor => new CardInfo(EtherealArmor, "Ethereal Armor", "1", "", "", false),
            Wildcard => new CardInfo(Wildcard, "Wildcard", "2", "*", "*", false),
            _ => null
        };

        public string? EnumName(string type, int value) => null;
        public string? AbilityText(int abilityGrpId) => null;
    }

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

    private static string Gre(string gameStateMessage) => $$"""
    { "timestamp": "1002", "greToClientEvent": { "greToClientMessages": [
      { "type": "GREMessageType_GameStateMessage",
        "gameStateMessage": {{gameStateMessage}} } ] } }
    """;

    /// <summary>One creature of ours on the battlefield, at the size given.</summary>
    private static string Creature(int id, int grpId, int power, int toughness,
                                   bool attacking = false) => $$"""
        { "instanceId": {{id}}, "grpId": {{grpId}}, "name": {{grpId}},
          "controllerSeatId": 1, "zoneId": 28, "cardTypes": [ "CardType_Creature" ],
          "power": {{power}}, "toughness": {{toughness}}
          {{(attacking
              ? """, "attackState": "AttackState_Attacking", "attackInfo": { "targetId": 2 }"""
              : "")}} }
        """;

    /// <summary>The same creature, reporting a name that is not its card's.</summary>
    private static string Renamed(int id, int grpId, int nameLocId, int power, int toughness,
                                  bool attacking = false) => $$"""
        { "instanceId": {{id}}, "grpId": {{grpId}}, "name": {{nameLocId}},
          "controllerSeatId": 1, "zoneId": 28, "cardTypes": [ "CardType_Creature" ],
          "power": {{power}}, "toughness": {{toughness}}
          {{(attacking
              ? """, "attackState": "AttackState_Attacking", "attackInfo": { "targetId": 2 }"""
              : "")}} }
        """;

    /// <summary>A turn boundary carrying the board it starts with.</summary>
    /// <remarks>
    /// The annotation id climbs with the turn because Arena's do, and never repeats
    /// inside a game. A fixture that reused one would be a log Arena never sends, and
    /// would now be read as a resync replaying itself (#52).
    /// </remarks>
    private static string Turn(int number, params string[] creatures) => Gre($$"""
        { "type": "GameStateType_Full",
          "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" },
                     { "zoneId": 35, "type": "ZoneType_Hand" } ],
          "turnInfo": { "turnNumber": {{number}}, "activePlayer": 1 },
          "gameObjects": [ {{string.Join(",", creatures)}} ],
          "annotations": [ { "id": {{number}}, "affectorId": 1, "affectedIds": [ 1 ],
            "type": [ "AnnotationType_NewTurnStarted" ] } ] }
        """);

    /// <summary>A state change inside a turn, with no annotation of its own.</summary>
    private static string Within(params string[] creatures) => Gre($$"""
        { "type": "GameStateType_Diff",
          "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" } ],
          "gameObjects": [ {{string.Join(",", creatures)}} ] }
        """);

    private static IReadOnlyList<string> Lines(params string[] log)
    {
        var transcript = new EventExtractor(new FakeCardDb())
            .Extract("m1", [RoomLine, MulliganLine, .. log]);
        return Narrator.Narrate(transcript, Density.Beats).Select(l => l.Text).ToList();
    }

    private static IEnumerable<string> Attacks(params string[] log) =>
        Lines(log).Where(l => l.StartsWith("You attack", StringComparison.Ordinal));

    // ---------- what the statline is for ----------

    [Test]
    public void Interchangeable_creatures_stay_one_collapsed_line()
    {
        // The property the whole rule exists to protect: five 1/1 Rabbits attacking are
        // one line, not five, and nothing about disambiguation may change that.
        var attacks = Attacks(
            Turn(1, Enumerable.Range(10, 5).Select(id => Creature(id, Rabbit, 1, 1)).ToArray()),
            Turn(2, Enumerable.Range(10, 5)
                              .Select(id => Creature(id, Rabbit, 1, 1, attacking: true))
                              .ToArray()));

        Assert.That(attacks, Is.EqualTo(new[] { "You attack with Rabbit ×5" }));
    }

    [Test]
    public void A_creature_at_its_printed_size_is_named_bare()
    {
        var attacks = Attacks(
            Turn(1, Creature(10, HareApparent, 2, 2)),
            Turn(2, Creature(10, HareApparent, 2, 2, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[] { "You attack with Hare Apparent" }));
    }

    [Test]
    public void A_creature_off_its_printed_size_carries_it()
    {
        var attacks = Attacks(
            Turn(1, Creature(10, HareApparent, 2, 2)),
            Turn(2, Creature(10, HareApparent, 3, 3, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[] { "You attack with Hare Apparent 3/3" }));
    }

    [Test]
    public void A_lone_permanent_is_never_given_a_letter()
    {
        // Explicitly asserted, because numbering a permanent nothing can be confused
        // with is the failure mode that would make every transcript worse.
        var attacks = Attacks(
            Turn(1, Creature(10, HareApparent, 2, 2)),
            Turn(2, Creature(10, HareApparent, 5, 5)),
            Turn(3, Creature(10, HareApparent, 5, 5, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[] { "You attack with Hare Apparent 5/5" }));
    }

    [Test]
    public void The_statline_reported_is_the_one_at_the_time_not_at_the_end()
    {
        // The Rabbit that was 5/5 on turn 2 is 6/6 by turn 4, and printing the final
        // size against the earlier line would be a lie about what happened.
        var attacks = Attacks(
            Turn(1, Creature(10, Rabbit, 1, 1)),
            Turn(2, Creature(10, Rabbit, 5, 5, attacking: true)),
            Turn(3, Creature(10, Rabbit, 5, 5)),
            Turn(4, Creature(10, Rabbit, 6, 6, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[]
        {
            "You attack with Rabbit 5/5",
            "You attack with Rabbit 6/6"
        }));
    }

    [Test]
    public void A_printed_statline_that_is_not_a_number_is_left_alone()
    {
        // "*" gives no baseline to call anything a change, so the size is not reported
        // at all rather than reported against a guess.
        var attacks = Attacks(
            Turn(1, Creature(10, Wildcard, 3, 3)),
            Turn(2, Creature(10, Wildcard, 7, 7, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[] { "You attack with Wildcard" }));
    }

    // ---------- when the statline stops being enough ----------

    [Test]
    public void Copies_that_split_and_then_match_again_get_letters()
    {
        // The case this whole feature exists for. Two Rabbits are told apart on turn 2
        // by their sizes; on turn 3 both are 6/6 and the size says nothing. Without the
        // letters a reader following the 5/5 has no way to know which one it became.
        var attacks = Attacks(
            Turn(1, Creature(10, Rabbit, 1, 1), Creature(11, Rabbit, 1, 1)),
            Turn(2, Creature(10, Rabbit, 5, 5), Creature(11, Rabbit, 1, 1)),
            Turn(3, Creature(10, Rabbit, 6, 6, attacking: true),
                    Creature(11, Rabbit, 6, 6, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[]
        {
            "You attack with Rabbit A 6/6",
            "You attack with Rabbit B 6/6"
        }));
    }

    [Test]
    public void Letters_follow_instance_id_order_so_a_rebuild_produces_the_same_ones()
    {
        // Order is taken from the log rather than from anything about this run, so the
        // same match always renders the same letters — declaring them in the other
        // order must not swap A and B.
        var attacks = Attacks(
            Turn(1, Creature(11, Rabbit, 1, 1), Creature(10, Rabbit, 1, 1)),
            Turn(2, Creature(11, Rabbit, 1, 1), Creature(10, Rabbit, 5, 5)),
            Turn(3, Creature(11, Rabbit, 6, 6, attacking: true),
                    Creature(10, Rabbit, 6, 6, attacking: true)));

        Assert.That(attacks, Does.Contain("You attack with Rabbit A 6/6"));
        Assert.That(attacks, Does.Contain("You attack with Rabbit B 6/6"));

        // 10 is the lower id, so 10 is A — and 10 is the one that was 5/5 on turn 2.
        var buffed = Lines(
            Turn(1, Creature(11, Rabbit, 1, 1), Creature(10, Rabbit, 1, 1)),
            Turn(2, Creature(11, Rabbit, 1, 1), Creature(10, Rabbit, 5, 5)),
            Turn(3, Creature(11, Rabbit, 6, 6), Creature(10, Rabbit, 6, 6)));

        Assert.That(buffed.Any(l => l.Contains("Rabbit A 5/5", StringComparison.Ordinal)),
            Is.True, "the lower instance id keeps the earlier letter");
    }

    [Test]
    public void Copies_that_split_and_stay_split_are_told_apart_by_size_alone()
    {
        // A pack that divides is completely described by the two sizes. Lettering all
        // of them would cost a line each, every turn, to say nothing new.
        var attacks = Attacks(
            Turn(1, Creature(10, Rabbit, 2, 2), Creature(11, Rabbit, 2, 2),
                    Creature(12, Rabbit, 2, 2)),
            Turn(2, Creature(10, Rabbit, 2, 2), Creature(11, Rabbit, 2, 2),
                    Creature(12, Rabbit, 4, 4)),
            Turn(3, Creature(10, Rabbit, 2, 2, attacking: true),
                    Creature(11, Rabbit, 2, 2, attacking: true),
                    Creature(12, Rabbit, 4, 4, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[]
        {
            "You attack with Rabbit 2/2 ×2",
            "You attack with Rabbit 4/4"
        }));
    }

    [Test]
    public void Copies_that_were_never_apart_are_never_lettered()
    {
        var attacks = Attacks(
            Turn(1, Creature(10, Rabbit, 2, 2), Creature(11, Rabbit, 2, 2)),
            Turn(2, Creature(10, Rabbit, 3, 3), Creature(11, Rabbit, 3, 3)),
            Turn(3, Creature(10, Rabbit, 3, 3, attacking: true),
                    Creature(11, Rabbit, 3, 3, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[] { "You attack with Rabbit 3/3 ×2" }));
    }

    [Test]
    public void A_difference_that_does_not_survive_the_turn_earns_no_letter()
    {
        // Two creatures whose own triggers pump them in consecutive messages differ for
        // the width of one message and are equal for the rest of combat. Compared
        // instant by instant that is a split followed by a convergence, and would buy
        // them permanent names; compared at the turn boundary, where "until end of turn"
        // has expired, it is what it actually was — nothing.
        var attacks = Attacks(
            Turn(1, Creature(10, Rabbit, 1, 1), Creature(11, Rabbit, 1, 1)),
            Within(Creature(10, Rabbit, 2, 2)),
            Within(Creature(11, Rabbit, 2, 2)),
            Within(Creature(10, Rabbit, 2, 2, attacking: true),
                   Creature(11, Rabbit, 2, 2, attacking: true)),
            Turn(2, Creature(10, Rabbit, 1, 1), Creature(11, Rabbit, 1, 1)));

        Assert.That(attacks, Is.EqualTo(new[] { "You attack with Rabbit 2/2 ×2" }));
    }

    [Test]
    public void Letters_go_only_on_the_copies_that_need_them()
    {
        // Four Rabbits, two of which converge on 6/6. The other two never stopped being
        // interchangeable, so they stay anonymous and stay collapsed.
        var attacks = Attacks(
            Turn(1, Creature(10, Rabbit, 1, 1), Creature(11, Rabbit, 1, 1),
                    Creature(12, Rabbit, 1, 1), Creature(13, Rabbit, 1, 1)),
            Turn(2, Creature(10, Rabbit, 5, 5), Creature(11, Rabbit, 1, 1),
                    Creature(12, Rabbit, 1, 1), Creature(13, Rabbit, 1, 1)),
            Turn(3, Creature(10, Rabbit, 6, 6, attacking: true),
                    Creature(11, Rabbit, 6, 6, attacking: true),
                    Creature(12, Rabbit, 1, 1, attacking: true),
                    Creature(13, Rabbit, 1, 1, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[]
        {
            "You attack with Rabbit A 6/6",
            "You attack with Rabbit B 6/6",
            "You attack with Rabbit ×2"
        }));
    }

    [Test]
    public void The_board_line_letters_without_repeating_the_size()
    {
        // The board already prints every creature's size, so the letter goes on alone.
        var board = Lines(
            Turn(1, Creature(10, Rabbit, 1, 1), Creature(11, Rabbit, 1, 1)),
            Turn(2, Creature(10, Rabbit, 5, 5), Creature(11, Rabbit, 1, 1)),
            Turn(3, Creature(10, Rabbit, 6, 6), Creature(11, Rabbit, 6, 6)),
            Turn(4, Creature(10, Rabbit, 6, 6), Creature(11, Rabbit, 6, 6)))
            .Last(l => l.StartsWith("You control", StringComparison.Ordinal));

        Assert.That(board, Is.EqualTo("You control: Rabbit A 6/6, Rabbit B 6/6"));
    }

    /// <summary>
    /// Creatures that read identically are counted rather than listed: twenty-eight
    /// "Rabbit 1/1" was already unreadable before the gap mark made it longer (#205). The
    /// count leads, the way a crowd line and the decklist count their subjects, and the
    /// group sits where its first member stood so the rest of the line keeps its order.
    /// A Rabbit at another size is another entry — that difference is information.
    /// </summary>
    [Test]
    public void Interchangeable_creatures_on_the_board_line_are_counted_not_listed()
    {
        var board = Lines(
            Turn(1, Creature(10, Rabbit, 1, 1), Creature(11, HareApparent, 2, 2),
                    Creature(12, Rabbit, 1, 1), Creature(13, Rabbit, 1, 1),
                    Creature(14, Rabbit, 3, 3)),
            Turn(2, Creature(10, Rabbit, 1, 1), Creature(11, HareApparent, 2, 2),
                    Creature(12, Rabbit, 1, 1), Creature(13, Rabbit, 1, 1),
                    Creature(14, Rabbit, 3, 3)))
            .Last(l => l.StartsWith("You control", StringComparison.Ordinal));

        Assert.That(board, Is.EqualTo("You control: 3× Rabbit 1/1, Hare Apparent 2/2, Rabbit 3/3"));
    }

    /// <summary>
    /// A letter is exactly what makes two same-named creatures not interchangeable, so a
    /// lettered creature is never folded into a count; only its anonymous siblings are.
    /// </summary>
    [Test]
    public void A_lettered_creature_is_never_folded_into_a_count()
    {
        var board = Lines(
            Turn(1, Creature(10, Rabbit, 1, 1), Creature(11, Rabbit, 1, 1),
                    Creature(12, Rabbit, 1, 1), Creature(13, Rabbit, 1, 1)),
            Turn(2, Creature(10, Rabbit, 5, 5), Creature(11, Rabbit, 1, 1),
                    Creature(12, Rabbit, 1, 1), Creature(13, Rabbit, 1, 1)),
            Turn(3, Creature(10, Rabbit, 6, 6), Creature(11, Rabbit, 6, 6),
                    Creature(12, Rabbit, 1, 1), Creature(13, Rabbit, 1, 1)),
            Turn(4, Creature(10, Rabbit, 6, 6), Creature(11, Rabbit, 6, 6),
                    Creature(12, Rabbit, 1, 1), Creature(13, Rabbit, 1, 1)))
            .Last(l => l.StartsWith("You control", StringComparison.Ordinal));

        Assert.That(board, Is.EqualTo("You control: Rabbit A 6/6, Rabbit B 6/6, 2× Rabbit 1/1"));
    }

    // ---------- what a spell did to what it hit ----------

    /// <summary>
    /// An aura cast on <paramref name="target"/>. The size change deliberately lands in
    /// the message that carries the resolution rather than the one that carries the
    /// cast, because that is where Arena puts it — which is the whole reason this cannot
    /// be worked out while the cast is being emitted.
    /// </summary>
    private static string[] Aura(int target, int before, int after) =>
    [
        Gre($$"""
            { "type": "GameStateType_Diff",
              "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" } ],
              "gameObjects": [ {{Creature(target, Rabbit, before, before)}},
                { "instanceId": 50, "grpId": {{EtherealArmor}}, "name": {{EtherealArmor}},
                  "controllerSeatId": 1, "zoneId": 35 } ],
              "annotations": [ { "id": 2, "affectedIds": [ 50 ],
                "type": [ "AnnotationType_ZoneTransfer" ], "details": [
                  { "key": "category", "valueString": [ "CastSpell" ] } ] } ] }
            """),
        Gre($$"""
            { "type": "GameStateType_Diff",
              "persistentAnnotations": [ { "id": 3, "affectorId": 50,
                "affectedIds": [ {{target}} ],
                "type": [ "AnnotationType_TargetSpec" ] } ] }
            """),
        Gre($$"""
            { "type": "GameStateType_Diff",
              "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" } ],
              "gameObjects": [ {{Creature(target, Rabbit, after, after)}} ],
              "annotations": [ { "id": 4, "affectedIds": [ 50 ],
                "type": [ "AnnotationType_ZoneTransfer" ], "details": [
                  { "key": "category", "valueString": [ "Resolve" ] } ] } ] }
            """),
    ];

    [Test]
    public void A_buff_reports_what_its_target_became()
    {
        var cast = Lines([
            Turn(1, Creature(10, Rabbit, 1, 1)),
            .. Aura(target: 10, before: 1, after: 5)
        ]).Single(l => l.StartsWith("You cast", StringComparison.Ordinal));

        Assert.That(cast, Is.EqualTo("You cast Ethereal Armor, targeting Rabbit (1/1 → 5/5)"));
    }

    [Test]
    public void A_spell_that_leaves_its_target_the_same_size_reports_no_arrow()
    {
        // Most spells are not buffs, and "targeting Rabbit (1/1 → 1/1)" is worse than
        // saying nothing.
        var cast = Lines([
            Turn(1, Creature(10, Rabbit, 1, 1)),
            .. Aura(target: 10, before: 1, after: 1)
        ]).Single(l => l.StartsWith("You cast", StringComparison.Ordinal));

        Assert.That(cast, Is.EqualTo("You cast Ethereal Armor, targeting Rabbit"));
    }

    [Test]
    public void A_buff_on_a_lettered_copy_names_which_copy()
    {
        var lines = Lines([
            Turn(1, Creature(10, Rabbit, 1, 1), Creature(11, Rabbit, 1, 1)),
            Turn(2, Creature(10, Rabbit, 5, 5), Creature(11, Rabbit, 1, 1)),
            .. Aura(target: 11, before: 1, after: 6),
            Turn(3, Creature(10, Rabbit, 6, 6), Creature(11, Rabbit, 6, 6))
        ]);

        Assert.That(lines.Single(l => l.StartsWith("You cast", StringComparison.Ordinal)),
            Is.EqualTo("You cast Ethereal Armor, targeting Rabbit B (1/1 → 6/6)"));
    }

    // ---------- a rename does not reach backwards ----------

    [Test]
    public void A_renamed_permanent_keeps_its_old_name_on_earlier_boards()
    {
        // Witness Protection renames what it enchants, and Arena reports that by
        // changing the same instance's name locId mid-stream. Board lines are written in
        // a second pass, once the whole log has been read, so the end-of-game name used
        // to be stamped onto every turn — naming a creature after a card that had not
        // been drawn yet. Issue #23.
        var boards = Lines(
                Turn(1, Creature(10, HareApparent, 2, 2)),
                Turn(2, Creature(10, HareApparent, 2, 2)),
                Turn(3, Renamed(10, HareApparent, Businessperson, 2, 2)))
            .Where(l => l.Contains("control", StringComparison.Ordinal))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(boards.First(), Does.Contain("Hare Apparent"),
                "the first board predates the rename");
            Assert.That(boards.First(), Does.Not.Contain("Legitimate Businessperson"),
                "the rename must not reach backwards");
            Assert.That(boards.Last(), Does.Contain("Legitimate Businessperson"),
                "and the board after it must carry the new name");
        });
    }

    [Test]
    public void A_permanent_that_was_never_renamed_is_unaffected()
    {
        // The fallback chain in NameOf names emblems, abilities and tokens that localize
        // to nothing; as-of-turn naming defers to it and must not shadow it.
        var attacks = Attacks(
            Turn(1, Creature(10, HareApparent, 2, 2)),
            Turn(2, Creature(10, HareApparent, 3, 3, attacking: true)));

        Assert.That(attacks, Is.EqualTo(new[] { "You attack with Hare Apparent 3/3" }));
    }

    [Test]
    public void A_spell_names_the_target_it_was_pointed_at_not_what_it_became()
    {
        // The aura that renames is the aura being cast, so the target's new name is
        // produced by this very resolution. Naming the cast after it would report the
        // player targeting something that did not exist when they targeted it — the
        // same backwards leak as the board lines, reached through FillTargets. #23.
        var cast = Lines([
                Turn(1, Creature(10, Rabbit, 1, 1)),
                .. Aura(target: 10, before: 1, after: 5),
                Turn(2, Renamed(10, Rabbit, Businessperson, 5, 5))
            ]).Single(l => l.StartsWith("You cast", StringComparison.Ordinal));

        Assert.That(cast, Is.EqualTo("You cast Ethereal Armor, targeting Rabbit (1/1 → 5/5)"));
    }
}
