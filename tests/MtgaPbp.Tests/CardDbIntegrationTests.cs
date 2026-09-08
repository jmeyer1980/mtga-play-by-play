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

    /// <summary>
    /// The face the decklist peek shows (#99), read from the real database: cost from
    /// <c>OldSchoolManaText</c>, type line from <c>TypeTextId</c>/<c>SubtypeTextId</c>,
    /// rules text from the <c>AbilityIds</c> pairs' text loc ids.
    /// </summary>
    [Test]
    public void Real_database_builds_a_card_face_by_name()
    {
        using var db = Open();
        var face = db.FaceForName("Hare Apparent");

        Assert.That(face, Is.Not.Null);
        Assert.That(face!.ManaCost, Is.EqualTo("{1}{W}"));
        Assert.That(face.TypeLine, Is.EqualTo("Creature — Rabbit Noble"));
        Assert.That(face.RulesText, Is.Not.Empty);
        Assert.That(face.Power, Is.EqualTo("2"));
        Assert.That(face.Toughness, Is.EqualTo("2"));
    }

    /// <summary>
    /// Rules text goes through the same cleaner as every ability text on the page:
    /// the raw rows pack symbols as o-runs, and "{oT}: Add {oC}" must reach the face
    /// as "{T}: Add {C}".
    /// </summary>
    [Test]
    public void Face_rules_text_is_cleaned_of_arena_markup()
    {
        using var db = Open();
        var birds = db.FaceForName("Birds of Paradise");

        Assert.That(birds, Is.Not.Null);
        Assert.That(birds!.RulesText, Has.Some.Contains("{T}"));
        Assert.That(birds.RulesText, Has.None.Contains("{oT}"));
        Assert.That(birds.RulesText, Has.None.Contains("CARDNAME"));
    }

    [Test]
    public void A_land_face_has_no_mana_cost_and_an_unknown_name_has_no_face()
    {
        using var db = Open();
        var plains = db.FaceForName("Plains");

        Assert.That(plains, Is.Not.Null);
        Assert.That(plains!.ManaCost, Is.Empty);
        Assert.That(plains.TypeLine, Does.Contain("Land"));
        Assert.That(db.FaceForName("Definitely Not A Card Name"), Is.Null);
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

    // ---------- other faces (issue #221) ----------

    [Test]
    public void Real_database_links_an_adventure_creature_and_its_adventure_both_ways()
    {
        using var db = Open();
        var giant = db.FaceForName("Bonecrusher Giant")!;
        Assert.That(giant.OtherFaces.Select(f => f.Name), Is.EqualTo(new[] { "Stomp" }));
        Assert.That(giant.OtherFaces[0].TypeLine, Is.EqualTo("Instant — Adventure"));
        Assert.That(giant.OtherFaces[0].ManaCost, Is.EqualTo("{1}{R}"));

        var stomp = db.FaceForName("Stomp")!;
        Assert.That(stomp.OtherFaces.Select(f => f.Name), Is.EqualTo(new[] { "Bonecrusher Giant" }));
    }

    [Test]
    public void Real_database_links_a_room_to_its_doors_and_a_door_to_the_other()
    {
        using var db = Open();
        Assert.That(db.FaceForName("Dollmaker's Shop // Porcelain Gallery")!.OtherFaces.Select(f => f.Name),
            Is.EqualTo(new[] { "Dollmaker's Shop", "Porcelain Gallery" }));
        Assert.That(db.FaceForName("Dollmaker's Shop")!.OtherFaces.Select(f => f.Name),
            Is.EqualTo(new[] { "Porcelain Gallery" }));
    }

    [Test]
    public void Real_database_gives_a_plain_card_no_other_faces_and_a_spell_no_statline()
    {
        using var db = Open();
        Assert.That(db.FaceForName("Hare Apparent")!.OtherFaces, Is.Empty);

        // Power and Toughness are the empty string on the real rows, never null.
        var stomp = db.FaceForName("Stomp")!;
        Assert.That(stomp.Power, Is.Null);
        Assert.That(stomp.Toughness, Is.Null);
    }

    /// <summary>
    /// The card a face belongs to, from the real rows (#223): Stomp on the stack is a
    /// Bonecrusher Giant, Tibalt is the back of Valki, a Room's door is the Room, and
    /// The Prismatic Bridge — the face of a Brawl commander that is cast most — is
    /// Esika, God of the Tree.
    /// </summary>
    [Test]
    public void Real_database_maps_a_face_to_the_card_it_is_printed_on()
    {
        using var db = Open();
        Assert.That(db.CardForFace(70488)!.Name, Is.EqualTo("Bonecrusher Giant"));
        Assert.That(db.CardForFace(75156)!.Name, Is.EqualTo("Valki, God of Lies"));
        Assert.That(db.CardForFace(92061)!.Name, Is.EqualTo("Dollmaker's Shop // Porcelain Gallery"));
        Assert.That(db.CardForFace(75213)!.Name, Is.EqualTo("Esika, God of the Tree"));
        Assert.That(db.CardForFace(70262)!.Name, Is.EqualTo("Bonecrusher Giant"), "a card is its own card");
        Assert.That(db.CardForFace(54281)!.Name, Is.EqualTo("Mutavault"));
    }

    /// <summary>
    /// Caretaker's Talent's face, from the real rows (#230): the five lines the printed
    /// card has, each level rule once, and no CLASSLEVEL wrapper. Warlock Class is the
    /// one card whose plain row adds reminder text the wrapper leaves out, and shows
    /// that row, once.
    /// </summary>
    [Test]
    public void Real_database_class_faces_read_as_printed()
    {
        using var db = Open();
        Assert.That(db.FaceForName("Caretaker's Talent")!.RulesText, Is.EqualTo(new[]
        {
            "Whenever one or more tokens you control enter, draw a card. This ability triggers only once each turn.",
            "{W}: Level 2",
            "When this Class becomes level 2, create a token that's a copy of target token you control.",
            "{3}{W}: Level 3",
            "Creature tokens you control get +2/+2.",
        }));

        var warlock = db.FaceForName("Warlock Class")!.RulesText;
        Assert.That(warlock, Has.None.Contains("CLASSLEVEL"));
        Assert.That(warlock.Count(r => r.StartsWith("At the beginning of your end step, each opponent loses life equal", StringComparison.Ordinal)),
            Is.EqualTo(1));
    }
}
