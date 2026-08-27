using Microsoft.Data.Sqlite;

namespace MtgaPbp.Core;

public sealed class CardDb : ICardDb, IDisposable
{
    private readonly SqliteConnection _con;
    private readonly Dictionary<int, string?> _locCache = new();
    private readonly Dictionary<int, CardInfo?> _cardCache = new();
    private readonly Dictionary<(string Type, int Value), string?> _enumCache = new();
    private readonly Dictionary<int, string?> _abilityCache = new();
    private readonly Dictionary<string, CardFace?> _faceCache = new(StringComparer.Ordinal);

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
            SELECT c.GrpId, l.Loc, c.Types, c.Power, c.Toughness, c.IsToken, c.ColorIdentity
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
                !r.IsDBNull(5) && r.GetBoolean(5))
            {
                // Null only when the column itself is null, which no row in the shipped
                // database is: colourless cards store the empty string, and the two
                // have to stay apart. See CardInfo.ColorIdentity.
                ColorIdentity = r.IsDBNull(6) ? null : r.GetString(6)
            };
        }
        _cardCache[grpId] = info;
        return info;
    }

    /// <summary>
    /// The face the decklist peek shows (#99), looked up by exact title. Null when no
    /// real card carries the name — token-only names and the extractor's "Card #123"
    /// fallbacks land there, and the caller simply shows no peek.
    /// </summary>
    /// <remarks>
    /// Non-token rows only, primary printing first: a card reprinted across sets has a
    /// row each, and any of them answers, but the primary one is the least likely to
    /// carry a promo oddity. Rules text comes from <c>AbilityIds</c>, whose entries are
    /// <c>abilityId:textLocId</c> pairs — the second half resolves through the same
    /// localization table as everything else, no join through Abilities needed.
    /// </remarks>
    public CardFace? FaceForName(string name)
    {
        if (_faceCache.TryGetValue(name, out var hit)) return hit;
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            SELECT c.OldSchoolManaText, c.TypeTextId, c.SubtypeTextId, c.AbilityIds,
                   c.Power, c.Toughness
            FROM Cards c
            JOIN Localizations_enUS l ON l.LocId = c.TitleId
            WHERE l.Loc = $name AND c.IsToken = 0
            ORDER BY c.IsPrimaryCard DESC, c.GrpId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$name", name);
        using var r = cmd.ExecuteReader();
        CardFace? face = null;
        if (r.Read())
        {
            var type = r.IsDBNull(1) ? null : NameForLocId(r.GetInt32(1));
            var subtype = r.IsDBNull(2) ? null : NameForLocId(r.GetInt32(2));
            var typeLine = string.IsNullOrWhiteSpace(subtype)
                ? type ?? ""
                : $"{type} — {subtype}";

            var rules = new List<string>();
            foreach (var pair in (r.IsDBNull(3) ? "" : r.GetString(3))
                         .Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = pair.IndexOf(':');
                if (colon < 0 || !int.TryParse(pair[(colon + 1)..], out var textLocId))
                    continue;
                // Through the same cleaner every ability text on the page goes
                // through — the raw rows carry Arena's renderer markup and o-packed
                // symbol runs. CARDNAME is resolved to the card's own name first: a
                // face, unlike a grant, knows exactly whose text it is showing.
                if (NameForLocId(textLocId) is { } text && !string.IsNullOrWhiteSpace(text))
                    rules.Add(Core.AbilityText.Plain(
                        text.Replace("CARDNAME", name, StringComparison.Ordinal)));
            }

            face = new CardFace(
                name,
                CardFace.DecodeMana(r.IsDBNull(0) ? null : r.GetString(0)),
                typeLine,
                rules,
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5));
        }
        _faceCache[name] = face;
        return face;
    }

    public string? AbilityText(int abilityGrpId)
    {
        if (_abilityCache.TryGetValue(abilityGrpId, out var hit)) return hit;
        using var cmd = _con.CreateCommand();
        // First Formatted variant available, not a fixed one: ability rows are
        // inconsistent about which variants exist — "First strike" lives only at
        // Formatted = 1, while most whole-sentence texts also have 0 and 2 — and
        // pinning any single value silently loses most of the table.
        cmd.CommandText = """
            SELECT l.Loc
            FROM Abilities a
            JOIN Localizations_enUS l ON l.LocId = a.TextId
            WHERE a.Id = $id
            ORDER BY l.Formatted
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", abilityGrpId);
        var text = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(text)) text = null;
        _abilityCache[abilityGrpId] = text;
        return text;
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
