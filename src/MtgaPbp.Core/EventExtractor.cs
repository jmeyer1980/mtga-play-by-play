using System.Text;
using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record PlayerInfo(int Seat, string UserId, string ScreenName, string Platform);

/// <summary>
/// One game of a match. A Bo1 has exactly one of these; a Bo3 has two or three, each
/// with its own opening, its own turn one and its own result.
/// </summary>
/// <remarks>
/// Arena numbers turns from one again in every game and hands out instance ids that
/// collide with the previous game's, so almost nothing about a game is meaningful
/// outside it. That is what this record is for: it is the unit the transcript is
/// actually divided into, and the reason the renderer can say "Turn 1" twice on one
/// page without lying.
/// </remarks>
public sealed record GameRecord(
    int Number,

    /// <summary>
    /// How this game began. Games after the first have no die roll — the loser of the
    /// previous game chooses who begins — so the opening for one of those carries a
    /// <see cref="Opening.ChoosingSeat"/> instead of rolls.
    /// </summary>
    Opening? Opening,

    /// <summary>The highest turn number this game reached, counting from its own turn one.</summary>
    int Turns,

    /// <summary>
    /// Which team won this game, from Arena's own per-game result, or null when the log
    /// stopped before it said. Team id equals seat in every observed match.
    /// </summary>
    int? WinningTeamId,

    /// <summary>
    /// How this game ended, in the same words the match-end line uses — "You concede —
    /// opponent wins game 1". Null when the result is unknown. Rendered only at a
    /// boundary between games: for the last game the match-end line already says it,
    /// and a single-game match would only be told twice.
    /// </summary>
    string? ResultLine);

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
    /// The die roll and the opening hands of the <em>first</em> game, or null when the
    /// log carried none of it. Kept off the event stream on purpose: these facts arrive
    /// scattered across the first few messages and are only complete once the first turn
    /// opens, so they cannot be emitted in sequence without disturbing the sequence
    /// numbers that the board, label and target passes all index by.
    /// </summary>
    Opening? Opening = null)
{
    /// <summary>
    /// One record per game the log carried, in the order they were played. Always at
    /// least one for a transcript that came out of the extractor; empty only on a
    /// transcript built by hand, which is why the renderers treat "no records" as
    /// "one game" rather than as "no games".
    /// </summary>
    public IReadOnlyList<GameRecord> Games { get; init; } = [];

    /// <summary>
    /// The commanders registered beside <see cref="Deck"/>, by name, in registration
    /// order. Separate from the deck because a commander is not a library card: it
    /// begins in the command zone, is cast from there, and returns there — rendered as
    /// a decklist row it would read as a card that could be drawn. A list because
    /// Arena's own <c>deckConstraintInfo</c> for Brawl allows two (partner
    /// commanders). Empty when the deck message carried no <c>commanderCards</c>,
    /// which is every non-Brawl format and every match archived before the slicer
    /// kept <c>ConnectResp</c> — so absence means "no commander recorded", never
    /// "this deck had no commander".
    /// </summary>
    public IReadOnlyList<string> Commanders { get; init; } = [];

    /// <summary>
    /// The colour identity of <see cref="Deck"/> as WUBRG letters — see
    /// <see cref="MtgaPbp.Core.DeckColors"/> for how it is derived and what "C" means.
    /// Null when the log carried no deck, which is the same set of matches
    /// <see cref="Deck"/> is empty for.
    /// </summary>
    /// <remarks>
    /// Worked out here rather than by whoever renders it, because here is the last place
    /// the grpIds still exist: <see cref="Deck"/> is names and counts, <see
    /// cref="Commanders"/> is names, and the card database can only be asked about ids.
    /// </remarks>
    public string? DeckColors { get; init; }

    /// <summary>
    /// Arena said <c>ResultType_Draw</c> for the match, in so many words. Only that:
    /// a completed match with no winner found is not a draw, it is a bug, and an
    /// incomplete one is <see cref="Incomplete"/> — so this flag never stands in for
    /// either. Carried separately from <see cref="WinningTeamId"/> because a draw is
    /// a result, not the absence of one.
    /// </summary>
    public bool Drawn { get; init; }

    /// <summary>
    /// Types found in <c>gameStateMessage.persistentAnnotations</c> that nothing reads
    /// and nobody has ruled out, counted once per distinct fact. Diagnostic only: none
    /// of it reaches the transcript.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="UnknownAnnotations"/> because the two surfaces are
    /// separate, and folding them together would let a persistent type that nobody has
    /// examined look like a streamed one that was. An init property rather than another
    /// positional parameter so that adding the inventory did not disturb every caller.
    /// </remarks>
    public IReadOnlyDictionary<string, int> UnknownPersistentAnnotations { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

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
        "AnnotationType_UserActionTaken",       // consumed by MarkActivations as attribution
        "AnnotationType_ResolutionStart",
        "AnnotationType_ResolutionComplete",
        "AnnotationType_LayeredEffectCreated",
        "AnnotationType_LayeredEffectDestroyed",
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
        "AnnotationType_LoseDesignation",
        "AnnotationType_ChoiceResult",
        "AnnotationType_RevealedCardDeleted",
        "AnnotationType_DisqualifiedEffect",
        "AnnotationType_Shuffle",
    };

    /// <summary>
    /// Persistent annotation types something already reads. Listed rather than inferred,
    /// because the code that reads them lives in <see cref="GameStateTracker"/> and this
    /// class would otherwise have no way to tell a handled type from an unmined one.
    /// </summary>
    private static readonly HashSet<string> PersistentHandled = new(StringComparer.Ordinal)
    {
        "AnnotationType_TargetSpec",        // what a spell or ability was aimed at
        "AnnotationType_ClassLevel",        // the level a Class enchantment reached
        "AnnotationType_TriggeringObject",  // what set a triggered ability off
        "AnnotationType_AddAbility",        // an ability granted — first strike, flying
        "AnnotationType_CopiedObject",      // a permanent became a copy of a card
    };

    /// <summary>
    /// Persistent annotation types examined and deliberately dropped. The counts are
    /// distinct facts across the 170-match archive, measured the same way
    /// <see cref="Emit.CountPersistent"/> counts.
    /// </summary>
    /// <remarks>
    /// Anything not named here and not in <see cref="PersistentHandled"/> is counted as
    /// unaccounted and shows up in <c>mtga-pbp stats</c>. That is the point of the split:
    /// a type nobody has looked at yet must not be able to hide behind one that was.
    /// </remarks>
    private static readonly HashSet<string> PersistentIgnored = new(StringComparer.Ordinal)
    {
        // The standing twins of things the transcript already narrates as they happen.
        // Each of these is Arena restating a fact the streamed annotation stream already
        // delivered as an event, so reporting them would be saying everything twice.
        "AnnotationType_EnteredZoneThisTurn",   // 10,339 — every zone move is reported
        "AnnotationType_ModifiedPower",         //    947 — statlines and before→after buffs
        "AnnotationType_ModifiedToughness",     //    899
        "AnnotationType_Counter",               //    486 — counters are named by kind
        "AnnotationType_LayeredEffect",         //  1,308 — the streamed twin is ignored too
        "AnnotationType_DamagedThisTurn",       //    155 — damage is reported when it lands

        // Exactly the same 175 (attachment, host) pairs as the streamed
        // AnnotationType_AttachmentCreated the extractor already handles — 175 in both,
        // none on either side alone. There is no attachment here that is not already on
        // the page or already deliberately suppressed as an aura.
        "AnnotationType_Attachment",

        // Unnameable, for the same reason the streamed GainDesignation was dropped: the
        // payload is a bare DesignationType int (19 and 20 account for 164 of the 188)
        // and Arena's card database has no enum table for it — the Enums table carries
        // CardColor, CardType, Color, CounterType, MatchState, Phase, Step, SubType and
        // SuperType, and nothing else. A line could say a permanent gained something but
        // never what. QualificationType is a bare int in the same way.
        "AnnotationType_Designation",
        "AnnotationType_Qualification",

        // Says the loser was at zero life. Every one of the 47 gives the same reason,
        // SBA_LifeTotal, and all 170 archived matches carry a finalMatchResult as well,
        // so it never recovers a result the transcript is missing — and the turn headers
        // already carry the life totals that got there.
        "AnnotationType_LossOfGame",

        // UsesRemaining is 0 in all 191: the client greying out an ability that has been
        // used this turn. The activation that used it up is already on the page.
        "AnnotationType_AbilityExhausted",

        // A permanent that will not last: the four ability ids behind the 76 read
        // "Sacrifice it at end of combat", "Sacrifice them at the beginning of the next
        // end step", a delayed return, and Evoke. The transcript reports the sacrifice
        // or the exile when it actually happens, which is the part a reader can act on.
        "AnnotationType_TemporaryPermanent",

        // A type-changing layered effect. 76 of the 83 carry nothing but effect ids;
        // only 7 carry the temporaryCardType that could be turned into words.
        "AnnotationType_ModifiedType",

        // Which colours of mana a land can produce. Bookkeeping for the client's mana
        // picker: "Forest can produce green" is not news to anybody reading a transcript.
        "AnnotationType_ColorProduction",

        // Carries no object at all — affectedIds is [0] and the affector is a synthetic
        // id in the 9000s. The only real payload is which player is choosing.
        "AnnotationType_ObjectsSelected",
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
        public readonly Dictionary<string, int> UnknownPersistent = new(StringComparer.Ordinal);
        public readonly HashSet<string> CardsSeen = new(StringComparer.Ordinal);

        /// <summary>
        /// Persistent annotations already counted this game, keyed by id and the objects
        /// they name. Arena re-sends the whole persistent set with almost every message,
        /// so without this the inventory reports how chatty the log is rather than how
        /// much of it is unmined: EnteredZoneThisTurn alone arrives 10,481 times across
        /// the archive to describe 10,339 distinct facts, and the ratio is far worse per
        /// match.
        /// </summary>
        /// <remarks>
        /// The id alone is not enough. Arena hands the same id back with different
        /// contents once the fact it stands for changes — the "entered this turn" set
        /// for a zone keeps its id and swaps its members every turn — so the objects go
        /// in the key too. Details are left out: a counter annotation counting up from
        /// one to three is one standing fact, not three.
        /// </remarks>
        public readonly HashSet<string> PersistentSeen = new(StringComparer.Ordinal);

        /// <summary>Last board text emitted per seat, so unchanged boards stay quiet.</summary>
        public readonly Dictionary<int, string> LastBoard = [];

        /// <summary>
        /// Forgets everything that was only true of the game that just ended. Turn
        /// numbers restart at one in every game, so a carried-over <see cref="LastTurn"/>
        /// is what made a second game's turn one render as a thirteenth turn; and a
        /// carried-over <see cref="LastBoard"/> could silence a new game's first board
        /// line for matching a board that no longer exists.
        /// </summary>
        public void StartGame()
        {
            LastTurn = 0;
            LastTurnStarted = 0;
            LastBoard.Clear();
            // Persistent annotation ids are handed out afresh per game: 96 of the 121
            // distinct ids in the archive's one Bo3 appear in both of its games. Keeping
            // the set across a boundary would silently drop game two's inventory.
            PersistentSeen.Clear();
        }

        /// <summary>
        /// Counts one persistent annotation against the inventory, once per distinct
        /// fact, skipping the types something already reads and the types that were
        /// examined and dropped.
        /// </summary>
        public void CountPersistent(JsonElement pa)
        {
            // Built before the type check rather than after, because one annotation can
            // carry several types and they have to agree on whether it was already seen.
            var key = new StringBuilder()
                .Append(Json.Int(pa, "id") ?? -1).Append('/')
                .Append(Json.Int(pa, "affectorId") ?? -1).Append(':');
            foreach (var x in Json.Array(pa, "affectedIds"))
                key.Append(Json.Int(x) ?? -1).Append(',');
            if (!PersistentSeen.Add(key.ToString())) return;

            foreach (var typeEl in Json.Array(pa, "type"))
            {
                if (typeEl.ValueKind != JsonValueKind.String) continue;
                var type = typeEl.GetString();
                if (type is null || PersistentHandled.Contains(type) ||
                    PersistentIgnored.Contains(type)) continue;
                UnknownPersistent[type] = UnknownPersistent.GetValueOrDefault(type) + 1;
            }
        }

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

    /// <summary>
    /// One game's worth of extraction state: its own tracker, its own opening, and where
    /// its events sit in the match-wide stream.
    /// </summary>
    /// <remarks>
    /// The tracker is per game and not per match because Arena reuses instance ids across
    /// the games of a Bo3 — 58 of the game objects in the one Bo3 the archive holds are
    /// described under an id the other game also used. One tracker for the whole match
    /// therefore ends up holding game two's objects under game one's ids,
    /// and since <see cref="NamePermanents"/>, <see cref="NameBoards"/> and
    /// <see cref="FillTargets"/> all run after the last message and re-read the tracker,
    /// every one of those passes named game one's cards after game two's. That is how a
    /// Swamp came to be reported as a 6/6.
    /// </remarks>
    private sealed class GameRun(int number, GameStateTracker tracker, int firstSeq)
    {
        public int Number { get; } = number;
        public GameStateTracker Tracker { get; } = tracker;

        /// <summary>Sequence number of this game's first event.</summary>
        public int FirstSeq { get; } = firstSeq;

        /// <summary>One past this game's last event, set when the game closes.</summary>
        public int EndSeq { get; set; }

        public readonly List<DieRoll> Rolls = [];
        public readonly Dictionary<int, int> Mulligans = [];

        /// <summary>
        /// Ability instances a player deliberately activated, mapped to the seat that
        /// acted, under the id Arena used at the time. From
        /// <c>AnnotationType_UserActionTaken</c> with an actionType of 2 — whose
        /// affector, unlike most affectors, really is the seat on every one in the
        /// archive. Kept raw and resolved only when the game closes, because the
        /// activation and the ability's creation arrive in different messages for a
        /// quarter of the archive and the alias map is not complete until the end.
        /// </summary>
        public readonly Dictionary<int, int> Activations = [];

        /// <summary>
        /// The last statline seen for each permanent, so a change that no annotation
        /// explains can be noticed. Per game, because instance ids are handed out again.
        /// </summary>
        public readonly Dictionary<int, (int Power, int Toughness)> LastStats = [];

        /// <summary>
        /// Every annotation this game has already narrated, as its exact JSON.
        /// </summary>
        private readonly HashSet<string> _narrated = new(StringComparer.Ordinal);

        /// <summary>
        /// Records an annotation and says whether this game had already been told it.
        /// </summary>
        /// <remarks>
        /// Arena re-sends state mid-game — a reconnect, a client hiccup — and the resync
        /// carries annotations it has already sent. Nothing downstream could tell the
        /// difference, so a land drop narrated twice became "Opponent plays Plains ×2",
        /// which is not a tidiness problem: one land a turn is a rule, so the page was
        /// describing something that cannot happen (#52).
        /// <para>
        /// Keyed on the whole annotation and not on its id, which is the trap here. Ids
        /// are NOT unique within a game: across the archive 944 (game, id) pairs recur,
        /// and while 708 are byte-identical replays, 236 carry different content — a
        /// different affector, a different affected object — and are genuinely separate
        /// events. Deduplicating on the id alone would have silently dropped those 236.
        /// </para>
        /// <para>
        /// Nor on how recently the annotation was seen, which was the other tempting
        /// rule: replays sit a median of 2 messages from their original, but genuine
        /// id reuse sits a median of 4, and 233 of those 236 fall inside the same
        /// twelve-message window. Distance cannot separate them.
        /// </para>
        /// <para>
        /// And content alone is not enough either, which is why only a resync may act
        /// on this. An annotation is not self-describing: it names objects by id, and
        /// ids are reassigned as the game runs, so the same bytes can mean two different
        /// things. One archived match sends a byte-identical block twice, a few messages
        /// apart, and it renders as "You cast Grab the Prize" the first time and "You
        /// cast Campus Guide" the second, because <c>ObjectIdChanged</c> remapped the
        /// object in between. Both are real. Silencing the second on content alone lost
        /// a cast and left its resolution standing on its own.
        /// </para>
        /// <para>
        /// A <c>GameStateType_Full</c> is the one message that is a re-send by
        /// definition, so it is the only one allowed to be silenced by this memory.
        /// That covers 561 of the archive's 708 identical repeats and leaves the
        /// Diff-to-Diff ones alone, which is the trade this evidence supports.
        /// </para>
        /// <para>
        /// Living on the game rather than the extractor is what resets it correctly.
        /// Instance ids and annotation ids are both handed out again in game two, and a
        /// <see cref="GameRun"/> is built fresh for each game — so the set clears when a
        /// game does and never when a resync arrives, which is the other half of the
        /// trap: clearing on a resync is what would make the replay look new again.
        /// </para>
        /// </remarks>
        public bool AlreadyTold(JsonElement annotation) =>
            !_narrated.Add(annotation.GetRawText());
    }

    /// <summary>What Arena said about one finished game.</summary>
    private readonly record struct GameOutcome(int? WinningTeamId, string? Reason);

    public Transcript Extract(string matchId, IReadOnlyList<string> rawLines)
    {
        var st = new Emit();
        var seatMeta = new Dictionary<int, PlayerInfo>();

        long started = 0, ended = 0;
        string eventName = "";
        int? localSeat = null, fallbackSeat = null, winningTeam = null;
        int gamesForTeam1 = 0, gamesForTeam2 = 0;
        var sawFinal = false;
        var drawn = false;
        string? endReason = null;
        var gaps = new List<LogGap>();

        // Collected rather than resolved on sight: the deck message arrives before the
        // MulliganReq that says which seat is ours, so there is nothing to check it
        // against yet when it goes past.
        var decks = new List<(int? Seat, IReadOnlyList<int> GrpIds, IReadOnlyList<int> Commanders)>();

        // How each finished game went, in game order. Arena states this twice: the
        // gameInfo of every message carries the results so far, and finalMatchResult
        // repeats the lot at the end. Both are read and the longer list kept, so a match
        // whose log stops in the middle still knows how its earlier games went.
        var outcomes = new List<GameOutcome>();

        // The games, each with its own tracker and its own opening. The first is opened
        // here rather than on sight of a gameNumber, because the die roll arrives in the
        // same envelope as the first game state and nothing has said "game one" yet.
        var games = new List<GameRun> { new(1, new GameStateTracker(cards), 0) };
        var game = games[0];

        foreach (var raw in rawLines)
        {
            JsonElement root;
            try { root = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { continue; }

            // Recorded by the scanner in place of a message the log did not keep. It
            // carries no game state by definition, so there is nothing to apply — but
            // where it fell is worth saying, and where it fell is simply where the walk
            // had got to. The warning above a transcript already reports that something
            // is missing; this puts it at the point it went missing, which is the part a
            // reader can act on and the part a bug report needs (#55).
            if (LogGaps.Read(root) is { } gap)
            {
                gap = gap with { Turn = game.Tracker.Turn, Game = game.Number };
                gaps.Add(gap);
                st.Add(Base(game.Tracker, ended, EventKind.LogGap) with
                {
                    Detail = GapLine(gap),
                    RawType = gap.Kind.ToString()
                });
                continue;
            }

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
                                ref endReason, ref drawn);
                    var final = ReadGameOutcomes(fmr, "resultList");
                    if (final.Count > outcomes.Count) outcomes = final;
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
                // The first one of the game only. A game carries exactly one in every
                // archived log, and if a re-roll ever produced a second it is the roll
                // that was played on that matters, not the one that was thrown out.
                else if (type is "GREMessageType_DieRollResultsResp" && game.Rolls.Count == 0)
                    game.Rolls.AddRange(Openings.ReadRolls(m));

                if (Json.Obj(m, "gameStateMessage") is not { } gsm) continue;

                if (Json.Obj(gsm, "gameInfo") is { } gameInfo)
                {
                    var sofar = ReadGameOutcomes(gameInfo, "results");
                    if (sofar.Count > outcomes.Count) outcomes = sofar;

                    // A new game. Everything the old tracker holds is about the game that
                    // just ended, and its instance ids are about to be handed out again,
                    // so the game gets a tracker of its own rather than inheriting one.
                    if (Json.Int(gameInfo, "gameNumber") is { } number && number != game.Number)
                    {
                        game.EndSeq = st.Seq;
                        // A log that opens mid-match announces game two before game one
                        // ever emits anything. An empty game is not a game.
                        if (game.EndSeq == game.FirstSeq) games.RemoveAt(games.Count - 1);

                        game = new GameRun(number, new GameStateTracker(cards), st.Seq);
                        games.Add(game);
                        st.StartGame();
                    }
                }

                var tracker = game.Tracker;

                // Only until the first turn number arrives. Mulligans are over by then —
                // all 29 increments in the archive land while the turn is still unset —
                // and the count is per game, so a later game's mulligans are read as its
                // own opening hands rather than folded into the first game's.
                if (tracker.Turn == 0) Openings.ReadMulligans(gsm, game.Mulligans);

                tracker.Apply(gsm, st.Seq);
                EmitCombat(tracker, ts, st);
                EmitLevels(tracker, ts, st);
                EmitCopies(tracker, ts, st);

                // Inventory only — nothing here reaches the transcript. persistentAnnotations
                // is a second annotation surface the extractor never used to look at, so
                // `stats` reported a clean bill while TargetSpec and ClassLevel sat unread
                // in it for months. Counting it is what stops that happening again.
                foreach (var pa in Json.Array(gsm, "persistentAnnotations"))
                    st.CountPersistent(pa);

                // Which permanents this message already reports a counter on. A power
                // change backed by a counter is said twice otherwise, and the counter
                // line is the better of the two because it names the kind.
                var countered = new HashSet<int>();
                foreach (var a in Json.Array(gsm, "annotations"))
                    if (Json.Array(a, "type").Any(t => t.ValueKind == JsonValueKind.String &&
                            t.GetString() is "AnnotationType_CounterAdded" or
                                             "AnnotationType_CounterRemoved"))
                        if (FirstAffected(a) is { } hit) countered.Add(hit);

                // Every permanent any annotation in this message speaks about. A statline
                // that moved while its object was named by something is already explained
                // by whatever that something was.
                var explained = new HashSet<int>();
                foreach (var a in Json.Array(gsm, "annotations"))
                    if (Json.Array(a, "type").Any(t => t.ValueKind == JsonValueKind.String &&
                            t.GetString() is "AnnotationType_CounterAdded"
                                          or "AnnotationType_CounterRemoved"
                                          or "AnnotationType_PowerToughnessModCreated"
                                          or "AnnotationType_ZoneTransfer") &&
                        FirstAffected(a) is { } spoken)
                        explained.Add(spoken);

                // Which ability instances a player deliberately activated. Not an event
                // of its own: the ability's AbilityInstanceCreated already produces the
                // line, and this is what corrects that line's verb — an activation
                // reported as "X's ability triggers" hides both the decision and the
                // cost that was paid. 450 of these across the archive, actionType 2
                // meaning "activate" (1 is a cast, 3 a land drop, 4 a mana ability).
                foreach (var a in Json.Array(gsm, "annotations"))
                {
                    if (!GameStateTracker.HasType(a, "AnnotationType_UserActionTaken"))
                        continue;
                    if (GameStateTracker.DetailInt(a, "actionType") != 2) continue;
                    if (Json.Int(a, "affectorId") is not { } actorSeat ||
                        actorSeat is not (1 or 2)) continue;
                    if (FirstAffected(a) is not { } abilityInst) continue;
                    game.Activations[abilityInst] = actorSeat;
                }

                // A resync re-sends annotations it has already delivered, and each one
                // used to narrate a second time. Everything is remembered; only a resync
                // is allowed to be silenced by that memory — see GameRun.AlreadyTold.
                //
                // The filter sits here and nowhere else. The loops above rebuild per
                // message, so a repeat only re-states what they already hold, and
                // tracker.Apply is right to take a resync, because a resync is a true
                // snapshot of the board. It is the telling that must not happen twice.
                var resync = Json.Str(gsm, "type") == "GameStateType_Full";
                foreach (var a in Json.Array(gsm, "annotations"))
                {
                    var told = game.AlreadyTold(a);
                    if (resync && told) continue;
                    EmitFor(a, tracker, ts, st, countered);
                }

                // After the streamed annotations, not before: the grant and the spell
                // that made it arrive in the same message, and "Enter the Avatar State
                // resolves" has to be on the page before the first strike it granted.
                EmitAbilityGrants(tracker, ts, st);

                EmitAbilityExpiries(tracker, ts, st);

                EmitStatExpiry(game, tracker, ts, st, explained);
            }
        }

        games[^1].EndSeq = st.Seq;

        // Per game, because every one of these passes asks the tracker what a permanent
        // is called and what size it was, and neither question has an answer that spans
        // games — the ids are reused and the statline history starts over.
        foreach (var g in games)
        {
            MarkActivations(g.Tracker, st, g);
            var labels = PermanentLabels.Build(g.Tracker, cards, Boundaries(st, g));
            NamePermanents(g.Tracker, labels, st, g);
            NameBoards(g.Tracker, labels, st, g);
            FillTargets(g.Tracker, labels, st, g);
        }

        var you = (localSeat ?? fallbackSeat) is { } seat && seatMeta.TryGetValue(seat, out var y)
            ? y : null;
        var opp = you is null ? null : seatMeta.Values.FirstOrDefault(p => p.Seat != you.Seat);
        foreach (var g in games) g.Tracker.LocalSeat = you?.Seat ?? 0;

        var yourTeam = you?.Seat;   // teamId equals seat in every observed match
        var won = yourTeam == 1 ? gamesForTeam1 : gamesForTeam2;
        var lost = yourTeam == 1 ? gamesForTeam2 : gamesForTeam1;

        var records = BuildGames(games, outcomes, yourTeam, st);

        if (sawFinal)
        {
            st.Add(new GameEvent
            {
                TimestampMs = ended,
                // The last game's number, not zero: the renderer reads a change of game
                // number as a boundary, and a match-end line filed under "game 0" would
                // open a game that never existed.
                GameNumber = games[^1].Number,
                Kind = EventKind.GameEnd,
                Amount = winningTeam ?? 0,
                // A draw has no winner for EndLine to name, which would leave the
                // page ending mid-sentence — the match was called, and the
                // transcript has to say so.
                Detail = drawn ? "The match ends in a draw — nobody wins"
                               : EndLine(winningTeam, yourTeam, endReason, "the match"),
                RawType = endReason
            });
        }

        var (deck, commanders, colors) = BuildDeck(decks, you, games);
        return new Transcript(
            matchId, started, ended, eventName, you, opp,
            winningTeam, won, lost, Incomplete: !sawFinal,
            st.Events, st.Unknown, st.CardsSeen, st.Unresolved, gaps,
            deck, records.Count > 0 ? records[0].Opening : null)
        {
            Games = records,
            Drawn = drawn,
            Commanders = commanders,
            DeckColors = colors,
            UnknownPersistentAnnotations = st.UnknownPersistent
        };
    }

    /// <summary>
    /// Turn boundaries within one game, plus its end so its last turn is looked at too.
    /// </summary>
    private static List<int> Boundaries(Emit st, GameRun g)
    {
        var boundaries = new List<int>();
        for (var i = g.FirstSeq; i < g.EndSeq; i++)
            if (st.Events[i].Kind == EventKind.TurnStart)
                boundaries.Add(i);
        boundaries.Add(g.EndSeq);
        return boundaries;
    }

    /// <summary>
    /// One record per game, carrying its opening, how far it ran and how it ended.
    /// </summary>
    /// <remarks>
    /// Outcomes are matched to games by game number rather than by position, because a
    /// log that begins mid-match holds game two's events alongside game one's result and
    /// lining those up by index would credit the wrong game with the wrong ending.
    /// </remarks>
    private static List<GameRecord> BuildGames(
        List<GameRun> games, List<GameOutcome> outcomes, int? yourTeam, Emit st)
    {
        GameOutcome? Outcome(int number) =>
            number >= 1 && number <= outcomes.Count ? outcomes[number - 1] : null;

        var records = new List<GameRecord>(games.Count);
        foreach (var g in games)
        {
            // Nobody rolls a die after game one: the loser of the previous game chooses
            // who begins. Arena does address ChooseStartingPlayerReq to a seat, but it
            // only ever addresses messages to the local client, so the rule is what
            // identifies the chooser and a game whose predecessor has no recorded winner
            // gets no claim about who chose.
            var chooser = g.Number >= 2 && Outcome(g.Number - 1)?.WinningTeamId is { } previous
                ? previous switch { 1 => 2, 2 => 1, _ => (int?)null }
                : null;

            var turns = 0;
            for (var i = g.FirstSeq; i < g.EndSeq; i++)
                turns = Math.Max(turns, st.Events[i].Turn);

            var outcome = Outcome(g.Number);
            records.Add(new GameRecord(
                g.Number,
                BuildOpening(g, st, chooser),
                turns,
                outcome?.WinningTeamId,
                EndLine(outcome?.WinningTeamId, yourTeam, outcome?.Reason, $"game {g.Number}")));
        }
        return records;
    }

    /// <summary>
    /// The per-game entries of one of Arena's result lists, in game order. Both
    /// <c>gameInfo.results</c> and <c>finalMatchResult.resultList</c> have this shape.
    /// </summary>
    private static List<GameOutcome> ReadGameOutcomes(JsonElement owner, string property)
    {
        var results = new List<GameOutcome>();
        foreach (var r in Json.Array(owner, property))
        {
            if (Json.Str(r, "scope") != "MatchScope_Game") continue;
            results.Add(new GameOutcome(Json.Int(r, "winningTeamId"), Json.Str(r, "reason")));
        }
        return results;
    }

    /// <summary>
    /// How one game opened, or null when the log carried none of it.
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
    private static Opening? BuildOpening(GameRun g, Emit st, int? chooser)
    {
        int? firstPlayer = null;
        for (var i = g.FirstSeq; i < g.EndSeq; i++)
        {
            var opener = st.Events[i];
            if (opener.Kind != EventKind.TurnStart) continue;
            if ((opener.ActorSeat ?? opener.ActiveSeat) is > 0 and var seat) firstPlayer = seat;
            break;
        }

        return g.Rolls.Count > 0 || g.Mulligans.Count > 0 || firstPlayer is not null
            ? new Opening(g.Rolls, firstPlayer, g.Mulligans, chooser)
            : null;
    }

    /// <summary>
    /// The decklist, its commanders and its colours, attributed to the local seat or not
    /// shown at all. They travel together because they arrive together: a commander
    /// taken from one message and a deck from another could describe two different
    /// decks, and the colours are read off both.
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
    private (IReadOnlyList<DeckEntry> Deck, IReadOnlyList<string> Commanders, string? Colors)
        BuildDeck(
            List<(int? Seat, IReadOnlyList<int> GrpIds, IReadOnlyList<int> Commanders)> decks,
            PlayerInfo? you, List<GameRun> games)
    {
        if (you is null) return ([], [], null);

        var mine = decks.LastOrDefault(d => d.Seat == you.Seat);
        if (mine.GrpIds is not { Count: > 0 } grpIds) return ([], [], null);

        // Owning a game object is the client's own record of having held the card.
        // A card that stayed in the library the whole match never gets one, which is
        // precisely the distinction worth drawing. Across every game, because the mark
        // says "all match" and a card drawn only in game two was still drawn.
        var seen = games
            .SelectMany(g => g.Tracker.Objects.Values)
            .Where(o => o.OwnerSeat == you.Seat && o.GrpId > 0)
            .Select(o => o.GrpId)
            .ToHashSet();

        return (DeckList.Build(grpIds, cards, seen),
                DeckList.CommanderNames(mine.Commanders, cards),
                DeckColors.Of(grpIds, mine.Commanders, cards));
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
    /// Emits one line per permanent that became a copy of a card. Without it the page
    /// shows an activation, then a consequence that cannot be accounted for: the archive's
    /// clearest case activates Shuri, Wakandan Inventor and reports "Iron Man, Futurist
    /// Paragon's ability triggers ×2" with exactly one Iron Man on the battlefield. Every
    /// line is true and the play — a Lembas turned into a second Iron Man, dodging the
    /// legend rule for a second animation trigger — is nowhere on the page.
    /// </summary>
    /// <remarks>
    /// Two sentences, because there are two things happening under one annotation. Seven
    /// of the archive's thirteen name an affector and carry a duration: an effect changed
    /// a permanent already in play. The other six carry Arena's "nobody" sentinel and no
    /// duration, and every one of them is a clone card — Waxen Shapethief, Spark Double,
    /// Mockingbird, Chameleon — arriving already copying something under its own
    /// replacement effect. "Becomes" would be wrong for those: nothing changed, it came
    /// that way.
    /// <para>
    /// A copy is dropped when the card database can name neither end of it. The line
    /// exists to say which permanent turned into which card, and half of that is not
    /// worth a line — the same bargain the grant lines strike.
    /// </para>
    /// </remarks>
    private void EmitCopies(GameStateTracker tracker, long ts, Emit st)
    {
        foreach (var copy in tracker.NewCopies)
        {
            if (copy.OwnName is not { } own || CardNames.IsPlaceholder(own)) continue;
            if (cards.CardForGrpId(copy.CopyFromGrpId)?.Name is not { } copied) continue;
            if (CardNames.IsPlaceholder(copied)) continue;

            st.SawCard(own);
            st.SawCard(copied);

            var causeName = copy.Affector is { } cid ? tracker.NameOf(cid) : null;
            st.SawCard(causeName);
            if (CardNames.IsPlaceholder(causeName)) causeName = null;

            st.Add(Base(tracker, ts, EventKind.Copied) with
            {
                ActorSeat = tracker.Get(copy.Affected)?.ControllerSeat is > 0 and var c
                    ? c : tracker.ActiveSeat,
                SourceInstanceId = copy.Affected,
                SourceName = own,
                TargetName = copied,
                CauseInstanceId = causeName is null ? null : copy.Affector,
                CauseName = causeName,

                // The narrator's whole grammar switch. Carried as the detail rather than
                // as a bool because GameEvent is flat and a second flag for one event
                // kind would be a column every other kind leaves null.
                Detail = copy.Temporary ? TemporaryCopy : PermanentCopy
            });
        }
    }

    /// <summary>
    /// What <see cref="EventKind.Copied"/> puts in <c>Detail</c> to say which of the two
    /// copy sentences applies. Constants rather than loose strings so the extractor and
    /// the narrator cannot drift apart on a spelling.
    /// </summary>
    public const string TemporaryCopy = "temporary";

    /// <inheritdoc cref="TemporaryCopy"/>
    public const string PermanentCopy = "permanent";

    /// <summary>
    /// Emits the abilities a permanent was granted, one line per granter per permanent.
    /// This is the line that answers "why did the Elves deal first-strike damage": the
    /// grant otherwise leaves no mark on the page at all, because the spell's
    /// resolution says only that it resolved and the damage step two lines later
    /// already behaves as if everyone knew.
    /// </summary>
    /// <remarks>
    /// Grants from one granter are one line — Enter the Avatar State gives four
    /// keywords in a single annotation, and four consecutive "gains" lines would be
    /// the same fact told worse. A grant whose ability text the database cannot name
    /// is dropped, the same bargain <c>AnnotationType_AbilityInstanceCreated</c>
    /// strikes: only worth a line when there are words to put on it. That costs one
    /// grant in the archive (grpid 1000001) out of 660.
    /// </remarks>
    private void EmitAbilityGrants(GameStateTracker tracker, long ts, Emit st)
    {
        foreach (var grants in tracker.NewAbilityGrants.GroupBy(g => (g.Affected, g.Affector)))
        {
            // A Class levelling up grants itself its new level's ability in the same
            // message that moves the level. "Caretaker's Talent becomes level 2" is
            // already on the page in Arena's own words, and the quoted grant under it
            // is the same fact restated by the machinery that implements it — 115
            // lines across the archive, every one directly beside its level line.
            if (tracker.NewLevels.Any(l => l.Id == grants.Key.Affected)) continue;

            var name = tracker.NameOf(grants.Key.Affected);
            if (CardNames.IsPlaceholder(name)) continue;

            var clauses = grants
                .Select(g => cards.AbilityText(g.AbilityGrpId))
                .Where(raw => raw is not null)
                .Select(raw => AbilityText.Clause(raw!, out _))
                .ToList();
            if (clauses.Count == 0) continue;

            st.SawCard(name);

            // The granter: a spell mid-resolution or a permanent's standing ability.
            // Counted before it is suppressed, like every unnameable cause. A grant
            // whose granter is the creature itself — a conditional menace switching
            // on, an Equipment activating into a creature — keeps no cause, because
            // "Battlesong Berserker gives Battlesong Berserker menace" names one
            // permanent as though it were two.
            var causeId = grants.Key.Affector;
            var self = causeId is { } cid && tracker.Resolve(cid) == grants.Key.Affected;
            var causeName = !self && causeId is > 2 ? tracker.NameOf(causeId.Value) : null;
            st.SawCard(causeName);
            if (CardNames.IsPlaceholder(causeName)) causeName = null;

            st.Add(Base(tracker, ts, EventKind.AbilityGained) with
            {
                ActorSeat = tracker.Get(grants.Key.Affected)?.ControllerSeat is > 0 and var c
                    ? c : tracker.ActiveSeat,
                TargetInstanceId = grants.Key.Affected,
                TargetName = name,
                CauseInstanceId = causeName is null ? null : causeId,
                CauseName = causeName,
                Detail = AbilityText.Join(clauses)
            });
        }
    }

    /// <summary>
    /// Emits the granted abilities that wore off, one line per permanent. This is the
    /// half of the story the grant line opened: a reader watching Battlesong Berserker
    /// gain menace four times across a match had to infer the four expiries in between,
    /// because Arena never announces one — the tracker reads it off the object's own
    /// description instead, the way a statline wear-off is read.
    /// </summary>
    /// <remarks>
    /// No cause is named. A wear-off has no actor — the effect simply reached its end —
    /// and the grant line two turns up already said who put it there. Grants whose text
    /// the database cannot name are dropped the same as at grant time: a line saying a
    /// creature lost something it was never said to have would open more questions than
    /// it answers.
    /// </remarks>
    private void EmitAbilityExpiries(GameStateTracker tracker, long ts, Emit st)
    {
        foreach (var expiries in tracker.NewAbilityExpiries.GroupBy(x => x.Affected))
        {
            var name = tracker.NameOf(expiries.Key);
            if (CardNames.IsPlaceholder(name)) continue;

            var clauses = expiries
                .Select(x => cards.AbilityText(x.AbilityGrpId))
                .Where(raw => raw is not null)
                .Select(raw => AbilityText.Clause(raw!, out _))
                .ToList();
            if (clauses.Count == 0) continue;

            st.SawCard(name);

            st.Add(Base(tracker, ts, EventKind.AbilityExpired) with
            {
                ActorSeat = tracker.Get(expiries.Key)?.ControllerSeat is > 0 and var c
                    ? c : tracker.ActiveSeat,
                TargetInstanceId = expiries.Key,
                TargetName = name,
                Detail = AbilityText.Join(clauses)
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

            // Tap state is printed but does not by itself make a board worth reprinting.
            // Permanents untap at their controller's untap step, so once tapped state was
            // read correctly every turn boundary "changed" every board — 417 of 1,076
            // consecutive snapshots differed from the one before by nothing except
            // creatures having untapped, which is what a turn passing means and not news
            // about the board.
            var shape = detail.Replace(" (tapped)", "", StringComparison.Ordinal)
                              .Replace(", tapped)", ")", StringComparison.Ordinal);
            if (st.LastBoard.TryGetValue(seat, out var previous) && previous == shape) continue;
            st.LastBoard[seat] = shape;

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

    private void EmitFor(JsonElement a, GameStateTracker tracker, long ts, Emit st,
                         IReadOnlySet<int> countered)
    {
        foreach (var typeEl in Json.Array(a, "type"))
        {
            if (typeEl.ValueKind != JsonValueKind.String) continue;
            var type = typeEl.GetString();
            if (type is null || Ignored.Contains(type)) continue;

            GameEvent? ev;

            if (type == "AnnotationType_GainDesignation")
            {
                // A Room's two halves are unlocked one at a time, and the designation
                // says which: 19 is the first door and 20 the second. Every one of the
                // archive's 201 of these lands on a card whose name holds both halves,
                // so the half being unlocked can be named even though DesignationType is
                // in no enum table — which was the reason this was dropped.
                //
                // The other 13 designations in the archive (types 16, 18, 22, 24) land
                // on ordinary cards and stay dropped: those really are a bare int with
                // nothing to say what was gained.
                if (FirstAffected(a) is not { } room) continue;
                var designation = GameStateTracker.DetailInt(a, "DesignationType");
                if (designation is not (19 or 20)) continue;

                var full = tracker.NameOf(room);
                var halves = full.Split(" // ", StringSplitOptions.TrimEntries);
                if (halves.Length != 2) continue;

                // No cause is named, though affectorId is populated on 54 of these.
                // It is not the unlocker: across the archive it points at a Plains once
                // and at Hare Apparent's ability once, neither of which can unlock a
                // door, so there is no shape of affector that can be trusted and no
                // filter that separates the 26 correct ones from the wrong ones. The
                // line above this one already names whatever just resolved.
                st.Add(Base(tracker, ts, EventKind.DoorUnlocked) with
                {
                    ActorSeat = tracker.Get(room)?.ControllerSeat is > 0 and var rc
                        ? rc : tracker.ActiveSeat,
                    SourceInstanceId = room,
                    SourceName = halves[designation == 19 ? 0 : 1]
                });
                continue;
            }

            if (type == "AnnotationType_PowerToughnessModCreated")
            {
                // A pump, a shrink, or a doubling — anything that moves a statline
                // without a counter behind it. Suppressed when this same message adds a
                // counter to the permanent, because the counter line says it better: it
                // names the kind, and 959 of the archive's 1,147 mods are that case.
                //
                // The remaining 188 had nothing reporting them at all. That is how a
                // creature could go from 1/2 to 24/4 across one turn of landfall
                // doublings with the transcript mentioning none of it, and how a
                // creature shrunk to death by -3/-3 could die with no stated cause.
                if (FirstAffected(a) is not { } pt || countered.Contains(pt)) continue;

                var dp = GameStateTracker.DetailInt(a, "power") ?? 0;
                var dt = GameStateTracker.DetailInt(a, "toughness") ?? 0;
                if (dp == 0 && dt == 0) continue;

                var mod = Base(tracker, ts, EventKind.StatsModified) with
                {
                    ActorSeat = tracker.Get(pt)?.ControllerSeat is > 0 and var pc
                        ? pc : tracker.ActiveSeat,
                    TargetInstanceId = pt,
                    TargetName = tracker.NameOf(pt),
                    Amount = dp,
                    Detail = $"{dp:+#;-#;+0}/{dt:+#;-#;+0}"
                };
                st.Add(mod);
                continue;
            }

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

                // A card the client never saw has no game object, so it has no controller
                // — but the zone it landed in has an owner, and that is whose card it is.
                // Falling straight through to the active player credited the opponent's
                // draws to you: they draw on your turn too, from a card-draw effect or an
                // end-step trigger, and 40 draws across 22 matches read "You draw Unknown
                // card" when the library and hand both belonged to seat two. Every one of
                // the 40 leaned the same way, because the active player is the only seat
                // the fallback can name.
                var zoneOwner = GameStateTracker.DetailInt(a, "zone_dest") is { } zd
                    ? tracker.ZoneOwner(zd) : null;

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

                // Carried for the categories whose destination is not implied by the
                // verb. A Destroy reads as a destroy wherever the card landed, but a
                // Return goes to hand 61 times and to the battlefield 47 in this archive
                // and used to claim "to hand" for all 108 of them. A state-based action
                // needs it for one case: every SBA in the archive ends in the graveyard
                // except SBA_Commander, which is the commander leaving it — dropping the
                // destination rendered that trip home as a second burial (#18).
                var wantsZone = kind
                    is EventKind.ZoneMove or EventKind.Returned or EventKind.StateBasedAction;
                string? Zone(string key) => wantsZone &&
                    GameStateTracker.DetailInt(a, key) is { } z &&
                    tracker.ZoneTypes.TryGetValue(z, out var name2) ? name2 : null;

                ev = Base(tracker, ts, kind) with
                {
                    SourceInstanceId = objId,
                    SourceName = name,
                    ActorSeat = controller is > 0 ? controller : zoneOwner ?? tracker.ActiveSeat,
                    CauseInstanceId = causeName is null ? null : cause,
                    CauseName = causeName,
                    Detail = category,
                    FromZone = Zone("zone_src"),
                    ToZone = Zone("zone_dest")
                };
            }
            else if (type == "AnnotationType_AbilityInstanceCreated")
            {
                // The ability object resolves to "<source card>'s ability" through its
                // objectSourceGrpId. Only worth a line when we can name the source.
                var abilityId = FirstAffected(a);
                var abilityName = abilityId is { } aid ? tracker.NameOf(aid) : null;
                if (CardNames.IsPlaceholder(abilityName)) continue;

                var (causeId, causeName) =
                    TriggerCause(tracker, abilityId, Json.Int(a, "affectorId"));
                st.SawCard(causeName);

                ev = Base(tracker, ts, EventKind.Triggered) with
                {
                    SourceInstanceId = abilityId,
                    SourceName = abilityName,
                    CauseInstanceId = causeId,
                    CauseName = causeName
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

    /// <summary>
    /// What set a triggered ability off, when naming it would tell a reader something the
    /// trigger line does not already say. Nulls otherwise.
    /// </summary>
    /// <param name="abilityId">The new ability instance, from the annotation's affected ids.</param>
    /// <param name="sourceId">
    /// The permanent whose ability this is, from the annotation's affector. Used only to
    /// recognise a trigger that set itself off.
    /// </param>
    /// <remarks>
    /// A creature's own enters-the-battlefield trigger names itself as the cause, and
    /// "Hare Apparent triggers Hare Apparent's ability" is worse than saying nothing — it
    /// reads as though a second permanent were involved. 996 of the archive's 2,394
    /// triggering objects are that case, so the check is most of what makes the remaining
    /// 1,398 worth printing.
    /// </remarks>
    private static (int? Id, string? Name) TriggerCause(
        GameStateTracker tracker, int? abilityId, int? sourceId)
    {
        if (abilityId is not { } ability) return (null, null);
        if (tracker.CauseOf(ability) is not { } cause) return (null, null);
        if (sourceId is { } source && tracker.Resolve(cause) == tracker.Resolve(source))
            return (null, null);

        var name = tracker.NameOf(cause);
        // "Unknown card triggers Carrot Cake's ability" is a worse sentence than the one
        // without a cause, so an unnameable cause is dropped rather than guessed at.
        return CardNames.IsPlaceholder(name) ? (null, null) : (cause, name);
    }

    private static int AmountFor(string type, JsonElement a) => type switch
    {
        "AnnotationType_DamageDealt" => GameStateTracker.DetailInt(a, "damage") ?? 0,
        "AnnotationType_ModifiedLife" => GameStateTracker.DetailInt(a, "life") ?? 0,
        "AnnotationType_CounterAdded" => GameStateTracker.DetailInt(a, "transaction_amount") ?? 0,
        "AnnotationType_CounterRemoved" => -(GameStateTracker.DetailInt(a, "transaction_amount") ?? 0),
        _ => 0
    };

    /// <summary>
    /// What to say where the log stops accounting for the match.
    /// </summary>
    /// <remarks>
    /// It says what Arena did and not how much it withheld, for the same reason the
    /// warning above the transcript does not: "88 game objects" is Arena's vocabulary,
    /// and the number a reader can act on is that this spot is not trustworthy. The
    /// counts stay where a diagnostic audience wants them, on the gap itself and in
    /// <c>mtga-pbp stats</c>.
    /// <para>
    /// A torn line and a summarised message are told apart because a reader's next move
    /// differs: a summary was a decision Arena made and nothing can undo it, while a
    /// torn line is damage and the neighbouring capture may still hold the rest.
    /// </para>
    /// </remarks>
    private static string GapLine(LogGap gap)
    {
        // "this turn" would be a small lie before turn one, where the only thing that has
        // happened is the die roll and the mulligans. No gap in the archive falls there —
        // the earliest sits on turn 1 — so this is a guard rather than an observed case,
        // and it is here because the alternative is a sentence that names a turn the
        // match had not reached.
        var where = gap.Turn > 0 ? "this turn" : "this game";

        return gap.Kind == LogGapKind.Summarized
            ? $"— part of {where} is missing: Arena summarised an update instead of writing it —"
            : $"— part of {where} is missing: a log line ended mid-message —";
    }

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
        ref string? reason, ref bool drawn)
    {
        foreach (var r in Json.Array(fmr, "resultList"))
        {
            var scope = Json.Str(r, "scope");
            // How it ended: a concede, a timeout, or actually losing the game. Most
            // matches end in a concede, which "wins the match" hides entirely.
            if (Json.Str(r, "reason") is { } why && why != "ResultReason_Game")
                reason ??= why;
            // A draw is the one result that carries no winningTeamId, so it has to be
            // read before the guard below skips the entry. Match scope only: a drawn
            // game inside a Bo3 does not make the match a draw, it just counts for
            // neither tally — which the guard already arranges.
            if (scope == "MatchScope_Match" && Json.Str(r, "result") == "ResultType_Draw")
                drawn = true;
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
    /// <summary>
    /// A statline that moved with nothing in the message accounting for it — a temporary
    /// effect ending.
    /// </summary>
    /// <remarks>
    /// Arena announces a pump and never announces its expiry: there is no
    /// <c>PowerToughnessModDeleted</c>, the effect simply stops applying and the object
    /// starts reporting its base size again. So the only evidence is the statline itself
    /// changing while no annotation names the permanent.
    /// <para>
    /// Restricted to creatures on the battlefield. A card's power is only a claim worth
    /// making about it while it is in play, and objects moving between zones re-report
    /// their printed size constantly — reading those as effects wearing off would bury
    /// the real ones.
    /// </para>
    /// </remarks>
    private static void EmitStatExpiry(
        GameRun g, GameStateTracker tracker, long ts, Emit st, IReadOnlySet<int> explained)
    {
        foreach (var seat in (int[])[1, 2])
            foreach (var o in tracker.CreaturesOnBattlefield(seat))
            {
                if (o.Power is not { } p || o.Toughness is not { } t) continue;

                var id = tracker.Resolve(o.InstanceId);
                var now = (Power: p, Toughness: t);

                if (!g.LastStats.TryGetValue(id, out var was)) { g.LastStats[id] = now; continue; }
                g.LastStats[id] = now;

                if (was == now || explained.Contains(id)) continue;

                // Only shrinking, and shrinking in both directions at once. A statline
                // growing with nothing to explain it is a layer the parser has not
                // learned to read, not an effect ending; and one that moves both ways —
                // 2/5 to 4/4 — is a characteristic-defining ability setting the numbers
                // rather than a buff falling off. Porcelain Gallery makes every creature
                // as big as the number of creatures you control, so a printed 2/5 Ghostly
                // Dancers "returns to 4/4" while its power has just gone up.
                if (now.Power > was.Power || now.Toughness > was.Toughness) continue;

                st.Add(Base(tracker, ts, EventKind.StatsExpired) with
                {
                    ActorSeat = o.ControllerSeat is > 0 ? o.ControllerSeat : tracker.ActiveSeat,
                    TargetInstanceId = id,
                    TargetName = tracker.NameOf(id),
                    Detail = $"{was.Power}/{was.Toughness} → {now.Power}/{now.Toughness}"
                });
            }
    }

    /// <summary>
    /// Rewrites the trigger line of every ability its player deliberately activated:
    /// "Lander's ability triggers" becomes "Opponent activates Lander". A correction,
    /// not an addition — the trigger line is replaced in place, because the verb was
    /// wrong, and a second line beside it would state 318 facts twice.
    /// </summary>
    /// <remarks>
    /// Deferred rather than done while the messages stream past, because the activation
    /// and the ability's creation are the same fact split across annotations that share
    /// only the ability's instance id — and for 102 of the archive's 450 activations
    /// they arrive in different messages, in either order. Runs before
    /// <see cref="NamePermanents"/> so a renamed source is still a name that pass
    /// recognises and can hang a disambiguating letter on.
    /// <para>
    /// A Class levelling up is also an activation, and its line is removed rather than
    /// reworded: "Caretaker's Talent becomes level 2", emitted a message later, is this
    /// same fact in Arena's own words, and 126 of the archive's 130 level lines sat
    /// directly under a wrong-verb trigger line saying it a second time.
    /// </para>
    /// </remarks>
    private static void MarkActivations(GameStateTracker tracker, Emit st, GameRun g)
    {
        if (g.Activations.Count == 0) return;

        // Both sides are folded to canonical ids only now, with the game's whole alias
        // map known — the activation names the id in use when the player acted, the
        // creation the id in use when Arena announced the ability.
        var activated = new Dictionary<int, int>();
        foreach (var (id, seat) in g.Activations) activated[tracker.Resolve(id)] = seat;

        // Every trigger line that was really an activation, with the permanent it
        // belongs to. An ability whose permanent cannot be named keeps its trigger
        // line: the verb is wrong, but a wrong verb still beats losing the fact.
        var found = new List<(int Index, int Seat, int? SourceId, string SourceName)>();
        for (var i = g.FirstSeq; i < g.EndSeq; i++)
        {
            var e = st.Events[i];
            if (e.Kind != EventKind.Triggered || e.SourceInstanceId is not { } ability)
                continue;
            if (!activated.TryGetValue(tracker.Resolve(ability), out var seat)) continue;

            var (sourceId, sourceName) = tracker.AbilitySource(ability);
            if (sourceName is null) continue;
            found.Add((i, seat, sourceId, sourceName));
        }
        if (found.Count == 0) return;

        // A Class levelling up is an activation too, and its line is already on the
        // page: "Caretaker's Talent becomes level 2", emitted a message later, is the
        // same fact in Arena's own words. Each level line claims the nearest earlier
        // unclaimed activation of its own permanent — one each, so a Class whose other
        // ability was also activated near the level-up loses only the leveling line.
        // The window is measured, not guessed: 126 of the archive's 130 level lines
        // sit directly under their activation's line and the farthest is five rendered
        // lines away, but the mana paid for the level costs events the page never
        // shows, so the window is wider than the worst rendered distance.
        const int levelUpWindow = 16;
        var suppressed = new HashSet<int>();
        for (var i = g.FirstSeq; i < g.EndSeq; i++)
        {
            if (st.Events[i] is not
                { Kind: EventKind.LevelUp, SourceInstanceId: { } cls } level) continue;
            var clsId = tracker.Resolve(cls);

            var best = -1;
            foreach (var (index, _, sourceId, sourceName) in found)
            {
                if (index >= i || i - index > levelUpWindow ||
                    suppressed.Contains(index)) continue;
                // By instance when the ability named its permanent, by name only when
                // it could not — the level event's own name is raw here because this
                // runs before NamePermanents letters it. The instance answer is final
                // when there is one: Classes are not legendary, and with two copies of
                // the same Class in play a bare name match would let one copy's level
                // line swallow the other copy's genuine activation.
                var same = sourceId is { } sid
                    ? tracker.Resolve(sid) == clsId
                    : string.Equals(sourceName, level.SourceName, StringComparison.Ordinal);
                if (same && index > best) best = index;
            }
            if (best >= 0) suppressed.Add(best);
        }

        foreach (var (index, seat, sourceId, sourceName) in found)
        {
            var e = st.Events[index];
            if (suppressed.Contains(index))
            {
                // Removed, not reworded: an activation with no name makes no sentence
                // in either density, and the level line stays the one report.
                st.Events[index] = e with
                {
                    Kind = EventKind.Activated,
                    ActorSeat = seat,
                    SourceName = null,
                    SourceInstanceId = sourceId,
                    CauseInstanceId = null,
                    CauseName = null
                };
                continue;
            }

            st.SawCard(sourceName);
            st.Events[index] = e with
            {
                Kind = EventKind.Activated,
                ActorSeat = seat,
                SourceInstanceId = sourceId,
                SourceName = sourceName,
                // The activation is the player's own act. Whatever TriggeringObject
                // Arena may have named belonged to the sentence this one replaces.
                CauseInstanceId = null,
                CauseName = null
            };
        }
    }

    private static void NamePermanents(
        GameStateTracker tracker, PermanentLabels labels, Emit st, GameRun g)
    {
        for (var i = g.FirstSeq; i < g.EndSeq; i++)
        {
            var e = st.Events[i];
            // A counter event reports the change itself, so it names the size the
            // permanent was changed FROM. Everything else describes a permanent as it
            // stands at that moment.
            // A counter or a stat mod reports the change itself, so it names the size the
            // permanent was changed FROM. Everything else describes a permanent as it
            // stands at that moment.
            var before = e.Kind is EventKind.CounterChanged or EventKind.StatsModified
                                or EventKind.StatsExpired;

            // A copy line names the card the permanent IS, never what it answers to.
            // Its source name was read off the grpId for exactly that reason, and the
            // as-of name at this sequence is the copied card — so labelling it turns
            // the line into "Hare Apparent becomes a temporary copy of Hare Apparent".
            // Only the three copies that wear off inside the log hit this: the guard in
            // Named leaves a name alone when it disagrees with the tracker's, and a
            // copy still standing at the end of the game disagrees.
            var source = e.Kind == EventKind.Copied
                ? e.SourceName
                : Named(tracker, labels, e.SourceInstanceId, e.SourceName, e.Seq, before);
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
    private static void NameBoards(
        GameStateTracker tracker, PermanentLabels labels, Emit st, GameRun g)
    {
        foreach (var (seq, creatures) in st.Boards)
        {
            if (seq < g.FirstSeq || seq >= g.EndSeq) continue;
            st.Events[seq] = st.Events[seq] with
            {
                // As of this snapshot, not as of the end of the game. A permanent can
                // be renamed mid-match — Witness Protection does it — and this pass runs
                // once the whole log has been read, so reading the tracker's final state
                // here named a creature after a card that was still in its owner's
                // library at the time. 73 of 467 archived matches contain a rename.
                Detail = string.Join(", ", creatures.Select(c =>
                    labels.NameAt(c.Id, seq) + labels.Suffix(c.Id, seq) + c.Stats))
            };
        }
    }

    /// <summary>
    /// Attaches targets to spells once the whole match has been read, and says what the
    /// spell did to them. TargetSpec usually arrives in a later message than the cast it
    /// belongs to, so this cannot be done while the cast is being emitted — at that
    /// moment the target is not yet known, and neither is what it became.
    /// </summary>
    private static void FillTargets(
        GameStateTracker tracker, PermanentLabels labels, Emit st, GameRun g)
    {
        for (var i = g.FirstSeq; i < g.EndSeq; i++)
        {
            var e = st.Events[i];
            if (e.Kind != EventKind.SpellCast || e.SourceInstanceId is not { } id) continue;

            var targets = tracker.TargetsOf(id);
            if (targets.Count == 0) continue;

            // Layers are applied in the message that carries the resolution, not in the
            // one that carries the cast, so the "after" only exists from there on.
            var settled = ResolvedAt(tracker, st, e, g.EndSeq) ?? e.Seq;

            var named = new List<string>();
            foreach (var target in targets)
            {
                // Deliberately the tracker's name, not the as-of one. SawCard feeds the
                // deck list's "not seen" markers, which match on the card a player
                // registered — and a locname resolves a double-faced card to "A // B",
                // which matches no deck entry. Only the rendered text below is
                // as-of-cast; what the match is recorded as having seen is not.
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
    /// <param name="end">
    /// Where this game's events stop. The search cannot run past it: instance ids are
    /// handed out again in the next game, so a spell that was countered would otherwise
    /// be reported as resolving whenever the next game happened to reuse its id.
    /// </param>
    private static int? ResolvedAt(GameStateTracker tracker, Emit st, GameEvent cast, int end)
    {
        if (cast.SourceInstanceId is not { } id) return null;
        var spell = tracker.Resolve(id);

        for (var i = cast.Seq + 1; i < end; i++)
        {
            var e = st.Events[i];
            if (e.Kind == EventKind.Resolved && e.SourceInstanceId is { } other &&
                tracker.Resolve(other) == spell)
                return e.Seq;
        }
        return null;
    }

    /// <summary>
    /// How something ended, naming a concede or timeout rather than hiding it.
    /// </summary>
    /// <param name="what">
    /// What was won — "the match", or "game 2". One sentence for both, because a game of
    /// a Bo3 ends exactly the way a match does and saying it two different ways would
    /// only invite the two wordings to drift apart.
    /// </param>
    private static string? EndLine(int? winningTeam, int? yourTeam, string? reason, string what)
    {
        if (winningTeam is null) return null;
        var youWon = winningTeam == yourTeam;
        var loser = youWon ? "Opponent" : "You";
        var verb = youWon ? "concedes" : "concede";

        return reason switch
        {
            "ResultReason_Concede" => $"{loser} {verb} — {(youWon ? "you win" : "opponent wins")} {what}",
            "ResultReason_Timeout" => $"{loser} {(youWon ? "runs" : "run")} out of time — {(youWon ? "you win" : "opponent wins")} {what}",
            _ => youWon ? $"You win {what}" : $"Opponent wins {what}"
        };
    }

    private static long ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var ts) ? Json.Long(ts) ?? 0 : 0;
}
