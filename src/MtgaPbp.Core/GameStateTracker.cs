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

    public void Apply(JsonElement gsm)
    {
        if (gsm.TryGetProperty("gameInfo", out var gi) &&
            gi.TryGetProperty("gameNumber", out var gn) && gn.TryGetInt32(out var gnv))
            GameNumber = gnv;

        if (gsm.TryGetProperty("turnInfo", out var ti))
        {
            if (ti.TryGetProperty("turnNumber", out var v) && v.TryGetInt32(out var tn)) Turn = tn;
            if (ti.TryGetProperty("activePlayer", out v) && v.TryGetInt32(out var ap)) ActiveSeat = ap;
            if (ti.TryGetProperty("phase", out v) && v.TryGetInt32(out var ph)) Phase = ph;
            if (ti.TryGetProperty("step", out v) && v.TryGetInt32(out var st)) Step = st;
        }

        if (gsm.TryGetProperty("players", out var players) &&
            players.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in players.EnumerateArray())
            {
                if (p.TryGetProperty("systemSeatNumber", out var s) && s.TryGetInt32(out var seat) &&
                    p.TryGetProperty("lifeTotal", out var l) && l.TryGetInt32(out var life))
                    _life[seat] = life;
            }
        }

        if (gsm.TryGetProperty("zones", out var zones) && zones.ValueKind == JsonValueKind.Array)
        {
            foreach (var z in zones.EnumerateArray())
            {
                if (z.TryGetProperty("zoneId", out var zi) && zi.TryGetInt32(out var zid) &&
                    z.TryGetProperty("type", out var zt) && zt.ValueKind == JsonValueKind.String)
                    _zoneTypes[zid] = zt.GetString()!;
            }
        }

        if (gsm.TryGetProperty("gameObjects", out var objs) && objs.ValueKind == JsonValueKind.Array)
            foreach (var go in objs.EnumerateArray()) UpsertObject(go);

        // Aliases must be applied before EventExtractor reads this message's annotations.
        if (gsm.TryGetProperty("annotations", out var anns) && anns.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in anns.EnumerateArray())
            {
                if (!HasType(a, "AnnotationType_ObjectIdChanged")) continue;
                var orig = DetailInt(a, "orig_id");
                var next = DetailInt(a, "new_id");
                if (orig is { } o && next is { } n && o != n) _alias[o] = n;
            }
        }
    }

    private void UpsertObject(JsonElement go)
    {
        if (!go.TryGetProperty("instanceId", out var idEl) || !idEl.TryGetInt32(out var id)) return;

        if (!_objects.TryGetValue(id, out var obj))
            _objects[id] = obj = new TrackedObject { InstanceId = id };

        if (go.TryGetProperty("grpId", out var v) && v.TryGetInt32(out var grp)) obj.GrpId = grp;
        if (go.TryGetProperty("name", out v) && v.TryGetInt32(out var nm)) obj.NameLocId = nm;
        if (go.TryGetProperty("type", out v) && v.ValueKind == JsonValueKind.String)
            obj.Type = v.GetString()!;
        if (go.TryGetProperty("ownerSeatId", out v) && v.TryGetInt32(out var os)) obj.OwnerSeat = os;
        if (go.TryGetProperty("controllerSeatId", out v) && v.TryGetInt32(out var cs))
            obj.ControllerSeat = cs;
        if (go.TryGetProperty("zoneId", out v) && v.TryGetInt32(out var zi)) obj.ZoneId = zi;
        if (go.TryGetProperty("power", out v)) obj.Power = ReadStat(v);
        if (go.TryGetProperty("toughness", out v)) obj.Toughness = ReadStat(v);
        if (go.TryGetProperty("damage", out v) && v.TryGetInt32(out var dmg)) obj.Damage = dmg;
        if (go.TryGetProperty("isTapped", out v)) obj.IsTapped = v.ValueKind == JsonValueKind.True;
        if (go.TryGetProperty("loyalty", out v)) obj.Loyalty = ReadStat(v);
        if (go.TryGetProperty("objectSourceGrpId", out v) && v.TryGetInt32(out var src))
            obj.ObjectSourceGrpId = src;
    }

    /// <summary>power/toughness arrive either as a number or as { "value": n }.</summary>
    private static int? ReadStat(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("value", out var v) && v.TryGetInt32(out var vn)) return vn;
        return null;
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
        if (!annotation.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var x in t.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String && x.GetString() == type) return true;
        return false;
    }

    internal static int? DetailInt(JsonElement annotation, string key)
    {
        if (!annotation.TryGetProperty("details", out var ds) || ds.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var d in ds.EnumerateArray())
        {
            if (!d.TryGetProperty("key", out var k) || k.GetString() != key) continue;
            if (d.TryGetProperty("valueInt32", out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var n in v.EnumerateArray())
                    if (n.TryGetInt32(out var iv)) return iv;
        }
        return null;
    }

    internal static string? DetailString(JsonElement annotation, string key)
    {
        if (!annotation.TryGetProperty("details", out var ds) || ds.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var d in ds.EnumerateArray())
        {
            if (!d.TryGetProperty("key", out var k) || k.GetString() != key) continue;
            if (d.TryGetProperty("valueString", out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var s in v.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String) return s.GetString();
        }
        return null;
    }
}
