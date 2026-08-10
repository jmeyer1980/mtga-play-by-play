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
    public string AttackState = "";
    public string BlockState = "";
    public int? AttackTargetId;
    public IReadOnlyList<int> BlockedAttackerIds = [];
    public readonly Dictionary<int, int> Counters = [];
}

public sealed class GameStateTracker(ICardDb cards)
{
    private readonly Dictionary<int, TrackedObject> _objects = [];
    private readonly Dictionary<int, int> _alias = [];   // old id -> new id
    private readonly Dictionary<int, int> _life = [];
    private readonly Dictionary<int, string> _zoneTypes = [];

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

    /// <summary>
    /// Creatures that declared an attack in the message just applied. Combat is not
    /// announced by an annotation — it only shows up as a state change on the object —
    /// so these are reported once, on the transition into the declared state.
    /// Cleared at the start of every <see cref="Apply"/>.
    /// </summary>
    public IReadOnlyList<int> NewAttackers => _newAttackers;

    /// <summary>Creatures that declared a block in the message just applied.</summary>
    public IReadOnlyList<int> NewBlockers => _newBlockers;

    public void Apply(JsonElement gsm)
    {
        _newAttackers.Clear();
        _newBlockers.Clear();

        if (Json.Obj(gsm, "gameInfo") is { } gi && Json.Int(gi, "gameNumber") is { } gnv)
            GameNumber = gnv;

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

        if (Json.Int(go, "grpId") is { } grp) obj.GrpId = grp;
        if (Json.Int(go, "name") is { } nm) obj.NameLocId = nm;
        if (Json.Str(go, "type") is { } ty) obj.Type = ty;
        if (Json.Int(go, "ownerSeatId") is { } os) obj.OwnerSeat = os;
        if (Json.Int(go, "controllerSeatId") is { } cs) obj.ControllerSeat = cs;
        if (Json.Int(go, "zoneId") is { } zi) obj.ZoneId = zi;
        if (ReadStat(go, "power") is { } pw) obj.Power = pw;
        if (ReadStat(go, "toughness") is { } tg) obj.Toughness = tg;
        if (Json.Int(go, "damage") is { } dmg) obj.Damage = dmg;
        if (go.TryGetProperty("isTapped", out var tap))
            obj.IsTapped = tap.ValueKind == JsonValueKind.True;
        if (ReadStat(go, "loyalty") is { } ly) obj.Loyalty = ly;
        if (Json.Int(go, "objectSourceGrpId") is { } src) obj.ObjectSourceGrpId = src;

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

    /// <summary>power/toughness arrive either as a number or as { "value": n }.</summary>
    private static int? ReadStat(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el)) return null;
        return el.ValueKind == JsonValueKind.Object ? Json.Int(el, "value") : Json.Int(el);
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

    public string NameOf(int instanceId)
    {
        var o = Get(instanceId);
        if (o is null) return $"#{instanceId}";

        if (o.NameLocId is { } loc && cards.NameForLocId(loc) is { } byLoc) return byLoc;
        if (cards.CardForGrpId(o.GrpId) is { } card) return card.Name;
        if (o.ObjectSourceGrpId is { } srcGrp && cards.CardForGrpId(srcGrp) is { } src)
            return $"{src.Name}'s ability";
        return $"Card #{o.GrpId}";
    }

    public string SeatName(int seat) => seat == LocalSeat ? "You" : "Opponent";

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
