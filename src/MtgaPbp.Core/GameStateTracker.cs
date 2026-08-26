using System.Text.Json;

namespace MtgaPbp.Core;

public sealed class TrackedObject
{
    public int InstanceId;
    public int GrpId;
    public int? NameLocId;
    public string Type = "";
    public int OwnerSeat;
    public int ControllerSeat;
    public int ZoneId;
    public int? Power;
    public int? Toughness;
    public int Damage;
    public bool IsTapped;
    public int? Loyalty;
    public int? ObjectSourceGrpId;

    /// <summary>
    /// The object this one hangs off — an ability's source permanent, an emblem's
    /// planeswalker. The only link that survives when <c>objectSourceGrpId</c> does not
    /// name a card, which is how every emblem's ability arrives.
    /// </summary>
    public int? ParentId;

    public IReadOnlyList<string> CardTypes = [];
    public string AttackState = "";
    public string BlockState = "";
    public int? AttackTargetId;
    public IReadOnlyList<int> BlockedAttackerIds = [];
    public readonly Dictionary<int, int> Counters = [];

    /// <summary>
    /// The ability grpids this object carried the last time Arena described it,
    /// printed and granted alike, with how many <c>uniqueAbilities</c> entries each
    /// had. Kept so a granted ability leaving can be noticed — the object's own
    /// description is the only surface that says a grant wore off, because the
    /// grant's persistent annotation is sampled in and out of messages while the
    /// ability stands.
    /// </summary>
    /// <remarks>
    /// A count, not a set, because printed menace and granted menace are two entries
    /// under one grpid. The grant expiring drops the count without emptying it, and
    /// that drop is what retires the grant in the tracker's registry — on set
    /// membership alone the registry entry would outlive its grant and misread a
    /// later transform as the wear-off it already missed.
    /// </remarks>
    public Dictionary<int, int> AbilityGrpIds = [];
}

/// <summary>
/// What a permanent's statline was at one point in the event stream, stamped with the
/// sequence number of the next event to be emitted when it changed.
/// </summary>
/// <remarks>
/// A transcript has to say what a creature was <em>at the time</em>, not what it ended
/// the match as: the Rabbit that was 5/5 on turn 27 is 6/6 by turn 29, and printing the
/// final value against the earlier line would be a lie. <see cref="InPlay"/> is carried
/// because power and toughness are only a claim worth making about a permanent on the
/// battlefield — the same card sitting on the stack or in a graveyard still reports
/// numbers, and they mean nothing there.
/// </remarks>
public readonly record struct StatSample(int Stamp, int Power, int Toughness, bool InPlay);

/// <summary>
/// What an object was called at one point in the match, and from when.
/// </summary>
/// <remarks>
/// A permanent's name is not fixed. Witness Protection renames what it enchants, and
/// Arena reports that honestly: the same instance's <c>name</c> locId changes mid-stream
/// while its grpId stays put. 73 of 467 archived matches contain at least one such
/// rename.
/// <para>
/// Stamped for the same reason <see cref="StatSample"/> is. Labels for a turn's board
/// are built once the whole log has been read, so anything read from final object state
/// describes the end of the game rather than the line being written — which is how a
/// creature came to be named nine turns before its name existed.
/// </para>
/// </remarks>
public readonly record struct NameSample(int Stamp, int NameLocId);

/// <summary>
/// One permanent becoming a copy of a card.
/// </summary>
/// <param name="Affected">The permanent that changed.</param>
/// <param name="OwnName">
/// Which card the permanent actually is, which is not what it answers to: by the time
/// this is read the object has already taken the copied card's name. Carried rather
/// than looked up downstream because every later reader would find the copied name and
/// produce "Iron Man becomes a copy of Iron Man". Null when the card is unknown.
/// </param>
/// <param name="CopyFromGrpId">The card it is now a copy of.</param>
/// <param name="Affector">
/// The permanent whose effect did it, or null. Null covers two cases that read the same
/// way from here: Arena's own 0xFFFFFFFD "nobody" sentinel, which marks a clone arriving
/// under its own replacement effect, and a self-copy where the affector is the affected.
/// </param>
/// <param name="Temporary">
/// Whether the annotation carried a <c>Duration</c>. Only whether, never how long: the
/// two duration codes in the archive (1227 and 3128) are in no table Arena ships, and
/// their meaning is legible only by reading the source cards' rules text.
/// </param>
public readonly record struct CopiedObject(
    int Affected, string? OwnName, int CopyFromGrpId, int? Affector, bool Temporary);

public sealed class GameStateTracker(ICardDb cards)
{
    private readonly Dictionary<int, TrackedObject> _objects = [];
    private readonly Dictionary<int, int> _alias = [];   // old id -> new id
    private readonly Dictionary<int, int> _aliasBack = []; // new id -> old id
    private readonly Dictionary<int, int> _life = [];
    private readonly Dictionary<int, string> _zoneTypes = [];
    private readonly Dictionary<int, int> _zoneOwners = [];
    private readonly Dictionary<int, List<int>> _targets = [];   // source id -> target ids
    private readonly Dictionary<int, List<StatSample>> _stats = [];
    private readonly Dictionary<int, List<NameSample>> _names = [];
    private readonly Dictionary<int, int> _classLevels = [];
    private readonly Dictionary<int, int> _triggerCauses = [];   // ability id -> what set it off
    private int _stamp;

    public int Turn { get; private set; }
    public int ActiveSeat { get; private set; }
    public int Phase { get; private set; }
    public int Step { get; private set; }
    public int GameNumber { get; private set; } = 1;
    public int LocalSeat { get; set; }

