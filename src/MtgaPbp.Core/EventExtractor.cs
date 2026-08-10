using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record PlayerInfo(int Seat, string UserId, string ScreenName, string Platform);

public sealed record Transcript(
    string MatchId, long StartedAtMs, long EndedAtMs, string EventName,
    PlayerInfo? You, PlayerInfo? Opponent,
    int? WinningTeamId, int GamesWon, int GamesLost, bool Incomplete,
    IReadOnlyList<GameEvent> Events,
    IReadOnlyDictionary<string, int> UnknownAnnotations,
    IReadOnlySet<string> CardsSeen);

public sealed class EventExtractor(ICardDb cards)
{
    private static readonly Dictionary<string, EventKind> CategoryKinds = new(StringComparer.Ordinal)
    {
        ["PlayLand"] = EventKind.LandPlayed,
        ["CastSpell"] = EventKind.SpellCast,
        ["Resolve"] = EventKind.Resolved,
        ["Countered"] = EventKind.Countered,
        ["Draw"] = EventKind.Drew,
        ["Discard"] = EventKind.Discarded,
        ["Destroy"] = EventKind.Destroyed,
        ["Sacrifice"] = EventKind.Sacrificed,
        ["Exile"] = EventKind.Exiled,
        ["Return"] = EventKind.Returned,
    };

    private static readonly Dictionary<string, EventKind> SimpleAnnotationKinds =
        new(StringComparer.Ordinal)
        {
            ["AnnotationType_NewTurnStarted"] = EventKind.TurnStart,
            ["AnnotationType_DamageDealt"] = EventKind.Damage,
            ["AnnotationType_ModifiedLife"] = EventKind.LifeChanged,
            ["AnnotationType_TokenCreated"] = EventKind.TokenCreated,
            ["AnnotationType_CounterAdded"] = EventKind.CounterChanged,
            ["AnnotationType_CounterRemoved"] = EventKind.CounterChanged,
            ["AnnotationType_Scry"] = EventKind.Scry,
            ["AnnotationType_RevealedCardCreated"] = EventKind.Revealed,
            ["AnnotationType_ManaPaid"] = EventKind.ManaPaid,
            ["AnnotationType_PhaseOrStepModified"] = EventKind.PhaseChange,
        };

