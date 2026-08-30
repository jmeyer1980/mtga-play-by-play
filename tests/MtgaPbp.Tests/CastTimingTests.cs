using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The beats view said <em>that</em> a spell was cast and never <em>when</em> in the
/// turn, so a trick held for blockers read exactly like one fired in the second main
/// (#148).
/// </summary>
public class CastTimingTests
{
    // Phase and Step numbering as Arena's card database defines it — Phase 2/4 are the
    // main phases, Step 5 is Declare Attackers. See Narrator's own note.
    private const int Beginning = 1, FirstMain = 2, Combat = 3, SecondMain = 4;
    private const int Upkeep = 2, DeclareAttackers = 5, DeclareBlockers = 6;

    private const int Mine = 1, Theirs = 2;

    /// <summary>One cast, placed wherever the test needs it.</summary>
    private static string Cast(int actor, int active, int phase, int step,
                               Density density = Density.Beats)
    {
        var t = RendererTests.Sample(opening: false) with
        {
            Events =
            [
                new GameEvent
                {
                    Seq = 0, Kind = EventKind.TurnStart, Turn = 1,
                    ActorSeat = active, ActiveSeat = active
                },
                new GameEvent
                {
                    Seq = 1, Kind = EventKind.SpellCast, Turn = 1,
                    ActorSeat = actor, ActiveSeat = active,
                    Phase = phase, Step = step,
                    SourceName = "Settle the Wreckage"
                },
            ]
        };

        return Narrator.Narrate(t, density)
            .Select(l => l.Text)
            .Single(x => x.Contains("Settle the Wreckage", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ordinary case, which is most casts: your own turn, a main phase. Annotating
    /// these would bury the handful that carry information.
    /// </summary>
    [TestCase(FirstMain)]
    [TestCase(SecondMain)]
    public void A_cast_in_your_own_main_phase_says_nothing_extra(int phase)
    {
        Assert.That(Cast(Mine, Mine, phase, step: 0),
            Is.EqualTo("You cast Settle the Wreckage"));
    }

    /// <summary>
    /// The worked example from the issue: Settle held until attackers were declared.
    /// </summary>
    [Test]
    public void A_cast_after_attackers_are_declared_names_the_step()
    {
        Assert.That(Cast(Mine, Theirs, Combat, DeclareAttackers),
            Is.EqualTo("You cast Settle the Wreckage (declare attackers)"));
    }

    /// <summary>
    /// The distinction the issue is about: these two rendered identically before, and
    /// they are different plays.
    /// </summary>
    [Test]
    public void Before_attacks_and_after_blockers_no_longer_read_alike()
    {
        Assert.That(Cast(Mine, Theirs, Beginning, Upkeep),
            Is.Not.EqualTo(Cast(Mine, Theirs, Combat, DeclareBlockers)));
    }

    /// <summary>
    /// Your own turn is not enough on its own — a spell cast in your combat is as much
    /// a timing decision as one cast in theirs.
    /// </summary>
    [Test]
    public void Your_own_combat_still_counts()
    {
        Assert.That(Cast(Mine, Mine, Combat, DeclareBlockers),
            Does.EndWith("(declare blockers)"));
    }

    /// <summary>
    /// And a main phase that is not yours is not your main phase, even though the phase
    /// number says main — which is why the test is turn-holding AND phase, not phase.
    /// </summary>
    [Test]
    public void Their_main_phase_is_not_your_main_phase()
    {
        Assert.That(Cast(Mine, Theirs, FirstMain, step: 0),
            Is.EqualTo("You cast Settle the Wreckage (first main phase)"));
    }

    /// <summary>
    /// Verbose prints the step transitions as lines of their own, so this would be
    /// saying the same thing twice on the density that least needs help.
    /// </summary>
    [Test]
    public void Verbose_is_left_alone()
    {
        Assert.That(Cast(Mine, Theirs, Combat, DeclareAttackers, Density.Verbose),
            Is.EqualTo("You cast Settle the Wreckage"));
    }

    /// <summary>
    /// A part of the turn the log did not name says nothing, rather than naming it by a
    /// number nobody recognises.
    /// </summary>
    [Test]
    public void An_unknown_part_of_the_turn_says_nothing()
    {
        Assert.That(Cast(Mine, Theirs, phase: 0, step: 0),
            Is.EqualTo("You cast Settle the Wreckage"));
    }

    /// <summary>
    /// A target brings its own parenthetical along — "targeting Bristly Bill
    /// (2/2 → 0/0)" — so the timing goes against the spell's name. Appended at the end
    /// instead, the two run together and read as one confused aside.
    /// </summary>
    [Test]
    public void The_timing_sits_against_the_spell_not_after_the_target()
    {
        var t = RendererTests.Sample(opening: false) with
        {
            Events =
            [
                new GameEvent
                {
                    Seq = 0, Kind = EventKind.TurnStart, Turn = 1,
                    ActorSeat = Theirs, ActiveSeat = Theirs
                },
                new GameEvent
                {
                    Seq = 1, Kind = EventKind.SpellCast, Turn = 1,
                    ActorSeat = Mine, ActiveSeat = Theirs,
                    Phase = Combat, Step = DeclareBlockers,
                    SourceName = "Bleeding Edge",
                    TargetName = "Bristly Bill, Spine Sower (2/2 → 0/0)"
                },
            ]
        };

        var line = Narrator.Narrate(t, Density.Beats)
            .Select(l => l.Text)
            .Single(x => x.Contains("Bleeding Edge", StringComparison.Ordinal));

        Assert.That(line, Is.EqualTo(
            "You cast Bleeding Edge (declare blockers), targeting "
            + "Bristly Bill, Spine Sower (2/2 → 0/0)"));
    }

    /// <summary>The opponent's casts are marked the same way — it is their play too.</summary>
    [Test]
    public void The_opponents_tricks_are_marked_as_well()
    {
        Assert.That(Cast(Theirs, Mine, Combat, DeclareBlockers),
            Is.EqualTo("Opponent casts Settle the Wreckage (declare blockers)"));
    }
}