    public IReadOnlyDictionary<int, int> Life => _life;
    public IReadOnlyDictionary<int, TrackedObject> Objects => _objects;
    public IReadOnlyDictionary<int, string> ZoneTypes => _zoneTypes;

    /// <summary>
    /// Whose zone this is, for the zones that belong to a player. Null for the shared
    /// ones and for a zone the log has not described.
    /// </summary>
    public int? ZoneOwner(int zoneId) =>
        _zoneOwners.TryGetValue(zoneId, out var seat) ? seat : null;

    private readonly List<int> _newAttackers = [];
    private readonly List<int> _newBlockers = [];
    private readonly List<(int Id, int Level)> _newLevels = [];
    private readonly List<(int Affected, int AbilityGrpId, int? Affector)> _newAbilityGrants = [];
    private readonly HashSet<(int AnnotationId, int Affected, int AbilityGrpId)> _grantsSeen = [];
    private readonly List<(int Affected, int AbilityGrpId)> _newAbilityExpiries = [];
    private readonly List<CopiedObject> _newCopies = [];
    private readonly HashSet<(int AnnotationId, int Affected)> _copiesSeen = [];
    // canonical id -> grpid -> grants still outstanding. A count for the same reason
    // TrackedObject.AbilityGrpIds is one: two standing grants of trample are two facts.
    private readonly Dictionary<int, Dictionary<int, int>> _grantedAbilities = [];

    /// <summary>
    /// Creatures whose attack was submitted in the message just applied. Combat is not
    /// announced by an annotation — it only shows up as a state change on the object —
    /// so these are reported once, on the transition into the attacking state.
    /// Cleared at the start of every <see cref="Apply"/>.
    /// </summary>
    public IReadOnlyList<int> NewAttackers => _newAttackers;

    /// <summary>Creatures whose block was submitted in the message just applied.</summary>
    public IReadOnlyList<int> NewBlockers => _newBlockers;

    /// <summary>
    /// Classes that reached a new level in the message just applied, and the level they
    /// reached. Reported the same way combat is, and for the same reason: Arena states a
    /// class's level as a standing fact re-sent with every message rather than as an
    /// event, so only the transition is worth a line.
    /// </summary>
    /// <remarks>
    /// Level 1 is never annotated — a Class enters play at it and Arena says nothing —
    /// so the first level a class is ever seen at is a level-up, not a starting value.
    /// The exception is a log that begins mid-match with a class already levelled, which
    /// would be reported as levelling the moment the log picks it up; those transcripts
    /// already say they are incomplete.
    /// </remarks>
    public IReadOnlyList<(int Id, int Level)> NewLevels => _newLevels;

    /// <summary>
    /// Abilities granted to permanents in the message just applied — who gained what,
    /// and what granted it. Reported the same way levels are: the grant is a standing
    /// <c>AnnotationType_AddAbility</c> fact on the persistent surface, re-sent with
    /// every message and never announced as an event, so only its first appearance is
    /// worth a line.
    /// </summary>
    /// <remarks>
    /// The seen-key includes the annotation id on purpose. A standing grant keeps its
    /// id while its <c>affectedIds</c> grow — each new member is a new grant under the
    /// same id — and a fresh grant of the same ability to the same creature on a later
    /// turn arrives under a fresh id. Keying on (affected, ability) alone would report
    /// the first landfall trample of the game and silently drop every later one.
    /// </remarks>
    public IReadOnlyList<(int Affected, int AbilityGrpId, int? Affector)> NewAbilityGrants =>
        _newAbilityGrants;

    /// <summary>
    /// Granted abilities that wore off a permanent still on the battlefield in the
    /// message just applied. Not read from the grant's annotation: that annotation is
    /// sampled — it goes missing from the persistent surface for stretches of a game
    /// and returns under the same id, 252 times across the archive while the creature
    /// stood in play the whole while — so its absence proves nothing. The evidence is
    /// the object's own <c>uniqueAbilities</c> losing a grpid this tracker saw granted,
    /// the same object-state channel a statline wear-off is read from.
    /// </summary>
    /// <remarks>
    /// Only permanents still in play report here. The annotation and the ability both
    /// vanish when the creature dies too, and the death line already owns that fact —
    /// a creature that died did not "lose trample". A grant standing when the log stops
    /// is likewise never reported: with no later description of the object there is no
    /// diff, so a truncated log manufactures nothing.
    /// </remarks>
    public IReadOnlyList<(int Affected, int AbilityGrpId)> NewAbilityExpiries =>
        _newAbilityExpiries;

    /// <summary>
    /// Permanents that became a copy of something in the message just applied.
    /// </summary>
    /// <remarks>
    /// Unlike every other report on this class, this one is not a transition read off a
    /// standing fact. All 13 <c>AnnotationType_CopiedObject</c> annotations in the
    /// archive appear in exactly one message each and are never re-sent, so this is an
    /// event Arena states once. <see cref="_copiesSeen"/> is a cheap guard against a
    /// mid-game resync replaying it, not the mechanism.
    /// </remarks>
    public IReadOnlyList<CopiedObject> NewCopies => _newCopies;

    /// <summary>
    /// Every statline change seen, under the instance id Arena used at the time. Ids
    /// change when a card moves zones, so a consumer that wants one timeline per card
    /// has to fold these onto <see cref="Resolve"/>d ids itself.
    /// </summary>
    public IEnumerable<(int InstanceId, IReadOnlyList<StatSample> Samples)> StatHistory =>
        _stats.OrderBy(p => p.Key).Select(p => (p.Key, (IReadOnlyList<StatSample>)p.Value));

