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
            // PhaseOrStepModified is handled separately — its phase and step come
            // from the annotation's own details, not from tracker state.
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
        var lastTurn = 0;

        foreach (var raw in rawLines)
        {
            JsonElement root;
            try { root = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { continue; }

            var ts = ReadTimestamp(root);
            if (ts > 0) { if (started == 0) started = ts; ended = ts; }

            if (Json.Obj(root, "matchGameRoomStateChangedEvent") is { } room &&
                Json.Obj(room, "gameRoomInfo") is { } info)
            {
                ReadRoom(info, seatMeta, ref eventName);
                if (Json.Obj(info, "finalMatchResult") is { } fmr)
                {
                    sawFinal = true;
                    ReadResults(fmr, ref winningTeam, ref gamesForTeam1, ref gamesForTeam2);
                }
            }

            if (Json.Obj(root, "greToClientEvent") is not { } gre) continue;

            foreach (var m in Json.Array(gre, "greToClientMessages"))
            {
                var type = Json.Str(m, "type");

                if (type is "GREMessageType_MulliganReq" && localSeat is null)
                    localSeat = FirstSeat(m);
                else if (type is "GREMessageType_ActionsAvailableReq" && fallbackSeat is null)
                    fallbackSeat = FirstSeat(m);

                if (Json.Obj(m, "gameStateMessage") is not { } gsm) continue;

                tracker.Apply(gsm);
                EmitCombat(tracker, ts, ref seq, events, cardsSeen);
                foreach (var a in Json.Array(gsm, "annotations"))
                    EmitFor(a, tracker, ts, ref seq, ref lastTurn, events, unknown, cardsSeen);
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

    /// <summary>
    /// Emits attack and block events. Combat has no annotation of its own — it appears
    /// only as a state change on the creature — so it is read from the tracker's
    /// transition reports rather than from the annotation stream.
    /// </summary>
    private static void EmitCombat(
        GameStateTracker tracker, long ts, ref int seq,
        List<GameEvent> events, HashSet<string> cardsSeen)
    {
        foreach (var id in tracker.NewAttackers)
        {
            var obj = tracker.Get(id);
            var name = tracker.NameOf(id);
            if (!name.StartsWith('#')) cardsSeen.Add(name);

            var target = obj?.AttackTargetId;
            events.Add(Base(tracker, ts, EventKind.Attack) with
            {
                Seq = seq++,
                ActorSeat = obj?.ControllerSeat is > 0 ? obj.ControllerSeat : tracker.ActiveSeat,
                SourceInstanceId = id,
                SourceName = name,
                TargetSeat = target is <= 2 and > 0 ? target : null,
                TargetInstanceId = target is > 2 ? target : null,
                TargetName = target is > 2 ? tracker.NameOf(target.Value) : null
            });
        }

        foreach (var id in tracker.NewBlockers)
        {
            var obj = tracker.Get(id);
            var name = tracker.NameOf(id);
            if (!name.StartsWith('#')) cardsSeen.Add(name);

            var attacker = obj?.BlockedAttackerIds.FirstOrDefault();
            events.Add(Base(tracker, ts, EventKind.Block) with
            {
                Seq = seq++,
                ActorSeat = obj?.ControllerSeat,
                SourceInstanceId = id,
                SourceName = name,
                TargetInstanceId = attacker is > 0 ? attacker : null,
                TargetName = attacker is > 0 ? tracker.NameOf(attacker.Value) : null
            });
        }
    }

    private void EmitFor(
        JsonElement a, GameStateTracker tracker, long ts, ref int seq, ref int lastTurn,
        List<GameEvent> events, Dictionary<string, int> unknown, HashSet<string> cardsSeen)
    {
        foreach (var typeEl in Json.Array(a, "type"))
        {
            if (typeEl.ValueKind != JsonValueKind.String) continue;
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

                // A card the opponent draws has no game object, so its controller is
                // unknown; whoever's turn it is did it.
                var controller = objId is { } id2 ? tracker.Get(id2)?.ControllerSeat : null;

                ev = Base(tracker, ts, kind) with
                {
                    SourceInstanceId = objId,
                    SourceName = name,
                    ActorSeat = controller is > 0 ? controller : tracker.ActiveSeat,
                    Detail = category
                };
            }
            else if (type == "AnnotationType_PhaseOrStepModified")
            {
                // The phase and step live on the annotation itself; turnInfo often
                // omits them, so reading tracker state here yields "phase 0, step 0".
                var phase = GameStateTracker.DetailInt(a, "phase") ?? 0;
                var step = GameStateTracker.DetailInt(a, "step") ?? 0;
                var label = string.Join(" · ", new[]
                {
                    cards.EnumName("Phase", phase),
                    cards.EnumName("Step", step)
                }.Where(s => !string.IsNullOrWhiteSpace(s)));

                // Phase 0 / Step 0 are both blank — nothing to say.
                if (label.Length == 0) continue;

                ev = Base(tracker, ts, EventKind.PhaseChange) with
                {
                    Phase = phase,
                    Step = step,
                    Detail = label
                };
            }
            else if (SimpleAnnotationKinds.TryGetValue(type, out var simple))
            {
                var affector = Json.Int(a, "affectorId");
                var affected = FirstAffected(a);

                var sourceName = affector is { } s && s > 2 ? tracker.NameOf(s) : null;
                if (sourceName is not null && !sourceName.StartsWith('#')) cardsSeen.Add(sourceName);

                // affectorId is a seat for player actions and an object id otherwise;
                // for an object, credit its controller, else the active player.
                var actor = affector switch
                {
                    <= 2 and > 0 => affector,
                    > 2 => tracker.Get(affector.Value)?.ControllerSeat is > 0 and var c
                        ? c : tracker.ActiveSeat,
                    _ => tracker.ActiveSeat
                };

                ev = Base(tracker, ts, simple) with
                {
                    ActorSeat = actor,
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

            // turnInfo carries no turnNumber until the first turn is under way, so the
            // opening NewTurnStarted would otherwise land on "Turn 0".
            var turn = tracker.Turn > 0 ? tracker.Turn : Math.Max(lastTurn, 1);
            lastTurn = turn;

            events.Add(ev with { Seq = seq++, Turn = turn });
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
        foreach (var v in Json.Array(a, "affectedIds"))
            if (Json.Int(v) is { } iv) return iv;
        return null;
    }

    private static int? FirstSeat(JsonElement message)
    {
        foreach (var v in Json.Array(message, "systemSeatIds"))
            if (Json.Int(v) is { } iv) return iv;
        return null;
    }

    private static void ReadRoom(
        JsonElement info, Dictionary<int, PlayerInfo> seats, ref string eventName)
    {
        if (Json.Obj(info, "gameRoomConfig") is not { } cfg) return;

        foreach (var p in Json.Array(cfg, "reservedPlayers"))
        {
            if (Json.Int(p, "systemSeatId") is not { } seat) continue;
            seats[seat] = new PlayerInfo(
                seat,
                Json.Str(p, "userId") ?? "",
                Json.Str(p, "playerName") ?? "",
                Json.Str(p, "platformId") ?? "");
            if (eventName.Length == 0 && Json.Str(p, "eventId") is { } ev)
                eventName = ev;
        }
    }

    private static void ReadResults(
        JsonElement fmr, ref int? winningTeam, ref int team1Games, ref int team2Games)
    {
        foreach (var r in Json.Array(fmr, "resultList"))
        {
            var scope = Json.Str(r, "scope");
            if (Json.Int(r, "winningTeamId") is not { } team) continue;
            if (scope == "MatchScope_Match") winningTeam = team;
            else if (scope == "MatchScope_Game")
            {
                if (team == 1) team1Games++;
                else if (team == 2) team2Games++;
            }
        }
    }

    private static long ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var ts) ? Json.Long(ts) ?? 0 : 0;
}
