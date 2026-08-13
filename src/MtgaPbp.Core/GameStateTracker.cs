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

public sealed class GameStateTracker(ICardDb cards)
{
    private readonly Dictionary<int, TrackedObject> _objects = [];
    private readonly Dictionary<int, int> _alias = [];   // old id -> new id
    private readonly Dictionary<int, int> _life = [];
    private readonly Dictionary<int, string> _zoneTypes = [];
    private readonly Dictionary<int, List<int>> _targets = [];   // source id -> target ids
    private readonly Dictionary<int, List<StatSample>> _stats = [];
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

    private readonly List<int> _newAttackers = [];
    private readonly List<int> _newBlockers = [];
    private readonly List<(int Id, int Level)> _newLevels = [];

    /// <summary>
    /// Creatures that declared an attack in the message just applied. Combat is not
    /// announced by an annotation — it only shows up as a state change on the object —
    /// so these are reported once, on the transition into the declared state.
    /// Cleared at the start of every <see cref="Apply"/>.
    /// </summary>
    public IReadOnlyList<int> NewAttackers => _newAttackers;

    /// <summary>Creatures that declared a block in the message just applied.</summary>
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
    /// Every statline change seen, under the instance id Arena used at the time. Ids
    /// change when a card moves zones, so a consumer that wants one timeline per card
    /// has to fold these onto <see cref="Resolve"/>d ids itself.
    /// </summary>
    public IEnumerable<(int InstanceId, IReadOnlyList<StatSample> Samples)> StatHistory =>
        _stats.OrderBy(p => p.Key).Select(p => (p.Key, (IReadOnlyList<StatSample>)p.Value));

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
        }

        foreach (var go in Json.Array(gsm, "gameObjects")) UpsertObject(go);

        // Targets live here, not in `annotations`. AnnotationType_TargetSpec names what
        // a spell or ability was aimed at — affectorId is the source, affectedIds are
        // the targets. Missing this array is why targeting looked unavailable.
        foreach (var pa in Json.Array(gsm, "persistentAnnotations"))
        {
            if (HasType(pa, "AnnotationType_ClassLevel")) ReadClassLevel(pa);
            if (HasType(pa, "AnnotationType_TriggeringObject")) ReadTriggerCause(pa);

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
            if (orig is { } o && next is { } n && o != n) _alias[o] = n;
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
        if (Json.Int(go, "name") is { } nm) obj.NameLocId = nm;
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
        // across many diffs is announced once.
        if (Json.Str(go, "attackState") is { } atk)
        {
            // Compare against the attacking states specifically, not "has any state":
            // a creature attacks on many turns, and its state returns to none in
            // between. Testing for a non-empty string would report only its first
            // attack of the game and silently drop every later one.
            var wasAttacking = obj.AttackState is "AttackState_Declared" or "AttackState_Attacking";
            obj.AttackState = atk;
            if (!wasAttacking && atk is "AttackState_Declared" or "AttackState_Attacking")
                _newAttackers.Add(id);
        }
        if (Json.Str(go, "blockState") is { } blk)
        {
            var wasBlocking = obj.BlockState is "BlockState_Declared" or "BlockState_Blocking";
            obj.BlockState = blk;
            if (!wasBlocking && blk is "BlockState_Declared" or "BlockState_Blocking")
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

    public TrackedObject? Get(int instanceId)
    {
        var id = Resolve(instanceId);
        if (_objects.TryGetValue(id, out var o)) return o;
        return _objects.TryGetValue(instanceId, out var orig) ? orig : null;
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

        return $"Card #{o.GrpId}";
    }

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