    /// <summary>
    /// Every name each object has answered to, in the order it took them on. Recorded
    /// under whichever id Arena was using at the time, exactly like
    /// <see cref="StatHistory"/> — callers fold on <see cref="Resolve"/>.
    /// </summary>
    public IEnumerable<(int InstanceId, IReadOnlyList<NameSample> Samples)> NameHistory =>
        _names.OrderBy(p => p.Key).Select(p => (p.Key, (IReadOnlyList<NameSample>)p.Value));

    /// <param name="stamp">
    /// The sequence number the next event out of this message will carry, so statline
    /// changes can be placed in the event stream. Zero when nobody is emitting events.
    /// </param>
    public void Apply(JsonElement gsm, int stamp = 0)
    {
        _stamp = stamp;
        _newAttackers.Clear();
        _newBlockers.Clear();
        _newLevels.Clear();
        _newAbilityGrants.Clear();
        _newAbilityExpiries.Clear();
        _newCopies.Clear();

        if (Json.Obj(gsm, "gameInfo") is { } gi && Json.Int(gi, "gameNumber") is { } gnv)
        {
            // Levels are remembered per game. Arena hands out instance ids afresh for
            // each game of a match, so a level carried over would let game one's
            // Caretaker's Talent silence game two's. Reuse turned out to run far deeper
            // than levels — objects, aliases, targets and statline history collide too —
            // so EventExtractor now gives each game a tracker of its own and this clear
            // fires on an already-empty map. It stays because a tracker that is handed
            // two games has to survive it, and because it is what documents the reuse.
            if (gnv != GameNumber) _classLevels.Clear();
            GameNumber = gnv;
        }

        if (Json.Obj(gsm, "turnInfo") is { } ti)
        {
            if (Json.Int(ti, "turnNumber") is { } tn)
            {
                // Combat state does not survive a turn, and Arena does not announce
                // that it ended — it simply stops sending attackState once combat is
                // over. Without this reset a creature that attacked once would be
                // considered permanently attacking and never reported again.
                if (tn != Turn) ClearCombatState();
                Turn = tn;
            }
            if (Json.Int(ti, "activePlayer") is { } ap) ActiveSeat = ap;
            if (Json.Int(ti, "phase") is { } ph) Phase = ph;
            if (Json.Int(ti, "step") is { } st) Step = st;
        }

        foreach (var p in Json.Array(gsm, "players"))
        {
            if (Json.Int(p, "systemSeatNumber") is { } seat &&
                Json.Int(p, "lifeTotal") is { } life)
                _life[seat] = life;
        }

        foreach (var z in Json.Array(gsm, "zones"))
        {
            if (Json.Int(z, "zoneId") is { } zid && Json.Str(z, "type") is { } zt)
                _zoneTypes[zid] = zt;
            // Only the per-player zones carry one — a hand, a library, a graveyard.
            // The battlefield, the stack and exile are shared and name no owner.
            if (Json.Int(z, "zoneId") is { } oid && Json.Int(z, "ownerSeatId") is { } os2)
                _zoneOwners[oid] = os2;
        }

        foreach (var go in Json.Array(gsm, "gameObjects")) UpsertObject(go);

        // Targets live here, not in `annotations`. AnnotationType_TargetSpec names what
        // a spell or ability was aimed at — affectorId is the source, affectedIds are
        // the targets. Missing this array is why targeting looked unavailable.
        foreach (var pa in Json.Array(gsm, "persistentAnnotations"))
        {
            if (HasType(pa, "AnnotationType_ClassLevel")) ReadClassLevel(pa);
            if (HasType(pa, "AnnotationType_TriggeringObject")) ReadTriggerCause(pa);
            if (HasType(pa, "AnnotationType_AddAbility")) ReadAddAbility(pa);
            if (HasType(pa, "AnnotationType_CopiedObject")) ReadCopiedObject(pa);

            if (!HasType(pa, "AnnotationType_TargetSpec")) continue;
            if (Json.Int(pa, "affectorId") is not { } src) continue;

            var targets = new List<int>();
            foreach (var x in Json.Array(pa, "affectedIds"))
                if (Json.Int(x) is { } t) targets.Add(t);
            if (targets.Count > 0) _targets[src] = targets;
        }

        // Aliases must be applied before EventExtractor reads this message's annotations.
        foreach (var a in Json.Array(gsm, "annotations"))
        {
            if (!HasType(a, "AnnotationType_ObjectIdChanged")) continue;
            var orig = DetailInt(a, "orig_id");
            var next = DetailInt(a, "new_id");
            if (orig is { } o && next is { } n && o != n)
            {
                _alias[o] = n;
                _aliasBack[n] = o;

                // Grants follow the object through a rename. They are keyed by the
                // canonical id known when the grant arrived, and an id change would
                // otherwise strand them under a key no later lookup resolves to.
                if (_grantedAbilities.Remove(o, out var moved))
                {
                    if (_grantedAbilities.TryGetValue(n, out var into))
                        foreach (var (grp, count) in moved)
                            into[grp] = into.GetValueOrDefault(grp) + count;
                    else _grantedAbilities[n] = moved;
                }
            }
        }
    }

