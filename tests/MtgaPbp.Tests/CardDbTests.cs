using Microsoft.Data.Sqlite;
using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class CardDbTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"carddb_{Guid.NewGuid():N}.sqlite");
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        Exec(con, @"CREATE TABLE Cards (GrpId INT PRIMARY KEY, TitleId INT, Types TEXT,
                                        Power TEXT, Toughness TEXT, IsToken BOOLEAN,
                                        ColorIdentity TEXT)");
        Exec(con, @"CREATE TABLE Localizations_enUS (LocId INT, Formatted INT, Loc TEXT,
                                                     PRIMARY KEY (LocId, Formatted))");
        // Real DB stores card titles at Formatted = 1 only.
        Exec(con, "INSERT INTO Cards VALUES (96179, 648, '5', '', '', 0, '1')");
        Exec(con, "INSERT INTO Localizations_enUS VALUES (648, 1, 'Plains')");
        // A token, and the real database's own way of saying colourless: the empty
        // string, never null. See CardInfo.ColorIdentity for why the two differ.
        Exec(con, "INSERT INTO Cards VALUES (91843, 700, '2', '1', '1', 1, '')");
        Exec(con, "INSERT INTO Localizations_enUS VALUES (700, 1, 'Rabbit')");
        // A LocId that also has a Formatted=0 row, to prove ordering is stable.
        Exec(con, "INSERT INTO Localizations_enUS VALUES (900, 0, 'plain text')");
        Exec(con, "INSERT INTO Localizations_enUS VALUES (900, 1, 'formatted')");
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

    [Test]
    public void CardForGrpId_resolves_title_stored_at_Formatted_1()
    {
        using var db = new CardDb(_dbPath);
        var card = db.CardForGrpId(96179);
        Assert.That(card, Is.Not.Null);
        Assert.That(card!.Name, Is.EqualTo("Plains"));
        Assert.That(card.IsToken, Is.False);
        Assert.That(card.ColorIdentity, Is.EqualTo("1"));
    }

    /// <summary>
    /// Colourless is the empty string in the shipped database, and null is nobody
    /// having said. Collapsing them would put "colourless" on every row rendered from
    /// a card source that predates the column.
    /// </summary>
    [Test]
    public void CardForGrpId_keeps_colourless_apart_from_unrecorded()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.CardForGrpId(91843)!.ColorIdentity, Is.EqualTo(""));
    }

    [Test]
    public void CardForGrpId_reads_token_flag_and_stats()
    {
        using var db = new CardDb(_dbPath);
        var card = db.CardForGrpId(91843)!;
        Assert.That(card.Name, Is.EqualTo("Rabbit"));
        Assert.That(card.IsToken, Is.True);
        Assert.That(card.Power, Is.EqualTo("1"));
    }

    [Test]
    public void CardForGrpId_returns_null_for_ability_grpid_absent_from_Cards()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.CardForGrpId(176406), Is.Null);
    }

    [Test]
    public void NameForLocId_prefers_lowest_Formatted_deterministically()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.NameForLocId(900), Is.EqualTo("plain text"));
        Assert.That(db.NameForLocId(648), Is.EqualTo("Plains"));
    }

    [Test]
    public void NameForLocId_returns_null_when_missing()
    {
        using var db = new CardDb(_dbPath);
        Assert.That(db.NameForLocId(123456), Is.Null);
    }
}
