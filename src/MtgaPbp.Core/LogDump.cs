using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>
/// One turn of the log as a reader can check it: what the game recorded, and what it
/// asked the player.
/// </summary>
/// <param name="Annotations">
/// The turn's raw annotations, in log order, with instance ids resolved to the names
/// they had at the time.
/// </param>
/// <param name="Negotiations">
/// The turn's prompts — costs demanded, ways to pay offered — folded so a request the
/// client re-sent on every reconsideration is said once and counted. Empty on the great
/// majority of turns, which ask the player nothing worth reporting.
/// </param>
public sealed record TurnDump(
    IReadOnlyList<string> Annotations, IReadOnlyList<string> Negotiations);

/// <summary>
/// The raw log of one turn, with instance ids resolved to the names they had at the time.
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
public static class LogDump
{
    /// <summary>
    /// Everything one turn of one game has to say for itself.
    /// </summary>
    public static TurnDump ForTurn(
        IReadOnlyList<string> rawLines, ICardDb cards, int turn, int game) =>
        ForTurns(rawLines, cards, [(turn, game)])[(turn, game)];

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
    /// That is also why the requests are read here rather than in a walk of their own.
    /// They need the same tracker in the same state to name the same ids, and a second
    /// pass would put the as-of-turn rule in two places for one of them to get wrong.
    /// </para>
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
    public static IReadOnlyDictionary<(int Turn, int Game), TurnDump> ForTurns(
        IReadOnlyList<string> rawLines, ICardDb cards,
        IReadOnlyCollection<(int Turn, int Game)> wanted)
    {
        var collected = wanted.Distinct().ToDictionary(pair => pair, _ => new Collector());
        if (collected.Count == 0) return Frozen(collected);

        var tracker = new GameStateTracker(cards);
        var currentTurn = 0;
        var currentGame = 1;

        string Name(int? id) => id switch
        {
            null => "-",
            // Never a game object: across 529 archived matches not one instance id of 1
            // or 2 is ever sent as one, while both appear as the subject of an
            // annotation and as the payer of a cost. They are seats.
            <= 2 and > 0 => $"seat{id}",
            _ => $"{tracker.NameOf(id.Value)} #{id}"
        };

        // The same resolver the annotations use, so an id reads identically in both
        // halves of the dump and a reader can carry it from one to the other.
        var named = (int id) => Name(id);

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
                var gsm = Json.Obj(m, "gameStateMessage");
                if (gsm is { } state)
                {
                    if (Json.Obj(state, "gameInfo") is { } gi &&
                        Json.Int(gi, "gameNumber") is { } gn) currentGame = gn;
                    if (Json.Obj(state, "turnInfo") is { } ti &&
                        Json.Int(ti, "turnNumber") is { } tn) currentTurn = tn;

                    // Applied on every message regardless of turn, so that by the time
                    // the requested turn arrives the tracker knows what everything is
                    // called.
                    tracker.Apply(state);
                }

                if (!collected.TryGetValue((currentTurn, currentGame), out var into)) continue;

                if (gsm is { } shown)
                    foreach (var a in Json.Array(shown, "annotations"))
                        Annotate(into.Annotations, a, Name);

                // A request arrives in the same batch as the state it is about, but not
                // inside the game-state message, so it is read from the message itself.
                foreach (var prompt in Negotiations.Describe(m, cards, named))
                    into.Add(prompt);
            }
        }

        return Frozen(collected);
    }

    private static void Annotate(List<string> into, JsonElement a, Func<int?, string> name)
    {
        var types = string.Join(",", Json.Array(a, "type")
            .Where(t => t.ValueKind == JsonValueKind.String)
            .Select(t => t.GetString()!.Replace("AnnotationType_", "", StringComparison.Ordinal)));

        var affected = string.Join(", ", Json.Array(a, "affectedIds")
            .Where(x => x.ValueKind == JsonValueKind.Number)
            .Select(x => name(x.GetInt32())));

        into.Add(types);
        into.Add($"    by {name(Json.Int(a, "affectorId"))}  on [{affected}]");

        var details = Detail(a);
        if (details.Length > 0) into.Add($"    {details}");
    }

    /// <summary>
    /// One turn's findings as they accumulate, with identical prompts folded.
    /// </summary>
    /// <remarks>
    /// The client re-sends a request every time the player reconsiders, so a turn spent
    /// failing to pay for an attack carries the same three prompts over and over — the
    /// worst turn in the archive sends 17 requests that are 4 distinct ones. Folding
    /// saves only 2% of requests archive-wide, which is the wrong number to judge it by:
    /// it saves 76% on the turn somebody actually opens this to read.
    /// <para>
    /// Folded on the whole rendering rather than on consecutive repeats. The repeats are
    /// interleaved in practice — declare, pay, dead end, cancel, declare again — so a
    /// consecutive-only rule would have collapsed nothing at all on the turn that
    /// prompted the issue.
    /// </para>
    /// </remarks>
    private sealed class Collector
    {
        public readonly List<string> Annotations = [];
        private readonly List<(Negotiation Prompt, int Count)> _prompts = [];
        private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

        public void Add(Negotiation prompt)
        {
            var key = string.Join("\n", [prompt.Headline, .. prompt.Detail]);
            if (_seen.TryGetValue(key, out var at))
            {
                _prompts[at] = (prompt, _prompts[at].Count + 1);
                return;
            }

            _seen[key] = _prompts.Count;
            _prompts.Add((prompt, 1));
        }

        /// <summary>
        /// The turn's prompts as lines: the ones that could be read out first, then the
        /// ones that could only be named.
        /// </summary>
        /// <remarks>
        /// Two thirds of the archive's turns carry a request of some kind and only 1.7%
        /// carry a cost, so on almost every turn the untranslated names are the whole
        /// section and their position does not arise. On the turns where it does, the
        /// answer should not be underneath them. Nothing true is lost by moving them:
        /// folding has already replaced each prompt's position with its first
        /// appearance, so the section was never a timeline to begin with.
        /// </remarks>
        public IReadOnlyList<string> Negotiations()
        {
            var lines = new List<string>();
            foreach (var (prompt, count) in _prompts.OrderBy(p => p.Prompt.Detail.Count == 0))
            {
                lines.Add(count == 1 ? prompt.Headline : $"{prompt.Headline}  (asked {count} times)");
                lines.AddRange(prompt.Detail.Select(d => $"    {d}"));
            }
            return lines;
        }
    }

    private static IReadOnlyDictionary<(int Turn, int Game), TurnDump> Frozen(
        Dictionary<(int Turn, int Game), Collector> collected) =>
        collected.ToDictionary(
            kv => kv.Key, kv => new TurnDump(kv.Value.Annotations, kv.Value.Negotiations()));

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