    private void UpsertObject(JsonElement go)
    {
        if (Json.Int(go, "instanceId") is not { } id) return;

        if (!_objects.TryGetValue(id, out var obj))
            _objects[id] = obj = new TrackedObject { InstanceId = id };

        var wasPower = obj.Power;
        var wasToughness = obj.Toughness;
        var wasInPlay = InPlay(obj);

        if (Json.Int(go, "grpId") is { } grp) obj.GrpId = grp;
        if (Json.Int(go, "name") is { } nm)
        {
            // Only transitions are logged. An object reports its name on every message
            // it appears in, and storing all of them would bury the handful that mean
            // something under thousands that repeat the previous one.
            if (obj.NameLocId != nm)
            {
                if (!_names.TryGetValue(id, out var names)) _names[id] = names = [];
                names.Add(new NameSample(_stamp, nm));
            }
            obj.NameLocId = nm;
        }
        if (Json.Str(go, "type") is { } ty) obj.Type = ty;
        if (Json.Int(go, "ownerSeatId") is { } os) obj.OwnerSeat = os;
        if (Json.Int(go, "controllerSeatId") is { } cs) obj.ControllerSeat = cs;
        if (Json.Int(go, "zoneId") is { } zi) obj.ZoneId = zi;
        if (ReadStat(go, "power") is { } pw) obj.Power = pw;
        if (ReadStat(go, "toughness") is { } tg) obj.Toughness = tg;
        // Assigned unconditionally, because absence is the value. Arena omits protobuf
        // defaults: across the archive `isTapped` is true 14,967 times and false zero
        // times, and `damage` is non-zero 1,415 times and zero zero times. Treating
        // absence as "unchanged" latched both on — once a creature was tapped or
        // damaged it stayed that way on every later board line, and 671 of 1,946 board
        // snapshots carried at least one claim that was no longer true. The errors ran
        // one way only, which is what a latch looks like.
        //
        // Safe because a gameObjects entry is a complete description rather than a
        // patch: the two commonest shapes in the archive are the same thirteen keys
        // with and without `isTapped`, and no entry omits the identifying fields.
        obj.Damage = Json.Int(go, "damage") ?? 0;
        obj.IsTapped = go.TryGetProperty("isTapped", out var tap) &&
                       tap.ValueKind == JsonValueKind.True;
        if (ReadStat(go, "loyalty") is { } ly) obj.Loyalty = ly;
        if (Json.Int(go, "objectSourceGrpId") is { } src) obj.ObjectSourceGrpId = src;
        if (Json.Int(go, "parentId") is { } par) obj.ParentId = par;

        var types = new List<string>();
        foreach (var ct in Json.Array(go, "cardTypes"))
            if (ct.ValueKind == JsonValueKind.String) types.Add(ct.GetString()!);
        if (types.Count > 0) obj.CardTypes = types;

        // Read unconditionally, like `isTapped` above: absence is the value. A vanilla
        // creature whose only ability was granted reports the wear-off as a complete
        // snapshot with no `uniqueAbilities` at all — treated as "unchanged", that
        // wear-off would never be seen.
        var abilities = new Dictionary<int, int>();
        foreach (var ua in Json.Array(go, "uniqueAbilities"))
            if (Json.Int(ua, "grpId") is { } ag)
                abilities[ag] = abilities.GetValueOrDefault(ag) + 1;

        // A grpid's entry count dropping is a grant ending; the count reaching zero is
        // the creature no longer having the ability. Both matter, separately. The drop
        // retires outstanding grants whether or not anything is said — a grant that
        // duplicated printed menace ends invisibly, and a registry entry that outlived
        // it would misread a later transform as this wear-off. The line is only worth
        // words when the ability is actually gone: a creature with printed menace
        // still has menace when the granted copy ends, and overlapping grants only
        // read as lost when the last one goes — which is what the reader would say
        // too. And only for a permanent still in play, because the ability also
        // vanishes when the creature does, and the death line already owns that fact.
        if (_grantedAbilities.TryGetValue(Resolve(id), out var granted))
            foreach (var (was, had) in obj.AbilityGrpIds)
            {
                var dropped = had - abilities.GetValueOrDefault(was);
                if (dropped <= 0 || granted.GetValueOrDefault(was) is not (> 0 and var standing))
                    continue;
                var retired = Math.Min(dropped, standing);
                if (standing - retired > 0) granted[was] = standing - retired;
                else granted.Remove(was);
                if (!abilities.ContainsKey(was) && InPlay(obj))
                    _newAbilityExpiries.Add((Resolve(id), was));
            }
        obj.AbilityGrpIds = abilities;

        // Entering and leaving play are recorded alongside the numbers themselves: a
        // creature can arrive on the battlefield already at its printed statline, and
        // without that sample there would be no evidence it was ever in play at all.
        if (obj.Power is { } power && obj.Toughness is { } toughness)
        {
            var nowInPlay = InPlay(obj);
            if (power != wasPower || toughness != wasToughness || nowInPlay != wasInPlay)
            {
                if (!_stats.TryGetValue(id, out var log)) _stats[id] = log = [];
                log.Add(new StatSample(_stamp, power, toughness, nowInPlay));
            }
        }

        if (Json.Obj(go, "attackInfo") is { } ai && Json.Int(ai, "targetId") is { } tid)
            obj.AttackTargetId = tid;
        if (Json.Obj(go, "blockInfo") is { } bi)
        {
            var attackers = new List<int>();
            foreach (var x in Json.Array(bi, "attackerIds"))
                if (Json.Int(x) is { } ax) attackers.Add(ax);
            if (attackers.Count > 0) obj.BlockedAttackerIds = attackers;
        }

        // Report only the transition into combat, so a creature that stays attacking
        // across many diffs is announced once. The transition that counts is into
        // Attacking/Blocking, not Declared: Declared is the provisional state Arena
        // streams per click while the player is still arranging combat, and a
        // declared creature can be reassigned — or withdrawn, in which case no
        // further combat state ever arrives for it. Announcing at Declared is how a
        // block the player moved elsewhere got narrated against the wrong attacker
        // (issue #11), with the stale pairing kept because Declared → Declared is
        // not a transition.
        if (Json.Str(go, "attackState") is { } atk)
        {
            // Compare against the attacking state specifically, not "has any state":
            // a creature attacks on many turns, and its state returns to none in
            // between. Testing for a non-empty string would report only its first
            // attack of the game and silently drop every later one.
            var wasAttacking = obj.AttackState is "AttackState_Attacking";
            obj.AttackState = atk;
            if (!wasAttacking && atk is "AttackState_Attacking")
                _newAttackers.Add(id);
        }
        if (Json.Str(go, "blockState") is { } blk)
        {
            var wasBlocking = obj.BlockState is "BlockState_Blocking";
            obj.BlockState = blk;
            if (!wasBlocking && blk is "BlockState_Blocking")
                _newBlockers.Add(id);
        }
    }

