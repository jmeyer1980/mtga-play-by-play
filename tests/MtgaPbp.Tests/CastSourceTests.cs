using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Where a spell was cast from, when that is not where spells come from (#127). A
/// flashback, an escape, an adventure and a foretell all rendered as an ordinary cast, so
/// the page showed a card being cast whose last reported whereabouts were somewhere it
/// could not be cast from — which reads as the parser repeating a line.
/// </summary>
public class CastSourceTests
{
    private const int Mine = 1;

    private static string Line(string? fromZone, string? target = null) =>
        Narrator.Narrate(
                RendererTests.Sample(opening: false) with
                {
                    Events =
                    [
                        new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = Mine },
                        new GameEvent
                        {
                            Seq = 1, Kind = EventKind.SpellCast, Turn = 1,
                            ActorSeat = Mine, ActiveSeat = Mine,
                            Phase = 2,   // your own main phase, so #148 stays quiet
                            SourceName = "Tenacious Underdog",
                            FromZone = fromZone,
                            TargetName = target
                        },
                    ]
                }, Density.Beats)
            .Select(l => l.Text)
            .Single(x => x.Contains("Tenacious Underdog", StringComparison.Ordinal));

    /// <summary>
    /// A cast's destination is refused, not merely unused: every spell goes to the
    /// stack, so it says nothing a reader wants, and an event carrying a field its own
    /// comment says it does not have is one refactor away from being believed.
    /// </summary>
    [Test]
    public void A_cast_carries_where_it_came_from_and_not_where_it_went()
    {
        var t = EventExtractorTests.RunFor(EventExtractorTests.Gre("""
            { "zones": [ { "zoneId": 27, "type": "ZoneType_Graveyard" },
                         { "zoneId": 31, "type": "ZoneType_Stack" } ],
              "gameObjects": [
                { "instanceId": 700, "grpId": 5, "type": "GameObjectType_Card",
                  "ownerSeatId": 1, "controllerSeatId": 1, "zoneId": 31 } ],
              "annotations": [
                { "id": 1, "affectorId": 1, "affectedIds": [700],
                  "type": ["AnnotationType_ZoneTransfer"],
                  "details": [
                    { "key": "category", "type": "EnumValue", "valueString": ["CastSpell"] },
                    { "key": "zone_src", "type": "int32", "valueInt32": [27] },
                    { "key": "zone_dest", "type": "int32", "valueInt32": [31] } ] } ] }
            """));

        var cast = t.Events.Single(e => e.Kind == EventKind.SpellCast);
        Assert.That(cast.FromZone, Is.EqualTo("ZoneType_Graveyard"));
        Assert.That(cast.ToZone, Is.Null, "a cast's destination is always the stack");
    }

    [TestCase("ZoneType_Graveyard", "from the graveyard")]
    [TestCase("ZoneType_Exile", "from exile")]
    [TestCase("ZoneType_Library", "from the library")]
    public void A_cast_from_somewhere_other_than_hand_says_where(string zone, string said) =>
        Assert.That(Line(zone), Is.EqualTo($"You cast Tenacious Underdog {said}"));

    /// <summary>Hand is where spells come from, so saying it would be noise on 15,632 casts.</summary>
    [TestCase("ZoneType_Hand")]
    [TestCase(null)]
    public void A_cast_from_hand_says_nothing_extra(string? zone) =>
        Assert.That(Line(zone), Is.EqualTo("You cast Tenacious Underdog"));

    /// <summary>
    /// The command zone is the largest non-hand source in the archive — 1,488 casts
    /// across 715 matches — and is deliberately silent. It is where a commander lives and
    /// the only place one can be cast from, so the card's own name already says it, and a
    /// recast is explained by the "returns to the command zone" line already on the page.
    /// </summary>
    [Test]
    public void A_commander_cast_from_the_command_zone_says_nothing_extra() =>
        Assert.That(Line("ZoneType_Command"), Is.EqualTo("You cast Tenacious Underdog"));

    /// <summary>
    /// Against the target's own clause, which carries its own parenthetical statline —
    /// the source belongs to the cast, so it sits with the spell rather than trailing
    /// after what the spell was aimed at.
    /// </summary>
    [Test]
    public void The_source_sits_with_the_spell_not_after_the_target() =>
        Assert.That(Line("ZoneType_Graveyard", target: "Rabbit (1/1 → 2/2)"),
            Is.EqualTo("You cast Tenacious Underdog from the graveyard, "
                       + "targeting Rabbit (1/1 → 2/2)"));

    /// <summary>
    /// And it composes with the timing annotation from #148 rather than fighting it: a
    /// graveyard cast made during the opponent's combat says both, in that order.
    /// </summary>
    [Test]
    public void The_source_and_the_timing_both_fit_on_one_line()
    {
        var t = RendererTests.Sample(opening: false) with
        {
            Events =
            [
                new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 2, ActiveSeat = 2 },
                new GameEvent
                {
                    Seq = 1, Kind = EventKind.SpellCast, Turn = 1,
                    ActorSeat = Mine, ActiveSeat = 2,
                    Phase = 3, Step = 6,          // their combat, blockers declared
                    SourceName = "Tenacious Underdog",
                    FromZone = "ZoneType_Graveyard"
                },
            ]
        };

        Assert.That(
            Narrator.Narrate(t, Density.Beats).Select(l => l.Text)
                .Single(x => x.Contains("Tenacious Underdog", StringComparison.Ordinal)),
            Is.EqualTo("You cast Tenacious Underdog from the graveyard (declare blockers)"));
    }
}
