using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// A permanent changing hands, which the transcript used to apply and never mention
/// (#124). Every line after one of these is about a creature the other player now
/// controls, so without it the board snapshot moving a creature across, and the opponent
/// attacking with it, both read as the parser losing track of it.
/// </summary>
public class ControlChangeTests
{
    /// <summary>
    /// A permanent on the battlefield, already under its new controller — the state the
    /// tracker is in when the annotation is read, because <c>Apply</c> runs over a
    /// message's objects before its annotations.
    /// </summary>
    private static string Stolen(int instance, int grp, int owner, int controller) => $$"""
        { "instanceId": {{instance}}, "grpId": {{grp}}, "type": "GameObjectType_Card",
          "ownerSeatId": {{owner}}, "controllerSeatId": {{controller}}, "zoneId": 28 }
        """;

    /// <summary>One ControllerChanged annotation.</summary>
    private static string Change(int id, int affector, int affected) => $$"""
        { "id": {{id}}, "affectorId": {{affector}}, "affectedIds": [{{affected}}],
          "type": ["AnnotationType_ControllerChanged"] }
        """;

    /// <summary>
    /// One message: a battlefield, the permanents on it, and whichever annotation
    /// surfaces are under test. Both are passed as array contents rather than whole
    /// keys, so a test never has to open a string literal with a quote.
    /// </summary>
    private static string Message(string objects, string? streamed = null, string? persistent = null)
    {
        var parts = new List<string>();
        if (streamed is not null) parts.Add($"\"annotations\": [ {streamed} ]");
        if (persistent is not null) parts.Add($"\"persistentAnnotations\": [ {persistent} ]");

        return EventExtractorTests.Gre($$"""
            { "zones": [ { "zoneId": 28, "type": "ZoneType_Battlefield" } ],
              "gameObjects": [ {{objects}} ],
              {{string.Join(", ", parts)}} }
            """);
    }

    private static List<string> Narrate(params string[] messages) =>
        Narrator.Narrate(EventExtractorTests.RunFor(messages), Density.Beats)
            .Select(l => l.Text).ToList();

    private static int Mentions(IEnumerable<string> lines) =>
        lines.Count(l => l.Contains("control of", StringComparison.Ordinal));

    /// <summary>The ordinary case: the streamed annotation names what moved.</summary>
    [Test]
    public void A_stolen_permanent_is_reported_under_its_new_controller()
    {
        var msg = Message(Stolen(500, 5, owner: 1, controller: 2),
                          streamed: Change(1, affector: 600, affected: 500));

        Assert.That(Narrate(msg), Does.Contain("Opponent gains control of Llanowar Elves"));
    }

    /// <summary>
    /// Seven of the archive's twenty-three matches carry the change ONLY as a persistent
    /// annotation — a surface that was inventory-only — and one of those is an opponent
    /// taking the player's commander. Reading the streamed surface alone left them silent.
    /// </summary>
    [Test]
    public void A_change_that_only_arrives_as_a_persistent_annotation_is_still_reported()
    {
        var msg = Message(Stolen(500, 5, owner: 1, controller: 2),
                          persistent: Change(1, affector: 500, affected: 500));

        Assert.That(Narrate(msg), Does.Contain("Opponent gains control of Llanowar Elves"));
    }

    /// <summary>
    /// Both surfaces describe the same theft and describe it differently — the streamed
    /// one names the effect as the cause, the persistent one names the permanent itself,
    /// and their ids differ — so they cannot be matched on identity. Told once.
    /// </summary>
    [Test]
    public void The_same_change_on_both_surfaces_is_reported_once()
    {
        var msg = Message(Stolen(500, 5, owner: 1, controller: 2),
                          streamed: Change(3, affector: 600, affected: 500),
                          persistent: Change(1, affector: 500, affected: 500));

        Assert.That(Mentions(Narrate(msg)), Is.EqualTo(1));
    }

    /// <summary>
    /// A persistent marker is a standing fact, re-sent for as long as the effect lasts.
    /// Telling it per re-send would report one theft every few seconds.
    /// </summary>
    [Test]
    public void A_persistent_marker_repeated_across_messages_is_reported_once()
    {
        var msg = Message(Stolen(500, 5, owner: 1, controller: 2),
                          persistent: Change(1, affector: 500, affected: 500));

        Assert.That(Mentions(Narrate(msg, msg, msg)), Is.EqualTo(1));
    }

    /// <summary>
    /// One effect taking several permanents is one thing that happened. Arena sends an
    /// annotation each, all in the same message and therefore all at once, and printing
    /// them apart produced a stutter that named the same creature twice in four lines.
    /// </summary>
    [Test]
    public void Several_permanents_taken_at_once_read_as_one_sentence()
    {
        var msg = Message(
            $"{Stolen(500, 5, 1, 2)}, {Stolen(501, 5, 1, 2)}, {Stolen(502, 41, 1, 2)}",
            streamed: $"{Change(1, 600, 500)}, {Change(2, 600, 501)}, {Change(3, 600, 502)}");

        Assert.That(Narrate(msg),
            Does.Contain("Opponent gains control of Llanowar Elves ×2, Lembas"));
    }

    /// <summary>
    /// Two effects trading permanents in opposite directions are two things happening.
    /// One sentence claiming both would name the wrong player for half of it.
    /// </summary>
    [Test]
    public void Changes_in_opposite_directions_are_not_folded_together()
    {
        var msg = Message(
            $"{Stolen(500, 5, 1, 2)}, {Stolen(501, 41, 2, 1)}",
            streamed: $"{Change(1, 600, 500)}, {Change(2, 601, 501)}");

        var lines = Narrate(msg);
        Assert.That(lines, Does.Contain("Opponent gains control of Llanowar Elves"));
        Assert.That(lines, Does.Contain("You gain control of Lembas"));
    }
}