    /// <summary>Annotations that carry no transcript value and are silently dropped.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "AnnotationType_ObjectIdChanged",       // consumed by the tracker as aliasing
        "AnnotationType_AbilityInstanceCreated",
        "AnnotationType_AbilityInstanceDeleted",
        "AnnotationType_TappedUntappedPermanent",
        "AnnotationType_UserActionTaken",
        "AnnotationType_ResolutionStart",
        "AnnotationType_ResolutionComplete",
        "AnnotationType_LayeredEffectCreated",
        "AnnotationType_LayeredEffectDestroyed",
        "AnnotationType_PowerToughnessModCreated",
        "AnnotationType_PlayerSelectingTargets",
        "AnnotationType_PlayerSubmittedTargets", // carries no target ids — see spec
        "AnnotationType_ShouldntPlay",
        "AnnotationType_MultistepEffectStarted",
        "AnnotationType_MultistepEffectComplete",
        "AnnotationType_SyntheticEvent",
        "AnnotationType_TokenDeleted",
        "AnnotationType_GainDesignation",
        "AnnotationType_AttachmentCreated",
        "AnnotationType_ChoiceResult",
        "AnnotationType_RevealedCardDeleted",
        "AnnotationType_DisqualifiedEffect",
        "AnnotationType_Shuffle",
    };

    public Transcript Extract(string matchId, IReadOnlyList<string> rawLines)
    {
        var tracker = new GameStateTracker(cards);
        var events = new List<GameEvent>();
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        var cardsSeen = new HashSet<string>(StringComparer.Ordinal);
        var seatMeta = new Dictionary<int, PlayerInfo>();

        long started = 0, ended = 0;
        string eventName = "";
        int? localSeat = null, fallbackSeat = null, winningTeam = null;
        int gamesForTeam1 = 0, gamesForTeam2 = 0;
        var sawFinal = false;
        var seq = 0;

        foreach (var raw in rawLines)
        {
            JsonElement root;
            try { root = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { continue; }

            var ts = ReadTimestamp(root);
            if (ts > 0) { if (started == 0) started = ts; ended = ts; }

            if (root.TryGetProperty("matchGameRoomStateChangedEvent", out var room) &&
                room.TryGetProperty("gameRoomInfo", out var info))
            {
                ReadRoom(info, seatMeta, ref eventName);
                if (info.TryGetProperty("finalMatchResult", out var fmr))
                {
                    sawFinal = true;
                    ReadResults(fmr, ref winningTeam, ref gamesForTeam1, ref gamesForTeam2);
                }
            }

            if (!root.TryGetProperty("greToClientEvent", out var gre) ||
                !gre.TryGetProperty("greToClientMessages", out var msgs) ||
                msgs.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var m in msgs.EnumerateArray())
            {
                var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type is "GREMessageType_MulliganReq" && localSeat is null)
                    localSeat = FirstSeat(m);
                else if (type is "GREMessageType_ActionsAvailableReq" && fallbackSeat is null)
                    fallbackSeat = FirstSeat(m);

                if (!m.TryGetProperty("gameStateMessage", out var gsm)) continue;

                tracker.Apply(gsm);
                if (gsm.TryGetProperty("annotations", out var anns) &&
                    anns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in anns.EnumerateArray())
                        EmitFor(a, tracker, ts, ref seq, events, unknown, cardsSeen);
                }
            }
        }

        var you = (localSeat ?? fallbackSeat) is { } seat && seatMeta.TryGetValue(seat, out var y)
            ? y : null;
        var opp = you is null ? null : seatMeta.Values.FirstOrDefault(p => p.Seat != you.Seat);
        tracker.LocalSeat = you?.Seat ?? 0;

        var yourTeam = you?.Seat;   // teamId equals seat in every observed match
        var won = yourTeam == 1 ? gamesForTeam1 : gamesForTeam2;
        var lost = yourTeam == 1 ? gamesForTeam2 : gamesForTeam1;

        if (sawFinal)
        {
            events.Add(new GameEvent
            {
                Seq = seq++,
                TimestampMs = ended,
                Kind = EventKind.GameEnd,
                Amount = winningTeam ?? 0,
                Detail = winningTeam is null ? null
                    : winningTeam == yourTeam ? "You win the match" : "Opponent wins the match"
            });
        }

        return new Transcript(
            matchId, started, ended, eventName, you, opp,
            winningTeam, won, lost, Incomplete: !sawFinal,
            events, unknown, cardsSeen);
    }

    private void EmitFor(
        JsonElement a, GameStateTracker tracker, long ts, ref int seq,
        List<GameEvent> events, Dictionary<string, int> unknown, HashSet<string> cardsSeen)
    {
        if (!a.TryGetProperty("type", out var types) || types.ValueKind != JsonValueKind.Array)
            return;

        foreach (var typeEl in types.EnumerateArray())
        {
            var type = typeEl.GetString();
            if (type is null || Ignored.Contains(type)) continue;

            GameEvent? ev;

            if (type == "AnnotationType_ZoneTransfer")
            {
                var category = GameStateTracker.DetailString(a, "category") ?? "";
                var kind = CategoryKinds.TryGetValue(category, out var k) ? k
                    : category.StartsWith("SBA_", StringComparison.Ordinal)
                        ? EventKind.StateBasedAction
                        : EventKind.ZoneMove;

                var objId = FirstAffected(a);
                var name = objId is { } oid ? tracker.NameOf(oid) : null;
                if (name is not null && !name.StartsWith('#')) cardsSeen.Add(name);

                ev = Base(tracker, ts, kind) with
                {
                    SourceInstanceId = objId,
                    SourceName = name,
                    ActorSeat = objId is { } id2 ? tracker.Get(id2)?.ControllerSeat : null,
                    Detail = category
                };
            }
            else if (SimpleAnnotationKinds.TryGetValue(type, out var simple))
            {
                var affector = a.TryGetProperty("affectorId", out var af) && af.TryGetInt32(out var afv)
                    ? afv : (int?)null;
                var affected = FirstAffected(a);

                var sourceName = affector is { } s && s > 2 ? tracker.NameOf(s) : null;
                if (sourceName is not null && !sourceName.StartsWith('#')) cardsSeen.Add(sourceName);

                ev = Base(tracker, ts, simple) with
                {
                    ActorSeat = affector is { } s2 && s2 <= 2 ? s2 : null,
                    SourceInstanceId = affector,
                    SourceName = sourceName,
                    // Seats 1 and 2 are players; anything larger is an object instance id.
                    TargetSeat = affected is { } t2 && t2 <= 2 ? t2 : null,
                    TargetInstanceId = affected is { } t3 && t3 > 2 ? t3 : null,
                    TargetName = affected is { } t4 && t4 > 2 ? tracker.NameOf(t4) : null,
                    Amount = AmountFor(type, a)
                };
            }
            else
            {
                unknown[type] = unknown.GetValueOrDefault(type) + 1;
                ev = Base(tracker, ts, EventKind.Unknown) with { RawType = type };
            }

            events.Add(ev with { Seq = seq++ });
        }
    }

    private static int AmountFor(string type, JsonElement a) => type switch
    {
        "AnnotationType_DamageDealt" => GameStateTracker.DetailInt(a, "damage") ?? 0,
        "AnnotationType_ModifiedLife" => GameStateTracker.DetailInt(a, "life") ?? 0,
        "AnnotationType_CounterAdded" => GameStateTracker.DetailInt(a, "transaction_amount") ?? 0,
        "AnnotationType_CounterRemoved" => -(GameStateTracker.DetailInt(a, "transaction_amount") ?? 0),
        _ => 0
    };

    private static GameEvent Base(GameStateTracker t, long ts, EventKind kind) => new()
    {
        TimestampMs = ts,
        GameNumber = t.GameNumber,
        Turn = t.Turn,
        ActiveSeat = t.ActiveSeat,
        Phase = t.Phase,
        Step = t.Step,
        Kind = kind
    };

    private static int? FirstAffected(JsonElement a)
    {
        if (!a.TryGetProperty("affectedIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var v in ids.EnumerateArray())
            if (v.TryGetInt32(out var iv)) return iv;
        return null;
    }

    private static int? FirstSeat(JsonElement message)
    {
        if (!message.TryGetProperty("systemSeatIds", out var ids) ||
            ids.ValueKind != JsonValueKind.Array) return null;
        foreach (var v in ids.EnumerateArray())
            if (v.TryGetInt32(out var iv)) return iv;
        return null;
    }

    private static void ReadRoom(
        JsonElement info, Dictionary<int, PlayerInfo> seats, ref string eventName)
    {
        if (!info.TryGetProperty("gameRoomConfig", out var cfg) ||
            !cfg.TryGetProperty("reservedPlayers", out var players) ||
            players.ValueKind != JsonValueKind.Array) return;

        foreach (var p in players.EnumerateArray())
        {
            if (!p.TryGetProperty("systemSeatId", out var s) || !s.TryGetInt32(out var seat))
                continue;
            seats[seat] = new PlayerInfo(
                seat,
                p.TryGetProperty("userId", out var u) ? u.GetString() ?? "" : "",
                p.TryGetProperty("playerName", out var n) ? n.GetString() ?? "" : "",
                p.TryGetProperty("platformId", out var pl) ? pl.GetString() ?? "" : "");
            if (eventName.Length == 0 && p.TryGetProperty("eventId", out var e))
                eventName = e.GetString() ?? "";
        }
    }

    private static void ReadResults(
        JsonElement fmr, ref int? winningTeam, ref int team1Games, ref int team2Games)
    {
        if (!fmr.TryGetProperty("resultList", out var list) ||
            list.ValueKind != JsonValueKind.Array) return;

        foreach (var r in list.EnumerateArray())
        {
            var scope = r.TryGetProperty("scope", out var s) ? s.GetString() : null;
            if (!r.TryGetProperty("winningTeamId", out var w) || !w.TryGetInt32(out var team))
                continue;
            if (scope == "MatchScope_Match") winningTeam = team;
            else if (scope == "MatchScope_Game")
            {
                if (team == 1) team1Games++;
                else if (team == 2) team2Games++;
            }
        }
    }

    private static long ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var ts)) return 0;
        return ts.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(ts.GetString(), out var v) => v,
            JsonValueKind.Number when ts.TryGetInt64(out var v) => v,
            _ => 0
        };
    }
}
