using Microsoft.Data.Sqlite;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// How <see cref="CardDb.FaceForName"/> finds a card's other faces (#221), on a
/// database small enough to read: the columns the real one has, the handful of rows
/// each shape needs, and nothing else.
/// </summary>
/// <remarks>
/// The shapes come from the shipped database, checked 2026-09-08: an Adventure
/// creature and its Adventure link straight to each other; a Room's doors each link
/// to a row for the whole card — titled "A // B", the name Arena's decklist uses —
/// which links to both doors; a prototype card links to a second row with its own
/// title. The real database's own answers are in <see cref="CardDbIntegrationTests"/>,
/// which skips without Arena; this file runs everywhere.
/// </remarks>
public class LinkedFaceDbTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"carddb_{Guid.NewGuid():N}.sqlite");
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        Exec(con, @"CREATE TABLE Cards (GrpId INT PRIMARY KEY, TitleId INT, IsToken BOOLEAN,
                                        IsPrimaryCard BOOLEAN, OldSchoolManaText TEXT,
                                        TypeTextId INT, SubtypeTextId INT, AbilityIds TEXT,
                                        Power TEXT, Toughness TEXT, LinkedFaceGrpIds TEXT,
                                        Types TEXT, ColorIdentity TEXT)");
        Exec(con, @"CREATE TABLE Localizations_enUS (LocId INT, Formatted INT, Loc TEXT,
                                                     PRIMARY KEY (LocId, Formatted))");

        // Type words, shared.
        Loc(con, 10, "Creature"); Loc(con, 11, "Giant");
        Loc(con, 12, "Instant"); Loc(con, 13, "Adventure");
        Loc(con, 14, "Enchantment"); Loc(con, 15, "Room");
        Loc(con, 16, "Basic Land"); Loc(con, 17, "Plains");
        Loc(con, 18, "Artifact Creature"); Loc(con, 19, "Assembly-Worker");

        // An Adventure creature and its Adventure, two printings each: the primary
        // printing answers, and its link is the one followed.
        Loc(con, 100, "Bonecrusher Giant"); Loc(con, 101, "Stomp");
        Loc(con, 102, "CARDNAME deals 2 damage to any target.");
        Card(con, 70262, 100, primary: true, "o2oR", 10, 11, "", "4", "3", "70488");
        Card(con, 70488, 101, primary: false, "o1oR", 12, 13, "1:102", "", "", "70262");
        Card(con, 73724, 100, primary: false, "o2oR", 10, 11, "", "4", "3", "73725");
        Card(con, 73725, 101, primary: false, "o1oR", 12, 13, "1:102", "", "", "73724");

        // A Room: the whole card, and a door each side of it.
        Loc(con, 200, "Dollmaker's Shop // Porcelain Gallery");
        Loc(con, 201, "Dollmaker's Shop"); Loc(con, 202, "Porcelain Gallery");
        Card(con, 92060, 200, primary: true, "o1oWo4oWoW", 14, 15, "", "", "", "92061,92062");
        Card(con, 92061, 201, primary: false, "o1oW", 14, 15, "", "", "", "92060");
        Card(con, 92062, 202, primary: false, "o4oWoW", 14, 15, "", "", "", "92060");

        // A prototype card: two rows, one title.
        Loc(con, 300, "Autonomous Assembler");
        Card(con, 82518, 300, primary: true, "o5", 18, 19, "", "4", "4", "83682");
        Card(con, 83682, 300, primary: false, "o1oW", 18, 19, "", "2", "2", "82518");

        // A plain card, a card whose link goes nowhere, and a token that shares a
        // real card's name.
        Loc(con, 400, "Plains");
        Card(con, 96179, 400, primary: true, "", 16, 17, "", "", "", "");
        Loc(con, 500, "Lonely Half");
        Card(con, 55555, 500, primary: true, "o1", 12, 0, "", "", "", "999999");
        Card(con, 91843, 100, primary: false, "", 10, 11, "", "4", "3", "", token: true);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Titles live at Formatted = 1 in the real database.
    private static void Loc(SqliteConnection con, int id, string text) =>
        Exec(con, $"INSERT INTO Localizations_enUS VALUES ({id}, 1, '{text.Replace("'", "''")}')");

    private static void Card(SqliteConnection con, int grpId, int titleId, bool primary,
        string mana, int typeId, int subtypeId, string abilities, string power, string toughness,
        string linked, bool token = false) =>
        Exec(con, $"INSERT INTO Cards VALUES ({grpId}, {titleId}, {(token ? 1 : 0)}, {(primary ? 1 : 0)}, " +
                  $"'{mana}', {typeId}, {subtypeId}, '{abilities}', '{power}', '{toughness}', '{linked}', '2', '')");

    private static string[] Names(CardFace face) => face.OtherFaces.Select(f => f.Name).ToArray();

    [Test]
    public void An_adventure_creature_carries_its_adventure()
    {
        using var db = new CardDb(_dbPath);
        var giant = db.FaceForName("Bonecrusher Giant")!;

        Assert.That(Names(giant), Is.EqualTo(new[] { "Stomp" }));
        var stomp = giant.OtherFaces[0];
        Assert.That(stomp.ManaCost, Is.EqualTo("{1}{R}"));
        Assert.That(stomp.TypeLine, Is.EqualTo("Instant — Adventure"));
        Assert.That(stomp.RulesText, Is.EqualTo(new[] { "Stomp deals 2 damage to any target." }),
            "CARDNAME resolves to the face's own name, not the creature's");
    }

    [Test]
    public void The_adventure_carries_its_creature_back()
    {
        using var db = new CardDb(_dbPath);
        var stomp = db.FaceForName("Stomp")!;

        Assert.That(Names(stomp), Is.EqualTo(new[] { "Bonecrusher Giant" }));
        Assert.That(stomp.OtherFaces[0].Power, Is.EqualTo("4"));
        Assert.That(stomp.OtherFaces[0].Toughness, Is.EqualTo("3"));
    }

    /// <summary>
    /// One hop. A face reached through another face is the card the reader started
    /// from, and offering the way back would nest the deck list inside itself.
    /// </summary>
    [Test]
    public void A_face_reached_through_another_carries_no_links_of_its_own()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.FaceForName("Bonecrusher Giant")!.OtherFaces[0].OtherFaces, Is.Empty);
        Assert.That(db.FaceForName("Stomp")!.OtherFaces[0].OtherFaces, Is.Empty);
    }

    [Test]
    public void A_room_carries_both_doors()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(Names(db.FaceForName("Dollmaker's Shop // Porcelain Gallery")!),
            Is.EqualTo(new[] { "Dollmaker's Shop", "Porcelain Gallery" }));
    }

    /// <summary>
    /// A door links to the whole card, and the whole card is the list entry's own
    /// shape, not a face of it: what a door carries is the other door.
    /// </summary>
    [Test]
    public void A_door_carries_the_other_door_and_never_the_whole_card()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(Names(db.FaceForName("Dollmaker's Shop")!), Is.EqualTo(new[] { "Porcelain Gallery" }));
        Assert.That(Names(db.FaceForName("Porcelain Gallery")!), Is.EqualTo(new[] { "Dollmaker's Shop" }));
    }

    [Test]
    public void A_prototype_row_with_the_same_name_is_not_another_face()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.FaceForName("Autonomous Assembler")!.OtherFaces, Is.Empty);
    }

    [Test]
    public void A_plain_card_and_a_dangling_link_have_no_other_faces()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.FaceForName("Plains")!.OtherFaces, Is.Empty);
        Assert.That(db.FaceForName("Lonely Half")!.OtherFaces, Is.Empty);
    }

    /// <summary>
    /// The database writes the empty string, not null, where a card has no power or
    /// toughness — and the face must not carry that forward as a statline, or every
    /// instant, sorcery and enchantment peek ends in a bare "/". Measured on the real
    /// archive 2026-09-08: 1,501 of 1,516 pages carried one.
    /// </summary>
    [Test]
    public void An_empty_power_or_toughness_is_no_statline()
    {
        using var db = new CardDb(_dbPath);
        var stomp = db.FaceForName("Stomp")!;
        Assert.That(stomp.Power, Is.Null);
        Assert.That(stomp.Toughness, Is.Null);

        var giant = db.FaceForName("Bonecrusher Giant")!;
        Assert.That(giant.Power, Is.EqualTo("4"));
        Assert.That(giant.Toughness, Is.EqualTo("3"));
    }

    // ---------- the card a face belongs to (issue #223) ----------

    /// <summary>
    /// A game object on the stack as an Adventure carries the Adventure's own grpId;
    /// the card the opponent owns is the creature. The reprint's Adventure row links
    /// to the reprint's creature row, whose title is a card's title all the same.
    /// </summary>
    [Test]
    public void An_adventure_face_belongs_to_its_creature()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.CardForFace(70488)!.Name, Is.EqualTo("Bonecrusher Giant"));
        Assert.That(db.CardForFace(73725)!.Name, Is.EqualTo("Bonecrusher Giant"), "reprint");
        Assert.That(db.CardForFace(70262)!.GrpId, Is.EqualTo(70262), "the creature is already the card");
    }

    [Test]
    public void A_door_belongs_to_the_whole_room()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.CardForFace(92061)!.Name, Is.EqualTo("Dollmaker's Shop // Porcelain Gallery"));
        Assert.That(db.CardForFace(92062)!.Name, Is.EqualTo("Dollmaker's Shop // Porcelain Gallery"));
        Assert.That(db.CardForFace(92060)!.GrpId, Is.EqualTo(92060));
    }

    /// <summary>
    /// A prototype card's second row carries the card's own title, and a card with a
    /// link to nowhere, a plain card, a token and an unknown id all answer as
    /// <c>CardForGrpId</c> does: the fallback is the row itself.
    /// </summary>
    [Test]
    public void Everything_that_is_not_a_face_is_its_own_card()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.CardForFace(83682)!.Name, Is.EqualTo("Autonomous Assembler"));
        Assert.That(db.CardForFace(83682)!.GrpId, Is.EqualTo(83682));
        Assert.That(db.CardForFace(55555)!.Name, Is.EqualTo("Lonely Half"));
        Assert.That(db.CardForFace(96179)!.Name, Is.EqualTo("Plains"));
        Assert.That(db.CardForFace(91843)!.IsToken, Is.True);
        Assert.That(db.CardForFace(999999), Is.Null);
    }

    [Test]
    public void The_face_and_its_other_faces_are_cached_together()
    {
        using var db = new CardDb(_dbPath);
        var first = db.FaceForName("Bonecrusher Giant");
        Assert.That(db.FaceForName("Bonecrusher Giant"), Is.SameAs(first));
    }
}
