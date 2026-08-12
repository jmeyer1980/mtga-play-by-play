using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record PlayerInfo(int Seat, string UserId, string ScreenName, string Platform);

public sealed record Transcript(
    string MatchId, long StartedAtMs, long EndedAtMs, string EventName,
    PlayerInfo? You, PlayerInfo? Opponent,
    int? WinningTeamId, int GamesWon, int GamesLost, bool Incomplete,
    IReadOnlyList<GameEvent> Events,
    IReadOnlyDictionary<string, int> UnknownAnnotations,
    IReadOnlySet<string> CardsSeen,
    IReadOnlyDictionary<string, int> UnresolvedNames,
    /// <summary>
    /// Everything the log did not account for. Non-empty means this transcript is an
    /// incomplete record of the match and has to say so, which is a different claim
    /// from <c>Incomplete</c>: that one means the log stopped, this one means the log
    /// kept going and left things out of the middle.
    /// </summary>
    IReadOnlyList<LogGap> Gaps,

    /// <summary>
    /// The deck <see cref="You"/> registered for this match, sorted by name. Empty when
    /// the log did not carry one — which is every match archived before the slicer
    /// stopped discarding <c>ConnectResp</c>, and any match whose deck message named a
    /// seat other than the local player's.
    /// </summary>
    IReadOnlyList<DeckEntry> Deck,

    /// <summary>
    /// The die roll and the opening hands, or null when the log carried none of it.
    /// Kept off the event stream on purpose: these facts arrive scattered across the
    /// first few messages and are only complete once the first turn opens, so they
    /// cannot be emitted in sequence without disturbing the sequence numbers that the
    /// board, label and target passes all index by.
    /// </summary>
    Opening? Opening = null);

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
        ["Mill"] = EventKind.Milled,
        ["Surveil"] = EventKind.Surveilled,
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
            ["AnnotationType_RevealedCardCreated"] = EventKind.Revealed,
            ["AnnotationType_ManaPaid"] = EventKind.ManaPaid,
            // PhaseOrStepModified is handled separately — its phase and step come
            // from the annotation's own details, not from tracker state.
        };

    /// <summary>Annotations that carry no transcript value and are silently dropped.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "AnnotationType_ObjectIdChanged",       // consumed by the tracker as aliasing
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
        // A designation is an unnameable fact. Both carry nothing but a numeric
        // DesignationType, and unlike counters and phases that enum is in no table of
        // Arena's card database — so a line about one could say a permanent gained
        // something but never what. The 155 gains in the archive are also almost all
        // Room doors being unlocked, which the transcript already reports as a cast.
        "AnnotationType_GainDesignation",
        "AnnotationType_LoseDesignation",
        "AnnotationType_ChoiceResult",
        "AnnotationType_RevealedCardDeleted",
        "AnnotationType_DisqualifiedEffect",
        "AnnotationType_Shuffle",
    };

    /// <summary>
    /// Mutable state for one extraction run. Collected into an object because the
    /// emit helpers were accumulating ref parameters faster than they were gaining
    /// responsibilities.
    /// </summary>
    private sealed class Emit
    {
        public int Seq;
        public int LastTurn;
        public int LastTurnStarted;
        public readonly List<GameEvent> Events = [];
        public readonly Dictionary<string, int> Unknown = new(StringComparer.Ordinal);
        public readonly HashSet<string> CardsSeen = new(StringComparer.Ordinal);

        /// <summary>Last board text emitted per seat, so unchanged boards stay quiet.</summary>
        public readonly Dictionary<int, string> LastBoard = [];

        /// <summary>
        /// The creatures each board line lists, and the statline and flags already
        /// worked out for each, keyed by the line's sequence number. Held aside because
        /// only the tracker knows what was in play at that moment, while only the
        /// finished match knows which of them need telling apart by name.
        /// </summary>
        public readonly Dictionary<int, List<(int Id, string Stats)>> Boards = [];

        /// <summary>
        /// Appends an event and stamps it with its position, so <c>Events[n].Seq == n</c>.
        /// The deferred passes index straight into the list by sequence number and
        /// <see cref="Boards"/> is keyed by one, so that has to stay true.
        /// </summary>
        public void Add(GameEvent e) => Events.Add(e with { Seq = Seq++ });

        /// <summary>Names that could not be resolved, by how often each was emitted.</summary>
        public readonly Dictionary<string, int> Unresolved = new(StringComparer.Ordinal);

        /// <summary>
        /// A placeholder is counted rather than discarded. It still stays out of
        /// CardsSeen — nobody wants "unknown" matching every game in the search box —
        /// but silently dropping it left `mtga-pbp stats` structurally unable to
        /// report anything, since it looked for placeholders in the very set this
        /// method had already removed them from.
        /// </summary>
        public void SawCard(string? name)
        {
            if (name is null) return;
            if (CardNames.IsPlaceholder(name))
                Unresolved[name] = Unresolved.GetValueOrDefault(name) + 1;
            else
                CardsSeen.Add(name);
        }
    }

    public Transcript Extract(string matchId, IReadOnlyList<string> rawLines)
    {
        var tracker = new GameStateTracker(cards);
        var st = new Emit();
        var seatMeta = new Dictionary<int, PlayerInfo>();

        long started = 0, ended = 0;
        string eventName = "";
        int? localSeat = null, fallbackSeat = null, winningTeam = null;
        int gamesForTeam1 = 0, gamesForTeam2 = 0;
        var sawFinal = false;
        string? endReason = null;
        var gaps = new List<LogGap>();

        // Collected rather than resolved on sight: the deck message arrives before the
        // MulliganReq that says which seat is ours, so there is nothing to check it
        // against yet when it goes past.
        var decks = new List<(int? Seat, IReadOnlyList<int> GrpIds)>();

        // The opening, gathered as it goes past. The roll lands in the third message of
        // the match and the mulligans over the several after it, but who is on the play
        // is not settled until the first turn opens, so none of it can be turned into a
        // sentence here.
        var rolls = new List<DieRoll>();
        var mulligans = new Dictionary<int, int>();

        foreach (var raw in rawLines)
        {
            JsonElement root;
            try { root = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { continue; }

            // Recorded by the scanner in place of a message the log did not keep. It
            // carries no game state by definition, so it is collected and skipped.
            if (LogGaps.Read(root) is { } gap) { gaps.Add(gap); continue; }

            var ts = ReadTimestamp(root);
            if (ts > 0) { if (started == 0) started = ts; ended = ts; }

            if (Json.Obj(root, "matchGameRoomStateChangedEvent") is { } room &&
                Json.Obj(room, "gameRoomInfo") is { } info)
            {
                ReadRoom(info, seatMeta, ref eventName);
                if (Json.Obj(info, "finalMatchResult") is { } fmr)
                {
                    sawFinal = true;
                    ReadResults(fmr, ref winningTeam, ref gamesForTeam1, ref gamesForTeam2,
                                ref endReason);
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
                else if (type is "GREMessageType_ConnectResp" &&
                         DeckList.ReadMessage(m) is { } announced)
                    decks.Add(announced);
                // The first one only. A match carries exactly one in every archived log,
                // and if a re-roll ever produced a second it is the roll that was played
                // on that matters, not the one that was thrown out.
                else if (type is "GREMessageType_DieRollResultsResp" && rolls.Count == 0)
                    rolls.AddRange(Openings.ReadRolls(m));

                if (Json.Obj(m, "gameStateMessage") is not { } gsm) continue;

                // Only until the first turn number arrives. Mulligans are over by then —
                // all 29 increments in the archive land while the turn is still unset —
                // and stopping there keeps a later game's counts, in a Bo3 the archive
                // has yet to see, from being read as this game's opening hands.
                if (tracker.Turn == 0) Openings.ReadMulligans(gsm, mulligans);

                tracker.Apply(gsm, st.Seq);
                EmitCombat(tracker, ts, st);
                EmitLevels(tracker, ts, st);
                foreach (var a in Json.Array(gsm, "annotations"))
                    EmitFor(a, tracker, ts, st);
            }
        }

        // Turn boundaries, plus the end of the match so the last turn is looked at too.
        var boundaries = st.Events
            .Where(e => e.Kind == EventKind.TurnStart)
            .Select(e => e.Seq)
            .Append(st.Seq)
            .ToList();

        var labels = PermanentLabels.Build(tracker, cards, boundaries);
        NamePermanents(tracker, labels, st);
        NameBoards(tracker, labels, st);
        FillTargets(tracker, labels, st);

        var you = (localSeat ?? fallbackSeat) is { } seat && seatMeta.TryGetValue(seat, out var y)
            ? y : null;
        var opp = you is null ? null : seatMeta.Values.FirstOrDefault(p => p.Seat != you.Seat);
        tracker.LocalSeat = you?.Seat ?? 0;

        var yourTeam = you?.Seat;   // teamId equals seat in every observed match
        var won = yourTeam == 1 ? gamesForTeam1 : gamesForTeam2;
        var lost = yourTeam == 1 ? gamesForTeam2 : gamesForTeam1;

        if (sawFinal)
        {
            st.Add(new GameEvent
            {
                TimestampMs = ended,
                Kind = EventKind.GameEnd,
                Amount = winningTeam ?? 0,
                Detail = EndLine(winningTeam, yourTeam, endReason),
                RawType = endReason
            });
        }

        return new Transcript(
            matchId, started, ended, eventName, you, opp,
            winningTeam, won, lost, Incomplete: !sawFinal,
            st.Events, st.Unknown, st.CardsSeen, st.Unresolved, gaps,
            BuildDeck(decks, you, tracker), BuildOpening(rolls, mulligans, st));
    }

    /// <summary>
    /// The opening, or null when the log carried none of it.
    /// </summary>
    /// <remarks>
    /// Who is on the play is taken from the turn-one header this same run emitted,
    /// rather than worked out again from <c>turnInfo</c>. Two readings of the same fact
    /// can drift apart, and an opening that says "Opponent plays first" above a header
    /// reading "Turn 1 — You" would be worse than no opening at all. Reading the header
    /// also covers the one archived match whose <c>turnInfo</c> never reports a turn
    /// number: the turn is announced, the player concedes during the mulligan, and the
    /// extractor's own turn-one rule recovers the seat anyway.
    /// </remarks>
    private static Opening? BuildOpening(
        List<DieRoll> rolls, Dictionary<int, int> mulligans, Emit st)
    {
        int? firstPlayer = null;
        if (st.Events.FirstOrDefault(e => e.Kind == EventKind.TurnStart) is { } opener &&
            (opener.ActorSeat ?? opener.ActiveSeat) is > 0 and var seat)
            firstPlayer = seat;

        return rolls.Count > 0 || mulligans.Count > 0 || firstPlayer is not null
            ? new Opening(rolls, firstPlayer, mulligans)
            : null;
    }

    /// <summary>
    /// The decklist, attributed to the local seat or not shown at all.
    /// </summary>
    /// <remarks>
    /// Arena addresses the deck message to a seat, and it named the local player in all
    /// 35 occurrences across the current logs — but the one archived match where it
    /// disagreed had been mis-sliced, not mis-addressed, and that is exactly the case a
    /// reader must never be shown. So the seat is checked rather than trusted, and a
    /// disagreement drops the deck: no decklist is better than the wrong one.
    /// <para>
    /// The last message wins. A slice carries at most one today, and where two could
    /// ever reach the same match the later one is the nearer to it.
    /// </para>
    /// </remarks>
    private IReadOnlyList<DeckEntry> BuildDeck(
        List<(int? Seat, IReadOnlyList<int> GrpIds)> decks, PlayerInfo? you,
        GameStateTracker tracker)
    {
        if (you is null) return [];

        var mine = decks.LastOrDefault(d => d.Seat == you.Seat);
        if (mine.GrpIds is not { Count: > 0 } grpIds) return [];

        // Owning a game object is the client's own record of having held the card.
        // A card that stayed in the library the whole match never gets one, which is
        // precisely the distinction worth drawing.
        var seen = tracker.Objects.Values
            .Where(o => o.OwnerSeat == you.Seat && o.GrpId > 0)
            .Select(o => o.GrpId)
            .ToHashSet();

        return DeckList.Build(grpIds, cards, seen);
    }

    /// <summary>
    /// Emits attack and block events. Combat has no annotation of its own — it appears
    /// only as a state change on the creature — so it is read from the tracker's
    /// transition reports rather than from the annotation stream.
    /// </summary>
    private static void EmitCombat(GameStateTracker tracker, long ts, Emit st)
    {
        foreach (var id in tracker.NewAttackers)
        {
            var obj = tracker.Get(id);
            var name = tracker.NameOf(id);
            st.SawCard(name);

            var target = obj?.AttackTargetId;
            st.Add(Base(tracker, ts, EventKind.Attack) with
            {
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
            st.SawCard(name);

            var attacker = obj?.BlockedAttackerIds.FirstOrDefault();
            st.Add(Base(tracker, ts, EventKind.Block) with
            {
                ActorSeat = obj?.ControllerSeat,
                SourceInstanceId = id,
                SourceName = name,
                TargetInstanceId = attacker is > 0 ? attacker : null,
                TargetName = attacker is > 0 ? tracker.NameOf(attacker.Value) : null
            });
        }
    }

    /// <summary>
    /// Emits the level a Class enchantment reached. Read from the tracker's transitions
    /// for the same reason combat is: Arena states the level as a standing fact re-sent
    /// with every message, never as an event, so the annotation stream has nothing to
    /// key off.
    /// </summary>
    /// <remarks>
    /// The level is worth a line because nothing else on the page carries it. A class
    /// levels up by activating an ability that produces no trigger, and its consequences
    /// — "creature tokens you control get +2/+2" — land as a statline change on other
    /// permanents entirely, so a reader watching Toys go from 1/1 to 3/3 between two
    /// turns has no way at all to find out why.
    /// </remarks>
    private static void EmitLevels(GameStateTracker tracker, long ts, Emit st)
    {
        foreach (var (id, level) in tracker.NewLevels)
        {
            var name = tracker.NameOf(id);
            if (CardNames.IsPlaceholder(name)) continue;
            st.SawCard(name);

            st.Add(Base(tracker, ts, EventKind.LevelUp) with
            {
                ActorSeat = tracker.Get(id)?.ControllerSeat,
                SourceInstanceId = id,
                SourceName = name,
                Amount = level
            });
        }
    }

    /// <summary>
    /// One board line per player, describing what they control at the end of a turn.
    /// The line text is built here rather than in the renderer because only this layer
    /// has the tracker; the renderer still decides how to present it and which player
    /// label to use.
    /// </summary>
    private static void EmitBoardSnapshots(GameStateTracker tracker, long ts, int turn, Emit st)
    {
        foreach (var seat in new[] { 1, 2 })
        {
            var creatures = tracker.CreaturesOnBattlefield(seat);
            if (creatures.Count == 0) continue;

            var parts = creatures.Select(c =>
            {
                var text = "";
                if (c.Power is { } p && c.Toughness is { } t) text += $" {p}/{t}";

                var flags = new List<string>();
                if (c.Damage > 0) flags.Add($"{c.Damage} dmg");
                if (c.IsTapped) flags.Add("tapped");
                if (flags.Count > 0) text += $" ({string.Join(", ", flags)})";
                return (c.InstanceId, Stats: text);
            }).ToList();

            // A board that has not moved since the last turn tells you nothing, and
            // repeating it verbatim is most of the noise these lines can generate. The
            // comparison uses the bare names: the letters a permanent may pick up later
            // are stable, so they never make an unchanged board look changed.
            var detail = string.Join(", ",
                parts.Select(p => tracker.NameOf(p.InstanceId) + p.Stats));
            if (st.LastBoard.TryGetValue(seat, out var previous) && previous == detail) continue;
            st.LastBoard[seat] = detail;

            var seq = st.Seq;
            st.Add(new GameEvent
            {
                TimestampMs = ts,
                GameNumber = tracker.GameNumber,
                Turn = turn,
                Kind = EventKind.BoardSnapshot,
                ActorSeat = seat,
                Detail = detail
            });
            st.Boards[seq] = parts;
        }
    }

    private void EmitFor(JsonElement a, GameStateTracker tracker, long ts, Emit st)
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
                st.SawCard(name);

                // A card the opponent draws has no game object, so its controller is
                // unknown; whoever's turn it is did it.
                var controller = objId is { } id2 ? tracker.Get(id2)?.ControllerSeat : null;

                // What caused the move. Present on every Destroy, Exile, Return, Mill
                // and Countered in the sample archive, and it is the difference
                // between "Hare Apparent is destroyed" and "Split Up destroys Hare
                // Apparent". Seats 1 and 2 are players, not objects.
                var cause = Json.Int(a, "affectorId");
                var causeName = cause is > 2 ? tracker.NameOf(cause.Value) : null;
                // Counted before it is suppressed. An unnameable cause is dropped from
                // the sentence — "Hare Apparent is destroyed" beats naming a cause we
                // cannot name — but it is still a resolution gap worth reporting.
                st.SawCard(causeName);
                if (CardNames.IsPlaceholder(causeName)) causeName = null;

                ev = Base(tracker, ts, kind) with
                {
                    SourceInstanceId = objId,
                    SourceName = name,
                    ActorSeat = controller is > 0 ? controller : tracker.ActiveSeat,
                    CauseInstanceId = causeName is null ? null : cause,
                    CauseName = causeName,
                    Detail = category
                };
            }
            else if (type == "AnnotationType_AbilityInstanceCreated")
            {
                // The ability object resolves to "<source card>'s ability" through its
                // objectSourceGrpId. Only worth a line when we can name the source.
                var abilityId = FirstAffected(a);
                var abilityName = abilityId is { } aid ? tracker.NameOf(aid) : null;
                if (CardNames.IsPlaceholder(abilityName)) continue;

                ev = Base(tracker, ts, EventKind.Triggered) with
                {
                    SourceInstanceId = abilityId,
                    SourceName = abilityName
                };
            }
            else if (type == "AnnotationType_AttachmentCreated")
            {
                // affectorId is the aura or equipment; the single affected id is what it
                // went onto.
                if (Json.Int(a, "affectorId") is not { } attachment) continue;
                if (FirstAffected(a) is not { } host) continue;

                // An aura needs no line: it was cast at the creature, so the transcript
                // already reads "You cast Ethereal Armor, targeting Rabbit (1/1 → 5/5)",
                // and saying it again immediately afterwards would be the same fact
                // twice. Equipment is moved by an activated ability — equip is not a
                // cast — so nothing on the page says which creature is carrying it, and
                // the statline it explains changes with no visible reason. All 136
                // attachments in the archive that already had a target are auras, and
                // all 23 that did not are equipment, so the rule sorts them exactly.
                if (tracker.TargetsOf(attachment).Any(
                        t => tracker.Resolve(t) == tracker.Resolve(host)))
                    continue;

                var attachmentName = tracker.NameOf(attachment);
                var hostName = tracker.NameOf(host);
                // "Unknown card is attached to Hare Apparent" tells nobody anything.
                if (CardNames.IsPlaceholder(attachmentName) ||
                    CardNames.IsPlaceholder(hostName)) continue;

                st.SawCard(attachmentName);
                st.SawCard(hostName);

                ev = Base(tracker, ts, EventKind.Attached) with
                {
                    SourceInstanceId = attachment,
                    SourceName = attachmentName,
                    TargetInstanceId = host,
                    TargetName = hostName
                };
            }
            else if (type == "AnnotationType_Scry")
            {
                var top = GameStateTracker.DetailInts(a, "topIds");
                var bottom = GameStateTracker.DetailInts(a, "bottomIds");

                // Our own cards are named; the opponent's are hidden, so say only how
                // many rather than inventing detail we do not have.
                string Describe(IReadOnlyList<int> ids, string where)
                {
                    var names = ids.Select(tracker.NameOf)
                                   .Where(n => !CardNames.IsPlaceholder(n)).ToList();
                    if (names.Count == ids.Count && names.Count > 0)
                    {
                        foreach (var n in names) st.SawCard(n);
                        return $"{string.Join(", ", names)} to the {where}";
                    }
                    return ids.Count == 1 ? $"1 card to the {where}"
                                          : $"{ids.Count} cards to the {where}";
                }

                var parts = new List<string>();
                if (top.Count > 0) parts.Add(Describe(top, "top"));
                if (bottom.Count > 0) parts.Add(Describe(bottom, "bottom"));

                var actor = Json.Int(a, "affectorId") is { } af && af > 2
                    ? tracker.Get(af)?.ControllerSeat
                    : null;

                ev = Base(tracker, ts, EventKind.Scry) with
                {
                    ActorSeat = actor is > 0 ? actor : tracker.ActiveSeat,
                    Amount = top.Count + bottom.Count,
                    Detail = parts.Count > 0 ? string.Join(", ", parts) : null
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
                st.SawCard(sourceName);

                // affectorId is a seat for player actions and an object id otherwise;
                // for an object, credit its controller, else the active player.
                var actor = affector switch
                {
                    <= 2 and > 0 => affector,
                    > 2 => tracker.Get(affector.Value)?.ControllerSeat is > 0 and var c
                        ? c : tracker.ActiveSeat,
                    _ => tracker.ActiveSeat
                };

                // Arena numbers counter kinds; the card database names them, so a
                // planeswalker gains "1 Loyalty counter" rather than "1 counter".
                var counterName = simple == EventKind.CounterChanged
                    ? cards.EnumName("CounterType", GameStateTracker.DetailInt(a, "counter_type") ?? 0)
                    : null;

                ev = Base(tracker, ts, simple) with
                {
                    ActorSeat = actor,
                    Detail = counterName,
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
                st.Unknown[type] = st.Unknown.GetValueOrDefault(type) + 1;
                ev = Base(tracker, ts, EventKind.Unknown) with { RawType = type };
            }

            // turnInfo carries no turnNumber until the first turn is under way, so the
            // opening NewTurnStarted would otherwise land on "Turn 0".
            var turn = tracker.Turn > 0 ? tracker.Turn : Math.Max(st.LastTurn, 1);

            st.LastTurn = turn;

            if (ev.Kind == EventKind.TurnStart)
            {
                // Compare against the last turn we opened, not the last turn seen:
                // a phase change in the same message already advanced lastTurn to the
                // new turn, so testing that would never fire.
                if (st.LastTurnStarted > 0 && turn != st.LastTurnStarted)
                    EmitBoardSnapshots(tracker, ts, st.LastTurnStarted, st);
                st.LastTurnStarted = turn;

                ev = ev with
                {
                    LifeSeat1 = tracker.Life.GetValueOrDefault(1),
                    LifeSeat2 = tracker.Life.GetValueOrDefault(2)
                };
            }

            st.Add(ev with { Turn = turn });
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
        JsonElement fmr, ref int? winningTeam, ref int team1Games, ref int team2Games,
        ref string? reason)
    {
        foreach (var r in Json.Array(fmr, "resultList"))
        {
            var scope = Json.Str(r, "scope");
            // How it ended: a concede, a timeout, or actually losing the game. Most
            // matches end in a concede, which "wins the match" hides entirely.
            if (Json.Str(r, "reason") is { } why && why != "ResultReason_Game")
                reason ??= why;
            if (Json.Int(r, "winningTeamId") is not { } team) continue;
            if (scope == "MatchScope_Match") winningTeam = team;
            else if (scope == "MatchScope_Game")
            {
                if (team == 1) team1Games++;
                else if (team == 2) team2Games++;
            }
        }
    }

    /// <summary>
    /// Re-names every permanent an event mentions, now that the whole match is known.
    /// Deferred for the same reason <see cref="FillTargets"/> is: whether two Rabbits
    /// need telling apart is a fact about the match, not about the message the line came
    /// out of. <see cref="PermanentLabels"/> holds the rule.
    /// </summary>
    private static void NamePermanents(
        GameStateTracker tracker, PermanentLabels labels, Emit st)
    {
        for (var i = 0; i < st.Events.Count; i++)
        {
            var e = st.Events[i];
            // A counter event reports the change itself, so it names the size the
            // permanent was changed FROM. Everything else describes a permanent as it
            // stands at that moment.
            var before = e.Kind == EventKind.CounterChanged;

            var source = Named(tracker, labels, e.SourceInstanceId, e.SourceName, e.Seq, before);
            var target = Named(tracker, labels, e.TargetInstanceId, e.TargetName, e.Seq, before);
            var cause = Named(tracker, labels, e.CauseInstanceId, e.CauseName, e.Seq, before);

            if (source == e.SourceName && target == e.TargetName && cause == e.CauseName)
                continue;

            st.Events[i] = e with
            {
                SourceName = source,
                TargetName = target,
                CauseName = cause
            };
        }
    }

    /// <summary>
    /// The label for one role on one event, or the name untouched when there is nothing
    /// to add to it.
    /// </summary>
    private static string? Named(
        GameStateTracker tracker, PermanentLabels labels, int? instanceId, string? name, int seq,
        bool before = false)
    {
        // Seats 1 and 2 are players, not objects. A name the emitter composed rather
        // than looked up — "Carrot Cake's ability" reached through a different path, or
        // a placeholder — is left exactly as it was.
        if (instanceId is not { } id || id <= 2 || name is null) return name;
        if (!string.Equals(name, tracker.NameOf(id), StringComparison.Ordinal)) return name;
        return before ? labels.LabelBefore(id, seq) : labels.Label(id, seq);
    }

    /// <summary>
    /// Rebuilds the end-of-turn board lines with the letters that tell same-named
    /// creatures apart. The statlines and flags were worked out when the line was
    /// emitted, because only then was that state still around to read.
    /// </summary>
    private static void NameBoards(GameStateTracker tracker, PermanentLabels labels, Emit st)
    {
        foreach (var (seq, creatures) in st.Boards)
        {
            st.Events[seq] = st.Events[seq] with
            {
                Detail = string.Join(", ", creatures.Select(c =>
                    tracker.NameOf(c.Id) + labels.Suffix(c.Id, seq) + c.Stats))
            };
        }
    }

    /// <summary>
    /// Attaches targets to spells once the whole match has been read, and says what the
    /// spell did to them. TargetSpec usually arrives in a later message than the cast it
    /// belongs to, so this cannot be done while the cast is being emitted — at that
    /// moment the target is not yet known, and neither is what it became.
    /// </summary>
    private static void FillTargets(GameStateTracker tracker, PermanentLabels labels, Emit st)
    {
        for (var i = 0; i < st.Events.Count; i++)
        {
            var e = st.Events[i];
            if (e.Kind != EventKind.SpellCast || e.SourceInstanceId is not { } id) continue;

            var targets = tracker.TargetsOf(id);
            if (targets.Count == 0) continue;

            // Layers are applied in the message that carries the resolution, not in the
            // one that carries the cast, so the "after" only exists from there on.
            var settled = ResolvedAt(tracker, st, e) ?? e.Seq;

            var named = new List<string>();
            foreach (var target in targets)
            {
                var name = tracker.NameOf(target);
                if (CardNames.IsPlaceholder(name)) continue;
                st.SawCard(name);
                named.Add(labels.Buff(target, e.Seq, settled));
            }
            if (named.Count == 0) continue;

            st.Events[i] = e with { TargetName = string.Join(" and ", named) };
        }
    }

    /// <summary>
    /// Where in the event stream this spell finished resolving. The cast and the
    /// resolution are two annotations about the same object, so they are matched on its
    /// instance id through the alias map. Null when it never resolved — countered, or a
    /// log that stops mid-match — in which case there is no "after" to report.
    /// </summary>
    private static int? ResolvedAt(GameStateTracker tracker, Emit st, GameEvent cast)
    {
        if (cast.SourceInstanceId is not { } id) return null;
        var spell = tracker.Resolve(id);

        for (var i = cast.Seq + 1; i < st.Events.Count; i++)
        {
            var e = st.Events[i];
            if (e.Kind == EventKind.Resolved && e.SourceInstanceId is { } other &&
                tracker.Resolve(other) == spell)
                return e.Seq;
        }
        return null;
    }

    /// <summary>How the match ended, naming a concede or timeout rather than hiding it.</summary>
    private static string? EndLine(int? winningTeam, int? yourTeam, string? reason)
    {
        if (winningTeam is null) return null;
        var youWon = winningTeam == yourTeam;
        var loser = youWon ? "Opponent" : "You";
        var verb = youWon ? "concedes" : "concede";

        return reason switch
        {
            "ResultReason_Concede" => $"{loser} {verb} — {(youWon ? "you win" : "opponent wins")} the match",
            "ResultReason_Timeout" => $"{loser} {(youWon ? "runs" : "run")} out of time — {(youWon ? "you win" : "opponent wins")} the match",
            _ => youWon ? "You win the match" : "Opponent wins the match"
        };
    }

    private static long ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var ts) ? Json.Long(ts) ?? 0 : 0;
}
