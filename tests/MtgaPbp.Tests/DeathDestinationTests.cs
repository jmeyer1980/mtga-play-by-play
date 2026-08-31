using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Where a permanent actually went when it died (#126). "is put into the graveyard",
/// "destroys X" and "sacrifices X" all assert a burial, and a replacement effect turning
/// a death into an exile made every later graveyard interaction stop making sense with
/// nothing on the page to explain it.
/// </summary>
public class DeathDestinationTests
{
    private const int Mine = 1;

    private static string Line(EventKind kind, string? toZone, string? cause = null) =>
        Narrator.Narrate(
                RendererTests.Sample(opening: false) with
                {
                    Events =
                    [
                        new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = Mine },
                        new GameEvent
                        {
                            Seq = 1, Kind = kind, Turn = 1, ActorSeat = Mine,
                            SourceName = "Hare Apparent", CauseName = cause, ToZone = toZone
                        },
                    ]
                }, Density.Beats)
            .Select(l => l.Text)
            .Single(x => x.Contains("Hare Apparent", StringComparison.Ordinal));

    /// <summary>
    /// 92 of these across 71 matches, one of them an opponent's commander — the
    /// difference between gone and recurrable.
    /// </summary>
    [Test]
    public void A_creature_that_died_to_damage_and_was_exiled_says_exiled() =>
        Assert.That(Line(EventKind.StateBasedAction, "ZoneType_Exile"),
            Is.EqualTo("Hare Apparent is exiled"));

    /// <summary>
    /// The graveyard stays the default, including when the log named no destination —
    /// which is where every one of the archive's 6,090 state-based deaths but 97 went.
    /// </summary>
    [TestCase("ZoneType_Graveyard")]
    [TestCase(null)]
    public void A_state_based_death_still_reads_as_a_burial_by_default(string? zone) =>
        Assert.That(Line(EventKind.StateBasedAction, zone),
            Is.EqualTo("Hare Apparent is put into the graveyard"));

    /// <summary>The commander's trip home, which #18 added and this must not disturb.</summary>
    [Test]
    public void A_commander_still_returns_to_the_command_zone() =>
        Assert.That(Line(EventKind.StateBasedAction, "ZoneType_Command"),
            Is.EqualTo("Hare Apparent returns to the command zone"));

    /// <summary>
    /// "destroys X" names the act and leaves the outcome to a reader who takes it as a
    /// burial, so the outcome has to be said when it was not one.
    /// </summary>
    [Test]
    public void A_destroy_replaced_by_exile_says_so() =>
        Assert.That(Line(EventKind.Destroyed, "ZoneType_Exile", cause: "Bilbo's Deadly Slice"),
            Is.EqualTo("Bilbo's Deadly Slice destroys Hare Apparent — exiled instead"));

    /// <summary>And a sacrifice the same way.</summary>
    [Test]
    public void A_sacrifice_replaced_by_exile_says_so() =>
        Assert.That(Line(EventKind.Sacrificed, "ZoneType_Exile"),
            Is.EqualTo("You sacrifice Hare Apparent — exiled instead"));

    /// <summary>
    /// Only the graveyard is silent, because only the graveyard is what the verb already
    /// implies. 1,653 destroys and 1,690 sacrifices in the archive end there.
    /// </summary>
    [TestCase(EventKind.Destroyed, "ZoneType_Graveyard")]
    [TestCase(EventKind.Destroyed, null)]
    [TestCase(EventKind.Sacrificed, "ZoneType_Graveyard")]
    [TestCase(EventKind.Sacrificed, null)]
    public void A_death_that_ends_in_the_graveyard_adds_nothing(EventKind kind, string? zone) =>
        Assert.That(Line(kind, zone), Does.Not.Contain("instead"));
}
