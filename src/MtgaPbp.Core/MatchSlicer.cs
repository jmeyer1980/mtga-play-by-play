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

    public static IReadOnlyList<MatchSlice> Slice(IEnumerable<LogEnvelope> envelopes)
    {
        var builders = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var env in envelopes)
        {
            var matchId = ExtractMatchId(env.Root);
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
            if (HasFinalResult(env.Root)) b.SawFinalResult = true;
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

    private static string? ExtractMatchId(JsonElement root)
    {
        if (root.TryGetProperty("matchGameRoomStateChangedEvent", out var room) &&
            room.TryGetProperty("gameRoomInfo", out var info) &&
            info.TryGetProperty("gameRoomConfig", out var cfg) &&
            cfg.TryGetProperty("matchId", out var mid) &&
            mid.ValueKind == JsonValueKind.String)
            return mid.GetString();

        if (root.TryGetProperty("greToClientEvent", out var gre) &&
            gre.TryGetProperty("greToClientMessages", out var msgs) &&
            msgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in msgs.EnumerateArray())
            {
                if (m.TryGetProperty("gameStateMessage", out var gsm) &&
                    gsm.TryGetProperty("gameInfo", out var gi) &&
                    gi.TryGetProperty("matchID", out var id) &&
                    id.ValueKind == JsonValueKind.String)
                    return id.GetString();
            }
        }
        return null;
    }

    private static bool HasFinalResult(JsonElement root) =>
        root.TryGetProperty("matchGameRoomStateChangedEvent", out var room) &&
        room.TryGetProperty("gameRoomInfo", out var info) &&
        info.TryGetProperty("finalMatchResult", out _);
}
