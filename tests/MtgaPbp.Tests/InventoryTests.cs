using System.Text.Json;
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class InventoryTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// The line Arena writes on a session hook. Cosmetics and CustomTokens are elided
    /// here; in a real log they run to tens of kilobytes, which is why nothing tries to
    /// walk the whole object.
    /// </summary>
    private const string Line = """
    { "InventoryInfo": { "SeqId": 1, "Changes": [], "Gems": 560, "Gold": 1150,
      "TotalVaultProgress": 436, "wcTrackPosition": 9, "WildCardCommons": 20,
      "WildCardUnCommons": 42, "WildCardRares": 16, "WildCardMythics": 8,
      "Boosters": [], "Vouchers": {} } }
    """;

    [Test]
    public void A_snapshot_reads_the_currencies_off_an_inventory_line()
    {
        var s = Inventory.TryRead(Json(Line));

        Assert.That(s, Is.Not.Null);
        Assert.That(s!.Gems, Is.EqualTo(560));
        Assert.That(s.Gold, Is.EqualTo(1150));
        Assert.That(s.VaultProgress, Is.EqualTo(436));
        Assert.That(s.Commons, Is.EqualTo(20));
        Assert.That(s.Uncommons, Is.EqualTo(42));
        Assert.That(s.Rares, Is.EqualTo(16));
        Assert.That(s.Mythics, Is.EqualTo(8));
    }

    /// <summary>
    /// Arena also hangs the same object off a Course payload, which is where most of
    /// them arrive. Reading only the bare line would miss those.
    /// </summary>
    [Test]
    public void A_snapshot_is_read_when_it_rides_along_with_another_payload()
    {
        var s = Inventory.TryRead(Json("""
        { "Course": { "CourseId": "x", "InternalEventName": "Brawl_Ladder" },
          "InventoryInfo": { "Gems": 710, "Gold": 600 } }
        """));

        Assert.That(s, Is.Not.Null);
        Assert.That(s!.Gems, Is.EqualTo(710));
        Assert.That(s.Gold, Is.EqualTo(600));
    }

    [Test]
    public void An_ordinary_game_line_is_not_a_snapshot() =>
        Assert.That(Inventory.TryRead(Json("""{ "greToClientEvent": { } }""")), Is.Null);
}

public class InventoryPanelTests
{
    private static MatchSummary Row() =>
        new("m1", "2026-08-24 03:00", 1, "Ladder", "Opponent", "Won 1-0", 5, false, []);

    private static InventorySnapshot Snap(int gems, int gold, int uncommons) =>
        new(DateTimeOffset.UtcNow, gems, gold, 436, 9, 20, uncommons, 16, 8);

    [Test]
    public void The_vault_panel_reports_what_the_player_holds_now()
    {
        var html = IndexRenderer.Render([Row()], inventory: [Snap(560, 1150, 42)]);

        Assert.That(html, Does.Contain("id=\"vault\""));
        Assert.That(html, Does.Contain("1,150"), "gold");
        Assert.That(html, Does.Contain("560"), "gems");
        Assert.That(html, Does.Contain("42"), "uncommon wildcards");
    }

    /// <summary>
    /// The ledger starts empty on every existing archive, because these snapshots were
    /// discarded at capture time for the whole life of the project and cannot be
    /// backfilled. An empty ledger renders no panel rather than a panel full of zeroes,
    /// which would read as "you own nothing" (#51).
    /// </summary>
    [Test]
    public void No_ledger_means_no_panel()
    {
        var html = IndexRenderer.Render([Row()]);

        Assert.That(html, Does.Not.Contain("id=\"vault\""));
    }

    /// <summary>
    /// One entry is the day-one case and has no history to show. Movement only becomes
    /// reportable once the ledger has seen the holdings change.
    /// </summary>
    [Test]
    public void Movement_is_reported_once_there_is_more_than_one_reading()
    {
        var one = IndexRenderer.Render([Row()], inventory: [Snap(710, 600, 24)]);
        Assert.That(one, Does.Not.Contain("since"), "nothing has moved yet");

        var two = IndexRenderer.Render(
            [Row()], inventory: [Snap(710, 600, 24), Snap(560, 1150, 11)]);
        Assert.That(two, Does.Contain("since"));
        Assert.That(two, Does.Contain("−13"), "thirteen uncommons spent");
        Assert.That(two, Does.Contain("+550"), "gold earned");
    }
}