    /// <summary>
    /// Notes a class's level, reporting it only when it has moved. The annotation is
    /// persistent, so it arrives again in every message for the rest of the game;
    /// without this the same level-up would be announced a few hundred times.
    /// </summary>
    private void ReadClassLevel(JsonElement pa)
    {
        if (Json.Int(pa, "affectorId") is not { } id) return;
        if (DetailInt(pa, "Level") is not { } level) return;

        var canonical = Resolve(id);
        if (_classLevels.TryGetValue(canonical, out var known) && known == level) return;

        _classLevels[canonical] = level;
        _newLevels.Add((canonical, level));
    }

    /// <summary>
    /// Notes ability grants, reporting each once. The payload runs parallel arrays:
    /// <c>grpid</c> holds one entry per ability granted — Enter the Avatar State lands
    /// as a single annotation whose grpids are flying, first strike, lifelink and
    /// hexproof — while <c>affectedIds</c> holds every permanent granted to, so a lord
    /// effect is one grpid crossed with five creatures. Every combination is its own
    /// grant.
    /// </summary>
    /// <remarks>
    /// The affector is carried rather than re-derived because it is the one part of
    /// the annotation that says why: 431 is Enter the Avatar State, and the line
    /// "Llanowar Elves gains first strike" with no cause is the issue this exists to
    /// fix, restated smaller.
    /// </remarks>
    private void ReadAddAbility(JsonElement pa)
    {
        if (Json.Int(pa, "id") is not { } annId) return;

        // An ability that rides on a counter — indestructible from Season of the
        // Burrow — is dual-typed AddAbility and Counter, and the streamed CounterAdded
        // has already put "gets 1 Indestructible counter" on the page. The counter
        // line is the better of the two for the same reason it beats a statline mod:
        // it names the kind, and it is what the reader watches leave later.
        if (HasType(pa, "AnnotationType_Counter")) return;

        var grpids = DetailInts(pa, "grpid");
        if (grpids.Count == 0) return;

        // Resolved like the affected ids are, so grants that name the same granter
        // under an aliased id still group into one line downstream.
        var affector = Json.Int(pa, "affectorId");
        if (affector is { } af && af > 2) affector = Resolve(af);
        foreach (var x in Json.Array(pa, "affectedIds"))
        {
            // Seats 1 and 2 are players; a granted ability lands on a permanent.
            if (Json.Int(x) is not { } affected || affected <= 2) continue;
            var canonical = Resolve(affected);
            foreach (var grp in grpids.Distinct())
            {
                if (!_grantsSeen.Add((annId, canonical, grp))) continue;
                _newAbilityGrants.Add((canonical, grp, affector));

                // What arms the wear-off diff in UpsertObject: only a grpid this
                // registry holds can be reported as expiring, so a printed ability
                // leaving an object's description — a transform, a face-down flip —
                // never reads as an effect wearing off.
                if (!_grantedAbilities.TryGetValue(canonical, out var set))
                    _grantedAbilities[canonical] = set = [];
                set[grp] = set.GetValueOrDefault(grp) + 1;
            }
        }
    }

