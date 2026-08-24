using System.Text.Json;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class InventoryLedgerTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() =>
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"inv_{Guid.NewGuid():N}")).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private static JsonElement Line(int gems, int gold = 0, int uncommons = 0) =>
        JsonDocument.Parse(
            "{\"InventoryInfo\":{\"Gems\":" + gems +
            ",\"Gold\":" + gold +
            ",\"WildCardUnCommons\":" + uncommons + "}}").RootElement.Clone();

    /// <summary>Runs one capture pass over the given lines and says whether it appended.</summary>
    private bool Capture(params JsonElement[] lines)
    {
        var ledger = new InventoryLedger(_root);
        foreach (var l in lines) ledger.Observe(l);
        return ledger.Commit();
    }

    private static IReadOnlyList<InventorySnapshot> Stored(string root) =>
        new InventoryLedger(root).Entries;

    [Test]
    public void The_first_capture_records_what_the_player_holds()
    {
        Assert.That(Capture(Line(560, 1150, 42)), Is.True);

        var e = Stored(_root);
        Assert.That(e, Has.Count.EqualTo(1));
        Assert.That(e[0].Gems, Is.EqualTo(560));
        Assert.That(e[0].Gold, Is.EqualTo(1150));
        Assert.That(e[0].Uncommons, Is.EqualTo(42));
    }

    /// <summary>
    /// Every capture re-reads the whole log from the start, so the same snapshots arrive
    /// over and over. A ledger that appended on every sighting would grow without
    /// anything having happened.
    /// </summary>
    [Test]
    public void Capturing_the_same_log_twice_records_nothing_the_second_time()
    {
        Capture(Line(560, 1150, 42));

        Assert.That(Capture(Line(560, 1150, 42)), Is.False);
        Assert.That(Stored(_root), Has.Count.EqualTo(1));
    }

    [Test]
    public void A_change_in_holdings_is_appended()
    {
        Capture(Line(710, 1150, 42));
        Assert.That(Capture(Line(560, 1150, 42)), Is.True);

        Assert.That(Stored(_root).Select(x => x.Gems), Is.EqualTo(new[] { 710, 560 }));
    }

    /// <summary>
    /// The case that rules out comparing every sighting against the stored tail. A log
    /// holding 710 → 560 → 710 replays in full on the next capture, and a per-sighting
    /// rule would append 560 again each time. Only where the log leaves the player is
    /// recorded, so a re-read is a no-op.
    /// </summary>
    [Test]
    public void A_log_that_moves_and_moves_back_records_only_where_it_ended()
    {
        Assert.That(Capture(Line(710), Line(560), Line(710)), Is.True);
        Assert.That(Stored(_root).Select(x => x.Gems), Is.EqualTo(new[] { 710 }));

        Assert.That(Capture(Line(710), Line(560), Line(710)), Is.False,
            "re-reading the same log must not append");
        Assert.That(Stored(_root), Has.Count.EqualTo(1));
    }

    [Test]
    public void A_capture_that_saw_no_inventory_line_records_nothing()
    {
        Capture(Line(560));
        Assert.That(Capture(JsonDocument.Parse("""{"greToClientEvent":{}}""").RootElement.Clone()),
            Is.False);
        Assert.That(Stored(_root), Has.Count.EqualTo(1));
    }
}
