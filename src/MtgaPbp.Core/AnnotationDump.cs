using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>
/// The raw annotations of one turn, with instance ids resolved to the names they had at
/// the time.
/// </summary>
/// <remarks>
/// Every output bug in this project so far was found the same way: read a transcript,
/// notice a line that looks wrong, then hand-write a script to walk the gzipped archive
/// and print what the log actually said. That loop finds things no test does — a golden
/// file can only pin what someone already thought to check — and the slowest part of it
/// is writing the script again. This is that script, kept.
/// <para>
/// Ids are printed alongside names because the raw log is a wall of integers and the
/// question is usually "which permanent is 405". The name tells you whether the parser
/// agrees with you; the id is what you search the archive for next.
/// </para>
/// <para>
/// Lives in Core rather than the CLI because it reads log JSON, which is Core's work,
/// and it yields lines rather than printing them so nothing here owns a console.
/// </para>
/// </remarks>
public static class AnnotationDump
{
    /// <summary>
    /// Every annotation of one turn of one game, in the order the log carried them.
    /// </summary>
    public static IEnumerable<string> ForTurn(
        IReadOnlyList<string> rawLines, ICardDb cards, int turn, int game) =>
        ForTurns(rawLines, cards, [(turn, game)]).TryGetValue((turn, game), out var lines)
            ? lines
            : [];

    /// <summary>
    /// The same, for several turns at once, reading the match exactly once.
    /// </summary>
    /// <remarks>
    /// The walk cannot start at the turn asked for. The tracker has to have seen every
    /// message up to that point or the names come out wrong — a permanent renamed on
    /// turn 14 must not be named that way on turn 13, which is the whole of issue #23.
    /// So the replay is not waste and cannot be skipped; what it can do is answer more
    /// than one question per pass, which is what this is for. Asking for a whole match
    /// one turn at a time re-parsed the same JSON once per turn.
    /// <para>
    /// Keyed on the pair, never on the turn alone: Arena hands out instance ids again
    /// in each game, and a dump that ignored the game number would answer game one's
    /// question out of game two's state.
    /// </para>
    /// <para>
    /// Returns the lines rather than streaming them, because a single pass produces
    /// them in log order — game by game — while the caller prints turn by turn. The
    /// buffer is bounded by what was asked for, and the longest match in the archive
    /// runs to 33 turns.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<(int Turn, int Game), IReadOnlyList<string>> ForTurns(
        IReadOnlyList<string> rawLines, ICardDb cards,
        IReadOnlyCollection<(int Turn, int Game)> wanted)
    {
        var collected = wanted.Distinct().ToDictionary(pair => pair, _ => new List<string>());
        if (collected.Count == 0) return Frozen(collected);

        var tracker = new GameStateTracker(cards);
        var currentTurn = 0;
        var currentGame = 1;

        string Name(int? id) => id switch
        {
            null => "-",
            <= 2 and > 0 => $"seat{id}",
            _ => $"{tracker.NameOf(id.Value)} #{id}"
        };

        foreach (var line in rawLines)
        {
            JsonElement root;
            try
            {
                // Disposed rather than dropped: the clone owns its own memory and
                // outlives the document, so the parse buffers can go back to the pool
                // instead of waiting on the collector once per line of the match.
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch (JsonException) { continue; }

            if (Json.Obj(root, "greToClientEvent") is not { } gre) continue;

            foreach (var m in Json.Array(gre, "greToClientMessages"))
            {
                if (Json.Obj(m, "gameStateMessage") is not { } gsm) continue;

                if (Json.Obj(gsm, "gameInfo") is { } gi &&
                    Json.Int(gi, "gameNumber") is { } gn) currentGame = gn;
                if (Json.Obj(gsm, "turnInfo") is { } ti &&
                    Json.Int(ti, "turnNumber") is { } tn) currentTurn = tn;

                // Applied on every message regardless of turn, so that by the time the
                // requested turn arrives the tracker knows what everything is called.
                tracker.Apply(gsm);
                if (!collected.TryGetValue((currentTurn, currentGame), out var into)) continue;

                foreach (var a in Json.Array(gsm, "annotations"))
                {
                    var types = string.Join(",", Json.Array(a, "type")
                        .Where(t => t.ValueKind == JsonValueKind.String)
                        .Select(t => t.GetString()!.Replace("AnnotationType_", "", StringComparison.Ordinal)));

                    var affected = string.Join(", ", Json.Array(a, "affectedIds")
                        .Where(x => x.ValueKind == JsonValueKind.Number)
                        .Select(x => Name(x.GetInt32())));

                    into.Add(types);
                    into.Add($"    by {Name(Json.Int(a, "affectorId"))}  on [{affected}]");

                    var details = Detail(a);
                    if (details.Length > 0) into.Add($"    {details}");
                }
            }
        }

        return Frozen(collected);
    }

    private static IReadOnlyDictionary<(int Turn, int Game), IReadOnlyList<string>> Frozen(
        Dictionary<(int Turn, int Game), List<string>> collected) =>
        collected.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);

    /// <summary>
    /// An annotation's details as <c>key=value</c>, skipping the keys that carry nothing.
    /// </summary>
    private static string Detail(JsonElement a)
    {
        var parts = new List<string>();
        foreach (var d in Json.Array(a, "details"))
        {
            var values = Json.Array(d, "valueInt32")
                .Select(v => v.ToString())
                .Concat(Json.Array(d, "valueString")
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!));

            var joined = string.Join("/", values);
            if (joined.Length > 0) parts.Add($"{Json.Str(d, "key")}={joined}");
        }
        return string.Join("  ", parts);
    }
}
