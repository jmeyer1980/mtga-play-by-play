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
        IReadOnlyList<string> rawLines, ICardDb cards, int turn, int game)
    {
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
            try { root = JsonDocument.Parse(line).RootElement.Clone(); }
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
                if (currentTurn != turn || currentGame != game) continue;

                foreach (var a in Json.Array(gsm, "annotations"))
                {
                    var types = string.Join(",", Json.Array(a, "type")
                        .Where(t => t.ValueKind == JsonValueKind.String)
                        .Select(t => t.GetString()!.Replace("AnnotationType_", "", StringComparison.Ordinal)));

                    var affected = string.Join(", ", Json.Array(a, "affectedIds")
                        .Where(x => x.ValueKind == JsonValueKind.Number)
                        .Select(x => Name(x.GetInt32())));

                    yield return types;
                    yield return $"    by {Name(Json.Int(a, "affectorId"))}  on [{affected}]";

                    var details = Detail(a);
                    if (details.Length > 0) yield return $"    {details}";
                }
            }
        }
    }

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
