using Microsoft.Data.Sqlite;

namespace MtgaPbp.Core;

public sealed class CardDb : ICardDb, IDisposable
{
    private readonly SqliteConnection _con;
    private readonly Dictionary<int, string?> _locCache = new();
    private readonly Dictionary<int, CardInfo?> _cardCache = new();

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

    public void Dispose() => _con.Dispose();
}