    /// <summary>
    /// Notes a permanent becoming a copy of a card — the fact behind an activation whose
    /// consequence otherwise arrives unexplained.
    /// </summary>
    /// <remarks>
    /// The affected permanent has to be named by its grpId, and this is the one place in
    /// the codebase where that is true. <see cref="NameOf"/> deliberately prefers the
    /// object's <c>name</c> locId — issue #23 exists because it did not — but a copy is
    /// exactly the case where the two disagree on purpose: the locId is what the
    /// permanent answers to now, the grpId is which card it actually is. Asking for the
    /// name here produces "Iron Man, Futurist Paragon becomes a copy of Iron Man,
    /// Futurist Paragon", because <c>gameObjects</c> is applied above this loop and the
    /// rename has already landed.
    /// <para>
    /// Measured rather than assumed: across all 13 copies in the archive the affected
    /// object reports exactly one grpId for the whole match, and it is always its own
    /// card. Nothing about a copy effect touches it.
    /// </para>
    /// <para>
    /// This is also why the annotation cannot be replaced by watching for renames.
    /// Taskmaster, Mercenary Mimic keeps its own name by the card's own text — "except
    /// his name is Taskmaster, Mercenary Mimic" — so its two copies in the archive leave
    /// no trace in any channel but this one.
    /// </para>
    /// </remarks>
    private void ReadCopiedObject(JsonElement pa)
    {
        if (Json.Int(pa, "id") is not { } annId) return;
        if (DetailInt(pa, "copyFromGrpid") is not { } from) return;

        // Present means "this wears off", and that is the whole of what it means here.
        // The codes seen are 1227 and 3128; Arena's card database has no Duration enum
        // to resolve either against, and their lengths are legible only by reading the
        // source cards, so a length is not something this can honestly report.
        var temporary = DetailInt(pa, "Duration") is not null;

        // Six of the archive's thirteen carry Arena's "nobody did this" affector —
        // 4294967293, which is -3 read as unsigned — and every one of them is a clone
        // arriving already copying something under its own replacement effect, with no
        // permanent to name as the cause. It needs no filter of its own: the value does
        // not fit in an int32, so Json.Int already answers null for it. Seats 1 and 2
        // are players, who do not copy things either.
        var affector = Json.Int(pa, "affectorId") is > 2 and var af ? Resolve(af) : (int?)null;

        foreach (var x in Json.Array(pa, "affectedIds"))
        {
            if (Json.Int(x) is not { } affected || affected <= 2) continue;
            var canonical = Resolve(affected);
            if (!_copiesSeen.Add((annId, canonical))) continue;

            // A permanent copying something under its own ability is one permanent, not
            // two. Dropped for the same reason a self-grant is: naming it as its own
            // cause reads as though something else were involved.
            var cause = affector == canonical ? null : affector;
            _newCopies.Add(new CopiedObject(canonical, OwnCardName(canonical), from, cause, temporary));
        }
    }

    /// <summary>
    /// Which card a permanent actually is, ignoring any name it has been given. See
    /// <see cref="ReadCopiedObject"/> for why a copy is the one thing that needs this.
    /// </summary>
    private string? OwnCardName(int id) =>
        _objects.TryGetValue(id, out var o) && o.GrpId > 0
            ? cards.CardForGrpId(o.GrpId)?.Name
            : null;

    /// <summary>
    /// Notes what set a triggered ability off. <c>affectorId</c> is the ability instance
    /// and the single affected id is the object that caused it — the opposite way round
    /// from <c>AnnotationType_AbilityInstanceCreated</c>, which runs source → new ability.
    /// </summary>
    /// <remarks>
    /// The direction was established from the archive rather than assumed, because the
    /// two annotations disagreeing about which end is which is exactly the kind of thing
    /// that produces a confidently backwards sentence. Of the 2,394 of these in the
    /// archive, 2,389 name a <c>GameObjectType_Ability</c> as the affector and all 2,394
    /// name an id that <c>AbilityInstanceCreated</c> had already announced as a new
    /// ability; the affected ids are cards and tokens. Both readings agree, so the
    /// affector is the ability and the affected id is its cause.
    /// </remarks>
    private void ReadTriggerCause(JsonElement pa)
    {
        if (Json.Int(pa, "affectorId") is not { } ability) return;
        foreach (var x in Json.Array(pa, "affectedIds"))
        {
            // Seats 1 and 2 are players, not objects. A trigger caused by a player has
            // nothing to name.
            if (Json.Int(x) is not { } cause || cause <= 2) continue;
            _triggerCauses[ability] = cause;
            return;
        }
    }

    /// <summary>
    /// The object that set a triggered ability off, from
    /// <c>AnnotationType_TriggeringObject</c>. Null when Arena did not say — two thirds
    /// of triggered abilities have no triggering object at all, because nothing caused
    /// them but the turn advancing.
    /// </summary>
    public int? CauseOf(int abilityInstanceId)
    {
        if (_triggerCauses.TryGetValue(abilityInstanceId, out var direct)) return direct;
        return _triggerCauses.TryGetValue(Resolve(abilityInstanceId), out var viaAlias)
            ? viaAlias
            : null;
    }

    private void ClearCombatState()
    {
        foreach (var o in _objects.Values)
        {
            o.AttackState = "";
            o.BlockState = "";
            o.AttackTargetId = null;
            o.BlockedAttackerIds = [];
        }
    }

    private bool InPlay(TrackedObject obj) =>
        _zoneTypes.TryGetValue(obj.ZoneId, out var zone) && zone == "ZoneType_Battlefield";

    /// <summary>
    /// Whether this instance is on the battlefield, as far as the log has said. False
    /// for an object in any other zone and for one the log never described — either
    /// way, nothing the player was looking at is standing there.
    /// </summary>
    public bool OnBattlefield(int instanceId) => Get(instanceId) is { } o && InPlay(o);

