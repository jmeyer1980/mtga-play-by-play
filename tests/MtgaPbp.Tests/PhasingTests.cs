using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// A permanent leaving play without leaving the battlefield (#125). Phasing is not a zone
/// change — Arena announces it by annotation and the object keeps its zone — so the board
/// lines went on printing a creature that could not block and could not be targeted.
/// </summary>
public class PhasingTests
{
    /// <summary>A creature standing on the battlefield under the opponent.</summary>
    private static string Creature(int instance, int locId) => $$"""
        { "instanceId": {{instance}}, "grpId": 1, "name": {{locId}},
          "controllerSeatId": 2, "zoneId": 28, "cardTypes": [ "CardType_Creature" ],
          "power": 2, "toughness": 2 }
        """;

    private static string Phasing(int id, int affected, bool out_) => $$"""
        { "id": {{id}}, "affectorId": 600, "affectedIds": [{{affected}}],
          "type": ["AnnotationType_{{(out_ ? "PhasedOut" : "PhasedIn")}}"] }
        """;

    /// <summary>A turn boundary, which is what makes the board be snapshotted.</summary>
    private static string Turn(int n, int seat, string objects, string annotations) =>
        EventExtractorTests.Gre($$"""
            { "type": "GameStateType_Full",
              "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" } ],
              "gameObjects": [ {{objects}} ],
              "players": [ { "systemSeatNumber": 1, "lifeTotal": 20 },
                           { "systemSeatNumber": 2, "lifeTotal": 20 } ],
              "turnInfo": { "turnNumber": {{n}}, "activePlayer": {{seat}}, "phase": 2 },
              "annotations": [
                { "id": {{900 + n}}, "affectorId": {{seat}}, "affectedIds": [{{seat}}],
                  "type": ["AnnotationType_NewTurnStarted"] }{{annotations}} ] }
            """);

    private static List<string> Narrate(params string[] messages) =>
        Narrator.Narrate(EventExtractorTests.RunFor(messages), Density.Beats)
            .Select(l => l.Text).ToList();

    private static List<string> Boards(IEnumerable<string> lines) =>
        lines.Where(l => l.Contains("control", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// The bug: a phased-out creature kept its battlefield zone, so every board line —
    /// including the final snapshot — reported it standing there.
    /// </summary>
    [Test]
    public void A_phased_out_creature_leaves_the_board_lines()
    {
        var lines = Narrate(
            Turn(1, 1, Creature(50, 1000), ""),
            Turn(2, 2, Creature(50, 1000), $", {Phasing(1, 50, out_: true)}"),
            Turn(3, 1, Creature(50, 1000), ""));

        Assert.That(lines, Does.Contain("Lightning Bolt phases out"));
        Assert.That(Boards(lines).Count(b => b.Contains("Lightning Bolt", StringComparison.Ordinal)),
            Is.EqualTo(1),
            "it stands on the board before it phases out, and on no board after");
    }

    /// <summary>
    /// And it is standing there again once it phases back in. Asserted with a second
    /// creature beside it, because a board line is only reprinted when it has changed —
    /// one creature phasing out and back leaves an unchanged board either side, and a
    /// board that emptied entirely is skipped rather than printed as empty.
    /// </summary>
    [Test]
    public void A_creature_that_phases_back_in_returns_to_the_board_lines()
    {
        var both = $"{Creature(50, 1000)}, {Creature(51, 1001)}";
        var lines = Narrate(
            Turn(1, 1, both, ""),
            Turn(2, 2, both, $", {Phasing(1, 50, out_: true)}"),
            Turn(3, 1, both, $", {Phasing(2, 50, out_: false)}"),
            Turn(4, 2, both, ""));

        Assert.That(lines, Does.Contain("Lightning Bolt phases out"));
        Assert.That(lines, Does.Contain("Lightning Bolt phases in"));

        var boards = Boards(lines);
        Assert.That(boards, Has.Some.Contains("Llanowar Elves").And.Some.Not.Contains("Lightning Bolt"),
            "while it is phased out the board shows only what is still there");
        Assert.That(boards.Count(x => x.Contains("Lightning Bolt", StringComparison.Ordinal)),
            Is.GreaterThan(1), "and it is back on a board once it phases in");
    }

    /// <summary>
    /// One Teferi's Protection phasing a whole board is one thing that happened. Arena
    /// sends an annotation per permanent, all in the same message, so they fold the way
    /// a mass control change does — and the verb has to agree with the list.
    /// </summary>
    [Test]
    public void A_whole_board_phasing_out_reads_as_one_sentence()
    {
        var lines = Narrate(
            Turn(1, 1, $"{Creature(50, 1000)}, {Creature(51, 1000)}, {Creature(52, 1001)}", ""),
            Turn(2, 2, $"{Creature(50, 1000)}, {Creature(51, 1000)}, {Creature(52, 1001)}",
                 $", {Phasing(1, 50, true)}, {Phasing(2, 51, true)}, {Phasing(3, 52, true)}"));

        Assert.That(lines, Does.Contain("Lightning Bolt ×2, Llanowar Elves phase out"));
    }

    /// <summary>
    /// "phases out" for one and "phase out" for several — the fold puts a list behind
    /// one verb and English will not let both readings share it.
    /// </summary>
    [Test]
    public void The_verb_agrees_with_how_many_phased()
    {
        var one = Narrate(
            Turn(1, 1, Creature(50, 1000), ""),
            Turn(2, 2, Creature(50, 1000), $", {Phasing(1, 50, true)}"));

        Assert.That(one, Does.Contain("Lightning Bolt phases out"));
        Assert.That(one, Has.None.EqualTo("Lightning Bolt phase out"));
    }
}
