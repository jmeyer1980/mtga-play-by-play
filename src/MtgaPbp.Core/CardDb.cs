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
    private readonly Dictionary<int, CardInfo?> _faceCardCache = new();
    private readonly Dictionary<int, bool> _cardTitleCache = new();

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
    /// The face the decklist peek shows (#99), looked up by exact title, with the
    /// card's other faces beside it (#221). Null when no real card carries the name —
    /// token-only names and the extractor's "Card #123" fallbacks land there, and the
    /// caller simply shows no peek.
    /// </summary>
    /// <remarks>
    /// Non-token rows only, primary printing first: a card reprinted across sets has a
    /// row each, and any of them answers, but the primary one is the least likely to
    /// carry a promo oddity. Rules text comes from <c>AbilityIds</c>, whose entries are
    /// <c>abilityId:textLocId</c> pairs — the second half resolves through the same
    /// localization table as everything else, no join through Abilities needed.
    /// <para>
    /// Other faces come from <c>LinkedFaceGrpIds</c>, one hop from the printing that
    /// answered: an Adventure creature links to its Adventure and back, a double-faced
    /// card to its other face, a meld piece to what it melds into. A split card or a
    /// Room is shaped differently — each half links to a row for the whole card,
    /// titled "A // B", which links to both halves, and that row is the one Arena's
    /// decklist names. The whole card is never offered as a face, because it is the
    /// card and not a face of it: reached from a half it stands in for the other
    /// halves, and asked for by its own name it yields them. A prototype card links to
    /// a second row with its own title, and a face is not another face of itself.
    /// Nothing reached this way carries links of its own: one hop, because the reader
    /// started at the card.
    /// </para>
    /// </remarks>
    public CardFace? FaceForName(string name)
    {
        if (_faceCache.TryGetValue(name, out var hit)) return hit;
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            SELECT c.GrpId
            FROM Cards c
            JOIN Localizations_enUS l ON l.LocId = c.TitleId
            WHERE l.Loc = $name AND c.IsToken = 0
            ORDER BY c.IsPrimaryCard DESC, c.GrpId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$name", name);
        CardFace? face = null;
        if (cmd.ExecuteScalar() is long grpId && Row((int)grpId) is { } row)
        {
            // Under the name asked for, exactly as before; the row's own title is the
            // same string, and the cache and the callers key on this one.
            var self = row with { Name = name };
            face = Build(self, OtherFacesOf(self));
        }
        _faceCache[name] = face;
        return face;
    }

    /// <summary>The columns a face is built from, for one printing.</summary>
    private sealed record FaceRow(int GrpId, int TitleId, string Name, string? Mana, int? TypeId,
        int? SubtypeId, string Abilities, string? Power, string? Toughness, string Linked);

    /// <summary>
    /// One printing's face columns by grpId, or null for a token or an id the database
    /// does not have — a link that goes nowhere is simply not followed.
    /// </summary>
    private FaceRow? Row(int grpId)
    {
        using var cmd = _con.CreateCommand();
        // Titles live at Formatted = 1; ORDER BY keeps this deterministic, as in
        // CardForGrpId.
        cmd.CommandText = """
            SELECT c.GrpId, l.Loc, c.OldSchoolManaText, c.TypeTextId, c.SubtypeTextId,
                   c.AbilityIds, c.Power, c.Toughness, c.LinkedFaceGrpIds, c.TitleId
            FROM Cards c
            LEFT JOIN Localizations_enUS l ON l.LocId = c.TitleId
            WHERE c.GrpId = $id AND c.IsToken = 0
            ORDER BY l.Formatted
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", grpId);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(1)) return null;
        return new FaceRow(
            r.GetInt32(0), r.GetInt32(9), r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetInt32(3),
            r.IsDBNull(4) ? null : r.GetInt32(4),
            r.IsDBNull(5) ? "" : r.GetString(5),
            Stat(r, 6), Stat(r, 7),
            r.IsDBNull(8) ? "" : r.GetString(8));
    }

    /// <summary>
    /// The card a grpId belongs to (#223): the row itself when its title is a card's,
    /// otherwise the first linked row whose title is — the creature for an Adventure,
    /// the front for a back face, the whole "A // B" card for a door — and the row
    /// itself again when no link leads anywhere better.
    /// </summary>
    /// <remarks>
    /// What tells a face's title from a card's is that no printing of a face is ever
    /// the primary one: every card has a printing with <c>IsPrimaryCard = 1</c>
    /// somewhere in the database, while Stomp, Tibalt, Cosmic Impostor and Dollmaker's
    /// Shop have rows with 0 and nothing else (checked across all 23,000 non-token
    /// rows, 2026-09-08). A reprint of a card has 0 too, but shares its title with
    /// the printing that has 1, so the test is on the title, not the row. A prototype
    /// card's second row carries the card's own title and so stays itself; a melded
    /// card's row has no primary printing and links to its parts, so a melded
    /// permanent lists as the part it is printed on. Tokens have no face rows and fall
    /// straight through to <see cref="CardForGrpId"/>.
    /// </remarks>
    public CardInfo? CardForFace(int grpId)
    {
        if (_faceCardCache.TryGetValue(grpId, out var hit)) return hit;
        CardInfo? card = null;
        if (Row(grpId) is { } row && !IsCardTitle(row.TitleId))
            foreach (var id in Ids(row.Linked))
                if (Row(id) is { } linked && IsCardTitle(linked.TitleId))
                {
                    card = CardForGrpId(id);
                    break;
                }
        card ??= CardForGrpId(grpId);
        _faceCardCache[grpId] = card;
        return card;
    }

    /// <summary>Whether some non-token printing under this title is the primary one.</summary>
    private bool IsCardTitle(int titleId)
    {
        if (_cardTitleCache.TryGetValue(titleId, out var hit)) return hit;
        using var cmd = _con.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM Cards WHERE TitleId = $title AND IsToken = 0 AND IsPrimaryCard = 1 LIMIT 1";
        cmd.Parameters.AddWithValue("$title", titleId);
        var isCard = cmd.ExecuteScalar() is not null;
        _cardTitleCache[titleId] = isCard;
        return isCard;
    }

    /// <summary>
    /// A power or toughness, or null where the card has none. The column is the empty
    /// string on every row that is not a creature — <c>TEXT NOT NULL</c>, never null —
    /// and a face that carried that forward drew a bare "/" under every instant,
    /// sorcery and enchantment: 1,501 of 1,516 archived pages, 2026-09-08.
    /// </summary>
    private static string? Stat(SqliteDataReader r, int column) =>
        r.IsDBNull(column) || r.GetString(column).Length == 0 ? null : r.GetString(column);

    private CardFace Build(FaceRow row, IReadOnlyList<CardFace> others)
    {
        var type = row.TypeId is { } t ? NameForLocId(t) : null;
        var subtype = row.SubtypeId is { } s ? NameForLocId(s) : null;
        var typeLine = string.IsNullOrWhiteSpace(subtype)
            ? type ?? ""
            : $"{type} — {subtype}";

        // The raw rows in printed order. CARDNAME is resolved to the face's own name
        // first: a face, unlike a grant, knows exactly whose text it is showing.
        var raws = new List<string>();
        foreach (var pair in row.Abilities.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = pair.IndexOf(':');
            if (colon < 0 || !int.TryParse(pair[(colon + 1)..], out var textLocId))
                continue;
            if (NameForLocId(textLocId) is { } text && !string.IsNullOrWhiteSpace(text))
                raws.Add(text.Replace("CARDNAME", row.Name, StringComparison.Ordinal));
        }

        // Through the same cleaner every ability text on the page goes through — the
        // raw rows carry Arena's renderer markup and o-packed symbol runs. A Class card
        // carries each level ability twice (#230): once wrapped for the client, marking
        // the level it works from, and once plain, right after — and on Warlock Class
        // the plain row adds reminder text the wrapper leaves out. The face shows the
        // rule where the printed card has it, so a wrapper's rule is left out whenever
        // a plain row on the card begins with it, and kept — as its rule, not its
        // wrapper — when none does.
        var plainRows = raws.Where(r => !Core.AbilityText.IsClassLevel(r))
            .Select(Core.AbilityText.Plain)
            .ToList();
        var rules = new List<string>();
        foreach (var raw in raws)
        {
            if (!Core.AbilityText.IsClassLevel(raw))
            {
                rules.Add(Core.AbilityText.Plain(raw));
                continue;
            }
            foreach (var rule in Core.AbilityText.Unwrap(raw))
            {
                var plain = Core.AbilityText.Plain(rule);
                if (!plainRows.Any(p => p.StartsWith(plain, StringComparison.Ordinal)))
                    rules.Add(plain);
            }
        }

        return new CardFace(row.Name, CardFace.DecodeMana(row.Mana), typeLine, rules,
            row.Power, row.Toughness)
        { OtherFaces = others };
    }

    /// <summary>The faces one hop from a row, built without links of their own.</summary>
    private IReadOnlyList<CardFace> OtherFacesOf(FaceRow row)
    {
        var others = new List<CardFace>();
        var named = new HashSet<string>(StringComparer.Ordinal) { row.Name };
        foreach (var id in Ids(row.Linked))
        {
            if (Row(id) is not { } linked) continue;
            // The whole card stands in for its other halves.
            IEnumerable<FaceRow> faces = linked.Name.Contains(" // ", StringComparison.Ordinal)
                ? Ids(linked.Linked).Select(Row).OfType<FaceRow>()
                : [linked];
            foreach (var f in faces)
                if (named.Add(f.Name)) others.Add(Build(f, []));
        }
        return others;
    }

    private static IEnumerable<int> Ids(string commaList)
    {
        foreach (var part in commaList.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var id)) yield return id;
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
