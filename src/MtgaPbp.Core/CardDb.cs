using Microsoft.Data.Sqlite;

namespace MtgaPbp.Core;

public sealed class CardDb : ICardDb, IDisposable
{
    private readonly SqliteConnection _con;
    private readonly Dictionary<int, string?> _locCache = new();
    private readonly Dictionary<int, CardInfo?> _cardCache = new();
    private readonly Dictionary<(string Type, int Value), string?> _enumCache = new();

    public CardDb(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"Card database not found at: {dbPath}", dbPath);
        _con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        _con.Open();
    }

    /// <summary>Newest Raw_CardDatabase_*.mtga under the known Arena install paths.</summary>
    public static string? FindDatabase(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? overridePath : null;

        string[] roots =
        [
            @"C:\Program Files (x86)\Steam\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files (x86)\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
        ];

        return roots.Where(Directory.Exists)
                    .SelectMany(r => Directory.EnumerateFiles(r, "Raw_CardDatabase_*.mtga"))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
    }

    /// <summary>
    /// Every card name the database knows, for checking an imported collection against.
    /// </summary>
    /// <remarks>
    /// Read straight from the localization table joined to Cards rather than card by
    /// card: a collection is a few thousand names and one query is the difference
    /// between instant and a visible pause. Duplicates are expected — a card printed in
    /// several sets has a row each — and the caller wants a set anyway.
    /// </remarks>
    public IEnumerable<string> AllNames()
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT l.Loc FROM Cards c " +
            "JOIN Localizations_enUS l ON l.LocId = c.TitleId " +
            "WHERE l.Loc IS NOT NULL AND l.Loc <> ''";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(0)) yield return r.GetString(0);
    }

    public string? NameForLocId(int locId)
    {
        if (_locCache.TryGetValue(locId, out var hit)) return hit;
        using var cmd = _con.CreateCommand();
        cmd.CommandText =
            "SELECT Loc FROM Localizations_enUS WHERE LocId = $id ORDER BY Formatted LIMIT 1";
        cmd.Parameters.AddWithValue("$id", locId);
        var result = cmd.ExecuteScalar() as string;
        _locCache[locId] = result;
        return result;
    }

    public CardInfo? CardForGrpId(int grpId)
    {
        if (_cardCache.TryGetValue(grpId, out var hit)) return hit;
        using var cmd = _con.CreateCommand();
        // Card titles live at Formatted = 1; ORDER BY keeps this deterministic.
        cmd.CommandText = """
            SELECT c.GrpId, l.Loc, c.Types, c.Power, c.Toughness, c.IsToken
            FROM Cards c
            LEFT JOIN Localizations_enUS l ON l.LocId = c.TitleId
            WHERE c.GrpId = $id
            ORDER BY l.Formatted
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", grpId);
        using var r = cmd.ExecuteReader();
        CardInfo? info = null;
        if (r.Read() && !r.IsDBNull(1))
        {
            info = new CardInfo(
                r.GetInt32(0),
                r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                !r.IsDBNull(5) && r.GetBoolean(5));
        }
        _cardCache[grpId] = info;
        return info;
    }

    public string? EnumName(string type, int value)
    {
        var key = (type, value);
        if (_enumCache.TryGetValue(key, out var hit)) return hit;

        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            SELECT l.Loc
            FROM Enums e
            JOIN Localizations_enUS l ON l.LocId = e.LocId
            WHERE e.Type = $type AND e.Value = $value
            ORDER BY l.Formatted
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$value", value);

        var name = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(name)) name = null;
        _enumCache[key] = name;
        return name;
    }

    public void Dispose() => _con.Dispose();
}