    /// <summary>
    /// power/toughness, which arrive either as a number or as <c>{ "value": n }</c>.
    /// </summary>
    /// <remarks>
    /// An empty object is zero, not silence. Arena serializes protobuf with default
    /// values omitted, so a power of 0 arrives as <c>"power": {}</c> — reading that as
    /// "unknown" left the previous value standing, and a creature whose buff had ended
    /// kept the buffed number. "Sazh's Chocobo 4/5 returns to 4/1" was a 0/1 Chocobo.
    /// <para>
    /// The property being absent altogether is different and still means unknown: a
    /// non-creature has no power at all, and 12-key entries that describe an enchantment
    /// sit right beside 15-key ones that describe a creature.
    /// </para>
    /// </remarks>
    private static int? ReadStat(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Object) return Json.Int(el);
        return Json.Int(el, "value") ?? 0;
    }

    /// <summary>Follows the id-change chain to the current id. Cycle-safe.</summary>
    public int Resolve(int instanceId)
    {
        var seen = new HashSet<int>();
        var cur = instanceId;
        while (_alias.TryGetValue(cur, out var next) && seen.Add(cur)) cur = next;
        return cur;
    }

    /// <summary>
    /// Follows the id-change chain the way <see cref="Resolve"/> does, but stops short of
    /// a link that also changes which card the object is. Cycle-safe.
    /// </summary>
    /// <remarks>
    /// An Adventure card is one card wearing two faces, and Arena renumbers it as it
    /// moves between them: the spell is on the stack under one id carrying the Adventure's
    /// grpId, and lands in exile under another carrying the creature's. That is one chain
    /// holding two identities, which is not what <see cref="Resolve"/> is for — running it
    /// to the end folds both faces into one timeline, and since every sample from a single
    /// message shares a stamp, the face that arrived last then answers every question
    /// about the message, including what dealt the damage (#75).
    /// <para>
    /// <see cref="PermanentLabels"/> is the only caller, because labelling is the only job
    /// that asks "what was this at that moment". <see cref="Resolve"/> is left alone: the
    /// other thing the chain is for is deciding that two annotations concern the same
    /// object, which they do whether the face changed or not, and that is what pairs a
    /// cast with its resolution.
    /// </para>
    /// </remarks>
    public int ResolveFace(int instanceId)
    {
        var seen = new HashSet<int>();
        var cur = instanceId;
        while (_alias.TryGetValue(cur, out var next) && seen.Add(cur))
        {
            if (ChangesTheCard(cur, next)) return cur;
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// True when both ends of a rename are known and are different cards. A grpId of zero
    /// is Arena declining to say, which is not a disagreement.
    /// </summary>
    private bool ChangesTheCard(int from, int to) =>
        _objects.TryGetValue(from, out var a) && a.GrpId > 0 &&
        _objects.TryGetValue(to, out var b) && b.GrpId > 0 &&
        a.GrpId != b.GrpId;

    public TrackedObject? Get(int instanceId)
    {
        var id = Resolve(instanceId);
        if (_objects.TryGetValue(id, out var o)) return o;
        if (_objects.TryGetValue(instanceId, out var orig)) return orig;

        // Backwards along the rename, as a last resort. A permanent that dies is
        // renamed and then reported dead under the new id in the same message, and that
        // new id is never described — Arena renames 389 to 521 and says 521 changed
        // zones, while the object it is talking about is in the same message as 389.
        // Following only the forward map left those deaths as "Unknown card", which is
        // exactly the wrong thing to lose: five tokens dying to one board wipe is the
        // answer to how a game was lost.
        var seen = new HashSet<int>();
        var back = instanceId;
        while (_aliasBack.TryGetValue(back, out var older) && seen.Add(back))
            if (_objects.TryGetValue(back = older, out var was)) return was;

        return null;
    }

    public string NameOf(int instanceId) => NameOf(instanceId, null);

    /// <summary>
    /// What to call an object, resolved from whichever of Arena's four links answers
    /// first: its own localized name, its card, the card that produced it, or — failing
    /// all of those — the object it hangs off.
    /// </summary>
    /// <remarks>
    /// The parent hop exists because an emblem's ability names no card anywhere in
    /// itself. Its <c>objectSourceGrpId</c> is the emblem's own grpId of 2, which is not
    /// in the card database, so every emblem ability used to print as "Card #190846".
    /// The emblem it hangs off does carry a real source, and following one link reaches
    /// it: ability → emblem → Tezzeret, Cruel Captain. The same hop covers a linked
    /// ability whose source is a land, whose <c>objectSourceGrpId</c> arrives as 5.
    /// <para>
    /// A parent that cannot be named either is not used, because "Card #2's ability" is
    /// no better than "Card #190846" and is longer. The visited set guards against a
    /// parent chain that loops; nothing in the archive has one, and a hang here would be
    /// a hang in the middle of a render.
    /// </para>
    /// </remarks>
    private string NameOf(int instanceId, HashSet<int>? visited)
    {
        var o = Get(instanceId);
        if (o is null) return CardNames.Unknown;

        // Emblems localize their name to the empty string rather than to nothing, so a
        // bare null check would name them "" and stop before the links that work.
        if (o.NameLocId is { } loc && cards.NameForLocId(loc) is { Length: > 0 } byLoc)
            return byLoc;
        // Only for objects whose grpId is a card. An ability's grpId indexes the card
        // database's Abilities table, and the two id spaces overlap — 96573 is both
        // Sazh's Chocobo's landfall ability and the card Ureni of the Unwritten. Asking
        // Cards for it answered with a 7/7 that was never in the game, and the line read
        // "Escape Tunnel triggers Ureni of the Unwritten": 294 trigger lines across 46
        // matches naming a card the player never saw, and the same names poisoning the
        // index's search text. The source and parent links below are the ones that
        // resolve these correctly, and this lookup was reaching them first.
        if (!IsDerived(o) && cards.CardForGrpId(o.GrpId) is { } card) return card.Name;
        if (o.ObjectSourceGrpId is { } srcGrp && cards.CardForGrpId(srcGrp) is { } src)
            return src.Name + Belonging(o);

        if (o.ParentId is { } parent)
        {
            visited ??= [];
            if (visited.Add(o.InstanceId))
            {
                var name = NameOf(parent, visited);
                if (!CardNames.IsPlaceholder(name)) return name + Belonging(o);
            }
        }

        // Before the id fallback, because 3 is not an id the database is ever going to
        // answer for. See CardNames.FaceDown.
        return o.GrpId == FaceDownGrpId ? CardNames.FaceDown : $"Card #{o.GrpId}";
    }

    /// <summary>
    /// The grpId Arena gives a card whose face it is not showing us. Not a card, and in
    /// no card database — see <see cref="CardNames.FaceDown"/>.
    /// </summary>
    private const int FaceDownGrpId = 3;

    /// <summary>
    /// How an object reads when it has to be named through whatever produced it. An
    /// emblem is its planeswalker's emblem; everything else that gets here is an ability.
    /// </summary>
    private static string Belonging(TrackedObject o) =>
        o.Type == "GameObjectType_Emblem" ? "'s emblem" : "'s ability";

    /// <summary>
    /// True for an object that is not a card and whose grpId therefore means something
    /// else — an ability instance indexes the Abilities table, an emblem carries the
    /// constant 2. Both have to be named through whatever produced them.
    /// </summary>
    private static bool IsDerived(TrackedObject o) =>
        o.Type is "GameObjectType_Ability" or "GameObjectType_Emblem";

    /// <summary>
    /// The permanent an ability instance belongs to, named bare — "Lander", not
    /// "Lander's ability". An activation line names what the player activated, and a
    /// player activates the permanent, not the ability object Arena spawned for it.
    /// </summary>
    /// <remarks>
    /// The parent instance is preferred over <c>objectSourceGrpId</c>, the reverse of
    /// <see cref="NameOf(int)"/>'s order, because an instance id is something the
    /// deferred naming pass can hang a disambiguating letter on — "Rabbit (a)" — while
    /// a name reached through a grpId is only ever the printed card name. Both ends at
    /// the same card when both resolve. (null, null) when neither does, and the caller
    /// keeps whatever line it already had.
    /// </remarks>
    public (int? InstanceId, string? Name) AbilitySource(int abilityInstanceId)
    {
        var o = Get(abilityInstanceId);
        if (o is null) return (null, null);

        if (o.ParentId is { } parent)
        {
            var name = NameOf(parent);
            if (!CardNames.IsPlaceholder(name)) return (parent, name);
        }
        if (o.ObjectSourceGrpId is { } srcGrp && cards.CardForGrpId(srcGrp) is { } src)
            return (null, src.Name);

        return (null, null);
    }

    public string SeatName(int seat) => seat == LocalSeat ? "You" : "Opponent";

    /// <summary>
    /// What a spell or ability was aimed at, from AnnotationType_TargetSpec. Empty
    /// when it targeted nothing, or when Arena did not tell us.
    /// </summary>
    public IReadOnlyList<int> TargetsOf(int instanceId)
    {
        if (_targets.TryGetValue(instanceId, out var direct)) return direct;
        var resolved = Resolve(instanceId);
        return _targets.TryGetValue(resolved, out var viaAlias) ? viaAlias : [];
    }

    /// <summary>
    /// Creatures a seat currently controls on the battlefield, in a stable order.
    /// An object is only counted once — the alias map means a card can be reachable
    /// under several ids after moving zones.
    /// </summary>
    public IReadOnlyList<TrackedObject> CreaturesOnBattlefield(int seat)
    {
        var seen = new HashSet<int>();
        var result = new List<TrackedObject>();

        foreach (var o in _objects.Values)
        {
            if (o.ControllerSeat != seat) continue;
            if (!o.CardTypes.Contains("CardType_Creature")) continue;
            if (!_zoneTypes.TryGetValue(o.ZoneId, out var zt) || zt != "ZoneType_Battlefield")
                continue;
            if (!seen.Add(Resolve(o.InstanceId))) continue;
            result.Add(o);
        }

        return result.OrderBy(o => o.InstanceId).ToList();
    }

    internal static bool HasType(JsonElement annotation, string type)
    {
        foreach (var x in Json.Array(annotation, "type"))
            if (x.ValueKind == JsonValueKind.String && x.GetString() == type) return true;
        return false;
    }

    internal static int? DetailInt(JsonElement annotation, string key)
    {
        foreach (var d in Json.Array(annotation, "details"))
        {
            if (Json.Str(d, "key") != key) continue;
            foreach (var n in Json.Array(d, "valueInt32"))
                if (Json.Int(n) is { } iv) return iv;
        }
        return null;
    }

    /// <summary>
    /// Every int under a detail key. Arena omits <c>valueInt32</c> entirely when the
    /// list is empty — a scry that bottoms nothing sends a bare {"key":"bottomIds"} —
    /// so an empty result and a missing key are the same thing here.
    /// </summary>
    internal static IReadOnlyList<int> DetailInts(JsonElement annotation, string key)
    {
        var result = new List<int>();
        foreach (var d in Json.Array(annotation, "details"))
        {
            if (Json.Str(d, "key") != key) continue;
            foreach (var n in Json.Array(d, "valueInt32"))
                if (Json.Int(n) is { } iv) result.Add(iv);
        }
        return result;
    }

    internal static string? DetailString(JsonElement annotation, string key)
    {
        foreach (var d in Json.Array(annotation, "details"))
        {
            if (Json.Str(d, "key") != key) continue;
            foreach (var s in Json.Array(d, "valueString"))
                if (s.ValueKind == JsonValueKind.String) return s.GetString();
        }
        return null;
    }
}
