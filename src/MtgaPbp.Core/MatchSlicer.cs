using System.Text.Json;

namespace MtgaPbp.Core;

public static class MatchSlicer
{
    private sealed class Builder
    {
        public long Start = long.MaxValue;
        public long End = long.MinValue;
        public bool SawFinalResult;
        public int Gaps;
        public bool HasDeck;
        public readonly List<string> Lines = [];
    }

    /// <summary>
    /// How many un-attributed <c>ConnectResp</c> envelopes may wait for a match to open.
    /// </summary>
    /// <remarks>
    /// The longest run actually observed is one. Across the 67 MB in Player.log and
    /// Player-prev.log there are 7,053 <c>greToClientEvent</c> envelopes, of which 29
    /// arrive while no match is in progress; every one of the 29 is a lone
    /// <c>ConnectResp</c>, and every one is followed immediately by the match it
    /// belongs to. Two leaves a single envelope of headroom for a reconnect without
    /// letting a run of anything accumulate.
    /// </remarks>
    private const int MaxPending = 2;

    /// <summary>
    /// Groups log envelopes into matches.
    /// <para>
    /// Almost nothing names its match. Across 7,053 engine envelopes in a real pair of
    /// logs, 142 carry <c>gameInfo.matchID</c>: every one of the 37
    /// <c>GameStateType_Full</c> messages, and 105 of the 12,239 <c>GameStateType_Diff</c>
    /// ones. So the match id is sticky: once a match is identified, subsequent engine
    /// traffic belongs to it until a different match id appears. Engine traffic seen
    /// before any match id is dropped rather than guessed at — with one exception, below.
    /// </para>
    /// <para>
    /// An earlier version of this comment claimed only <c>Full</c> ever names a match.
    /// That was true of the 24-match sample it was written from and is false in general;
    /// a <c>Diff</c> naming a match is exactly what re-arms an already-finished one and
    /// caused the misattribution described below. Do not narrow it again without
    /// counting.
    /// </para>
    /// <para>
    /// A <c>ConnectResp</c> announces a new engine connection, and Arena writes it
    /// about three lines <em>before</em> it names the match that connection opens. It
    /// therefore belongs to the next match, never to the one in progress, and is held
    /// aside until that match opens. Both halves of that matter: without the buffer the
    /// decklist it carries was dropped for 29 of the 35 matches in the current logs,
    /// and without refusing it the sticky match id it was attributed to the wrong
    /// match in the 30th — a <c>GameStateType_Diff</c> can arrive after
    /// <c>finalMatchResult</c> and re-arm a match that has already ended.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MatchSlice> Slice(IEnumerable<LogEnvelope> envelopes)
    {
        var builders = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var order = new List<string>();
        string? current = null;
        var pending = new List<LogEnvelope>();

        foreach (var env in envelopes)
        {
            var explicitId = ExtractMatchId(env.Root);
            if (explicitId is not null) current = explicitId;

            // Held rather than dropped, and held rather than attributed. When the
            // ConnectResp shares its envelope with a message that names a match — six
            // of the 35 in the current logs — that name is authoritative and it goes
            // straight there instead.
            if (explicitId is null && DeckList.IsConnectResp(env.Root))
            {
                if (pending.Count == MaxPending) pending.RemoveAt(0);
                pending.Add(env);
                continue;
            }

            // Attribute unlabelled engine traffic to the match in progress.
            var matchId = explicitId ?? (IsEngineTraffic(env.Root) ? current : null);
            if (matchId is null) continue;

            if (!builders.TryGetValue(matchId, out var b))
            {
                b = new Builder();
                builders[matchId] = b;
                order.Add(matchId);
                // The buffer is the property of whichever match opens next, and it is
                // flushed first so the slice stays in log order.
                foreach (var held in pending) Take(b, held);
            }

            // Emptied either way: anything still waiting when a match we already know
            // speaks up was not the herald of a new match after all, so it is stale.
            // This is the bound that keeps a reconnect in the middle of one match from
            // handing its decklist to the next one.
            pending.Clear();

            Take(b, env);

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
                Incomplete: !b.SawFinalResult,
                Gaps: b.Gaps,
                HasDeck: b.HasDeck);
        }).ToList();
    }

    private static void Take(Builder b, LogEnvelope env)
    {
        b.Lines.Add(env.Root.GetRawText());
        if (LogGaps.IsGap(env.Root)) b.Gaps++;
        if (DeckList.HasDeck(env.Root)) b.HasDeck = true;
        if (env.TimestampMs > 0)
        {
            if (env.TimestampMs < b.Start) b.Start = env.TimestampMs;
            if (env.TimestampMs > b.End) b.End = env.TimestampMs;
        }
    }

    /// <summary>
    /// Game-engine traffic, which belongs to whichever match is in progress.
    /// <para>
    /// A gap counts as engine traffic because that is exactly what it stands in for: a
    /// message the engine sent and the log did not keep. It has to be attributed the
    /// same sticky way, or the one match that needs the warning is the one match that
    /// never gets it. A gap seen while no match is in progress is dropped with the rest
    /// of the unattributable traffic — it belongs to nobody's transcript.
    /// </para>
    /// </summary>
    private static bool IsEngineTraffic(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        (root.TryGetProperty("greToClientEvent", out _) || LogGaps.IsGap(root));

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
