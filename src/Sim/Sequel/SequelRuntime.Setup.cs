// The set-up of a mission of the remake's own campaigns: the players by role, the start (buildings, the epoch, units,
// the workers sent to work), the night, the set pieces, the script's setup, the abandoned generating stations.
// Ported from remake/src/game/sequel/runtime.js (source, build, startWork, brains, camera) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Sequel;

public sealed partial class SequelRuntime
{
    /// <summary>the map recipe with what must stay free (the base, zones, map.clear) and the mission's name</summary>
    public static JsonObject MapRecipe(JsonObject def)
    {
        var map = (JsonObject)(def["map"]?.DeepClone() ?? new JsonObject());
        var clear = map["clear"] as JsonArray ?? new JsonArray();
        var c2 = new JsonArray();
        foreach (var c in clear) c2.Add(c?.DeepClone());
        if (def["start"]?["buildings"] is JsonArray bs) foreach (var b in bs) if (b is JsonArray ba && ba.Count >= 3) c2.Add(new JsonArray(ba[1].Num(), ba[2].Num(), 24));
        if (def["zones"] is JsonArray zs) foreach (var z in zs) c2.Add(new JsonArray(z?["x"].Num() ?? 0, z?["z"].Num() ?? 0, (z?["r"].Num() ?? 0) + 4));
        map["clear"] = c2;
        map["name"] = def["title"].Str();
        return map;
    }

    /// <summary>runtime.js source(): the generating stations of the map are buildings, not decoration - abandoned plants
    /// the player can take over</summary>
    public void TakePlantSpots(MapSource S)
    {
        foreach (var d in S.Decor) if (d.Model == "sq_power_station") _plantSpots.Add(new[] { d.X, d.Z, d.Rot });
        S.Decor.RemoveAll(d => d.Model == "sq_power_station");
    }

    /// <summary>the tribes whose models load (a scenario: all four - its script may put down anybody's units)</summary>
    public IEnumerable<string> Tribes()
    {
        if (Kind == "scenario") return SequelCatalog.SCENARIO_TRIBES;
        var l = new List<string> { "SEAS" };
        if (Def["tribes"] is JsonArray t) foreach (var x in t) l.Add(x.Str());
        return l;
    }

    /// <summary>models the mission needs besides its map's: its machines, civilians, creatures, set pieces</summary>
    public IEnumerable<string> ExtraModels()
    {
        var o = new ModelSet();
        foreach (var m in new[] { "sq_pen", "sq_windmill_rotor", "sq_waterwheel", "sq_searchlamp", "aje_warpgate" }) o.Use(m);
        var D = W.Data;
        void Cls(string c) { for (int l = 1; l <= 5; l++) { var s = D.Stats(c, l, null); if (s != null && !string.IsNullOrEmpty(s.Gfx)) o.Use(s.Gfx.ToLowerInvariant()); } }
        if (Kind == "sequel") foreach (var c in Classes.All.Keys) Cls(c);
        var cr = new List<string>();
        if (Def["creatures"] is JsonArray ca) foreach (var c in ca) cr.Add(c.Str());
        foreach (var c in cr.Concat(new[] { "Velociraptor", "Deinonychus", "Allosaurus" })) Cls(c);
        if (Def["models"] is JsonArray ms) foreach (var m in ms) { if (m is JsonArray ml) foreach (var x in ml) Cls(x.Str()); else Cls(m.Str()); }
        if (Def["pieces"] is JsonArray ps) foreach (var p in ps) o.Use(p?["model"].Str());
        if (Def["props"] is JsonArray pr) foreach (var p in pr) o.Use(p.Str());
        return o;
    }

    static string? StrOrNull(JsonNode? n) => n is JsonValue v ? v.Str() : null;
    /// <summary>a start entry's class: a name or a list (the first the data has)</summary>
    string ClsOf(JsonNode? n) => n is JsonArray a ? Cls(a.Select(x => x.Str()).ToList()) : n.Str();

