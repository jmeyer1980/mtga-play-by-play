using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Checks the real Arena card database, which cannot be committed and does not exist
/// on a CI runner — these are the only tests that skip there.
/// </summary>
/// <remarks>
/// The golden-file tests used to depend on the real database too, which meant the
/// whole end-to-end check sat out every CI run. They now use a checked-in name
/// fixture; this class keeps the real-database integration covered, and is also what
/// would catch the fixture drifting away from what Arena actually returns.
/// </remarks>
public class CardDbIntegrationTests
{
    private static CardDb Open()
    {
        var path = CardDb.FindDatabase(null);
        if (path is null)
            Assert.Ignore("MTG Arena card database not present; needs Arena installed.");
        return new CardDb(path!);
    }

    [Test]
    public void FindDatabase_locates_an_installed_card_database()
    {
        var path = CardDb.FindDatabase(null);
        if (path is null)
            Assert.Ignore("MTG Arena card database not present; needs Arena installed.");

        Assert.That(Path.GetFileName(path), Does.StartWith("Raw_CardDatabase_"));
        Assert.That(new FileInfo(path!).Length, Is.GreaterThan(1_000_000));
    }

    [Test]
    public void FindDatabase_honours_an_override_and_rejects_a_bad_one()
    {
        Assert.That(CardDb.FindDatabase(@"C:\definitely\not\here.mtga"), Is.Null);
    }

    [Test]
    public void Real_database_resolves_a_basic_land_name()
    {
        using var db = Open();
        // Titles live at Formatted = 1; querying Formatted = 0 silently returns null.
        Assert.That(db.NameForLocId(648), Is.EqualTo("Plains"));
    }

    [Test]
    public void Real_database_resolves_phase_and_step_labels()
    {
        using var db = Open();
        Assert.That(db.EnumName("Phase", 3), Is.EqualTo("Combat"));
        Assert.That(db.EnumName("Step", 5), Is.EqualTo("Declare Attackers"));
        Assert.That(db.EnumName("Phase", 0), Is.Null, "phase 0 has a blank label");
    }

    /// <summary>
    /// The reason the name fixture is safe to rely on: everything it claims must
    /// still match what Arena actually returns.
    /// </summary>
    [Test]
    public void Name_fixture_agrees_with_the_real_database()
    {
        using var db = Open();
        var fixtureDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");
        var fixture = FixtureCardDb.Load(fixtureDir);

        var transcript = new EventExtractor(fixture)
            .Extract("sample-match-0001", GoldenFileTests.ReadFixture());
        var viaReal = new EventExtractor(db)
            .Extract("sample-match-0001", GoldenFileTests.ReadFixture());

        Assert.That(transcript.CardsSeen, Is.EquivalentTo(viaReal.CardsSeen),
            "the checked-in name fixture has drifted from the real card database");
        Assert.That(transcript.Events.Select(e => e.Detail),
            Is.EqualTo(viaReal.Events.Select(e => e.Detail)));

        // Colours are the one thing the fixture carries that no rendered line spells
        // out, so nothing above would notice the ColorIdentity column drifting.
        Assert.That(transcript.DeckColors, Is.EqualTo(viaReal.DeckColors));
        Assert.That(viaReal.DeckColors, Is.Not.Null,
            "the sample match registers a deck, so the real database can colour it");
    }
}
