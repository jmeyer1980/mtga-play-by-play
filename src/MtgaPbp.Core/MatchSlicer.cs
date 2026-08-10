using System.Text.Json;

namespace MtgaPbp.Core;

public static class MatchSlicer
{
    private sealed class Builder
    {
        public long Start = long.MaxValue;
        public long End = long.MinValue;
        public bool SawFinalResult;
        public readonly List<string> Lines = [];
    }

    /// <summary>
    /// Groups log envelopes into matches.
    /// <para>
    /// Only <c>GameStateType_Full</c> carries <c>gameInfo.matchID</c> — in a real log
    /// that is 74 lines out of 4,774. Every <c>GameStateType_Diff</c>, which is where
    /// the annotations live, has no match id at all. So the match id is sticky:
    /// once a match is identified, subsequent game-engine traffic belongs to it until
    /// a different match id appears. Engine traffic seen before any match id is
    /// dropped rather than guessed at.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MatchSlice> Slice(IEnumerable<LogEnvelope> envelopes)
    {
        var builders = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var order = new List<string>();
        string? current = null;

        foreach (var env in envelopes)
        {
            var explicitId = ExtractMatchId(env.Root);
            if (explicitId is not null) current = explicitId;

            // Attribute unlabelled engine traffic to the match in progress.
            var matchId = explicitId ?? (IsEngineTraffic(env.Root) ? current : null);
            if (matchId is null) continue;

            if (!builders.TryGetValue(matchId, out var b))
            {
                b = new Builder();
                builders[matchId] = b;
                order.Add(matchId);
            }

            b.Lines.Add(env.Root.GetRawText());
            if (env.TimestampMs > 0)
            {
                if (env.TimestampMs < b.Start) b.Start = env.TimestampMs;
                if (env.TimestampMs > b.End) b.End = env.TimestampMs;
            }

            if (HasFinalResult(env.Root))
            {
                b.SawFinalResult = true;
                current = null;   // the match is over; stop absorbing later traffic
            }
        }

        return order.Select(id =>
        {
            var b = builders[id];
            return new MatchSlice(
                id,
                b.Start == long.MaxValue ? 0 : b.Start,
                b.End == long.MinValue ? 0 : b.End,
                b.Lines,
                Incomplete: !b.SawFinalResult);
        }).ToList();
    }

    /// <summary>Game-engine traffic, which belongs to whichever match is in progress.</summary>
    private static bool IsEngineTraffic(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("greToClientEvent", out _);

    private static string? ExtractMatchId(JsonElement root)
    {
        if (Json.Obj(root, "matchGameRoomStateChangedEvent") is { } room &&
            Json.Obj(room, "gameRoomInfo") is { } info &&
            Json.Obj(info, "gameRoomConfig") is { } cfg &&
            Json.Str(cfg, "matchId") is { } mid)
            return mid;

        if (Json.Obj(root, "greToClientEvent") is { } gre)
        {
            foreach (var m in Json.Array(gre, "greToClientMessages"))
            {
                if (Json.Obj(m, "gameStateMessage") is { } gsm &&
                    Json.Obj(gsm, "gameInfo") is { } gi &&
                    Json.Str(gi, "matchID") is { } id)
                    return id;
            }
        }
        return null;
    }

    private static bool HasFinalResult(JsonElement root) =>
        Json.Obj(root, "matchGameRoomStateChangedEvent") is { } room &&
        Json.Obj(room, "gameRoomInfo") is { } info &&
        info.TryGetProperty("finalMatchResult", out _);
}