    /// <summary>runtime.js build(): the players and the start; then the script's setup</summary>
    public Player Build(Rules.Rules D)
    {
        var def = Def;
        var roles = new List<string> { "me", "outbreak", "civilians" };
        roles.AddRange(EXTRA_ROLES.Where(r => def[r] is JsonObject || (def[r] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.True)));
        W.Players.Clear();
        foreach (var r in roles)
        {
            var o = def[r] as JsonObject;
            var looks = def["looks"]?[r] as JsonObject;
            var look = ROLE_LOOK[r];
            string name = looks?["name"] is JsonValue ln ? ln.Str() : o?["name"] is JsonValue on ? on.Str() : r == "me" && Kind == "scenario" ? TRIBE_NAMES.GetValueOrDefault(Tribe, Tribe) : look.Name;
            int color = look.Color; int? party = look.Party;
            var colN = looks?["color"] ?? o?["color"];
            if (colN is JsonValue cv) { color = (int)cv.Num(); var pn = looks?["party"] ?? o?["party"]; party = pn is JsonValue pv ? (int)pv.Num() : color; }
            string tribe = r == "me" ? Tribe : r == "aje" ? "Aje" : StrOrNull(o?["tribe"]) ?? "SEAS";
            var p = new Player(ROLE_IDS[r], tribe, new PlayerOpts { Ai = r != "me", Name = name, Color = color, PartyColor = party, Team = look.Team, Rules = D });
            Roles[r] = p;
            W.Players.Add(p);
        }
        var me = Roles["me"];
        var res = def["res"] as JsonObject;
        if (res != null) foreach (var (k, v) in res) me.Res[k] = v.Num();
        else { me.Res["food"] = 400; me.Res["wood"] = 400; me.Res["stone"] = 300; }
        me.PopMax = def["popMax"] is JsonValue pm && pm.Num() > 0 ? pm.Num() : 52;
        // the civilians and the outbreak need no economy; everybody fights the outbreak
        foreach (var p in W.Players) if (p != me) { p.Res["food"] = 5000; p.Res["wood"] = 5000; p.Res["stone"] = 5000; }
        // (a scenario's computer players may start with less: def.<role>.res)
        foreach (var r in EXTRA_ROLES) if (PlayerOf(r) is { } p && def[r]?["res"] is JsonObject rr) foreach (var (k, v) in rr) p.Res[k] = v.Num();
        if (def["aje"] != null && def["ajeFriendly"].Bool() && PlayerOf("aje") is { } aje) { aje.SetRelation(me, 2); me.SetRelation(aje, 2); }
        // a scenario's other tribes may start at peace with the player (neutral: 1) - the script changes it
        foreach (var r in EXTRA_ROLES) if (PlayerOf(r) is { } p && def[r]?["relation"] is JsonValue rel) { p.SetRelation(me, (int)rel.Num()); me.SetRelation(p, (int)rel.Num()); }
        // the start
        if (def["zones"] is JsonArray zs)
            foreach (var z in zs)
                if (z is JsonObject zo) Zones.Set(zo["name"].Str(), new SqZone { Name = zo["name"].Str(), X = zo["x"].Num(), Z = zo["z"].Num(), R = zo["r"].Num(), Kind = zo["kind"].Str() });
        if (def["start"]?["buildings"] is JsonArray sb)
            foreach (var b in sb)
                if (b is JsonArray ba && ba.Count >= 3) { var e = W.PlaceAdapted(ClsOf(ba[0]), me, ba[1].Num(), ba[2].Num(), ba.Count > 3 ? ba[3].Num() : 0, true); if (e != null) W.RecomputeCapsOf(me); }
        // the epoch it starts in (from mission 4 on epoch 2, from mission 7 on epoch 3; def.epoch overrides)
        int epoch = def["epoch"] is JsonValue ep ? (int)ep.Num() : Kind == "scenario" ? 1 : Id >= 7 ? 3 : Id >= 4 ? 2 : 1;
        var hq = Home(me);
        for (int k = 2; hq != null && k <= epoch; k++)
        {
            var a = W.Data.Actions(me).Find(x => x.Kind == "Upgrades" && x.Id == "age_" + k);
            if (a != null) W.CompleteUpgrade(hq, a);
        }
        if (def["start"]?["units"] is JsonArray su)
            foreach (var u in su)
                if (u is JsonArray ua && ua.Count >= 3)
                    Spawn(ClsOf(ua[0]), "me", ua[1].Num(), ua[2].Num(), ua.Count > 3 && ua[3] != null ? (int)ua[3].Num(1) : 1, new SpawnOpts2 { Level = ua.Count > 4 && ua[4] != null ? (int)ua[4].Num(1) : 1 });
        W.RecomputeCapsOf(me);
        StartWork(me);
        if (NightOn) SetNight(true);
        if (def["pieces"] is JsonArray ps)
            foreach (var p in ps)
                if (p is JsonObject po) Piece(po["id"].Str(), po["model"].Str(), po["x"].Num(), po["z"].Num(), po["y"] is JsonValue y ? y.Num() : null, po["rot"].Num(0), po["spin"].Num(0));
        return me;
    }

    /// <summary>the script's setup and what follows it (runtime.js build's end): the introduction of what the mission
    /// brings in, the map's stations the mission did not place itself standing abandoned (the civilians')</summary>
    public void RunSetup()
    {
        Call("setup");
        if (Unlocks(false).Any()) After(8, "_intro");
        var civ = PlayerOf("civilians");
        foreach (var s in _plantSpots)
        {
            if (W.Buildings.Any(b => b.Alive && PowerGrid.IsPlant(b) && Math.Sqrt((b.Pos.X - s[0]) * (b.Pos.X - s[0]) + (b.Pos.Z - s[1]) * (b.Pos.Z - s[1])) < 20)) continue;
            W.PlaceAdapted("sq_power_station", civ, s[0], s[1], s[2], true);
        }
    }

    /// <summary>the starting workers go to work at once, spread evenly over food, wood and stone - the nearest source of
    /// each to the headquarters; a kind the map has none of near the base goes to the next kind</summary>
    void StartWork(Player me)
    {
        var hq = Home(me);
        var ws = W.Units.Where(u => u.Alive && u.Owner == me && u.IsWorker).ToList();
        V3? at = hq?.Pos ?? (ws.Count > 0 ? ws[0].Pos : null);
        if (at == null) return;
        var src = new List<ResNode>();
        foreach (var r in Rules.Rules.RES) if (W.NearestResource(at.Value.X, at.Value.Z, r, 120) is { } n) src.Add(n);
        if (src.Count == 0) return;
        for (int i = 0; i < ws.Count; i++) W.Order(ws[i], new Order("gather") { Target = src[i % src.Count] });
    }

    /// <summary>where the camera starts: def.camera, else the first start unit's place</summary>
    public (double X, double Z) Camera()
    {
        if (Def["camera"] is JsonArray c && c.Count >= 2) return (c[0].Num(), c[1].Num());
        if (Def["start"]?["units"] is JsonArray u && u.Count > 0 && u[0] is JsonArray f && f.Count >= 3) return (f[1].Num(), f[2].Num());
        return (0, 0);
    }
}
