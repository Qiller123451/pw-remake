// The C# side of the rules golden test: the answers of src/Sim/Rules in the shape tests/golden/rules_dump.mjs writes
// for the JS rules (field names as in the JS objects).
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Tests;

public static class RulesGolden
{
    static JsonNode? S(string? s) => s == null ? null : JsonValue.Create(s);
    static JsonNode N(double d) => JsonValue.Create(d)!;
    static JsonNode B(bool b) => JsonValue.Create(b)!;
    static JsonArray Arr(IEnumerable<string>? l) { var a = new JsonArray(); if (l != null) foreach (var s in l) a.Add(JsonValue.Create(s)); return a; }
    static JsonNode? ArrOrNull(IEnumerable<string>? l) => l == null ? null : Arr(l);

    public static JsonNode? StatsN(LevelStats? s)
    {
        if (s == null) return null;
        var ab = new JsonObject();
        foreach (var (k, a) in s.Abilities)
        {
            var o = new JsonObject();
            foreach (var (kk, v) in a) o[kk] = v.Number.HasValue ? N(v.Number.Value) : S(v.Raw);
            ab[k] = o;
        }
        return new JsonObject
        {
            ["exists"] = B(s.Exists), ["level"] = N(s.Level), ["hp"] = N(s.Hp), ["fow"] = N(s.Fow), ["speed"] = N(s.Speed), ["maxspeed"] = N(s.MaxSpeed),
            ["tf"] = N(s.Tf), ["scalps"] = N(s.Scalps), ["gfx"] = S(s.Gfx), ["size"] = N(s.Size), ["captainlevel"] = N(s.CaptainLevel),
            ["aggressive"] = N(s.Aggressive),
            ["carry"] = new JsonObject { ["food"] = N(s.Carry.Food), ["wood"] = N(s.Carry.Wood), ["stone"] = N(s.Carry.Stone) },
            ["limits"] = new JsonObject { ["max_units"] = N(s.Limits.MaxUnits), ["max_food"] = N(s.Limits.MaxFood), ["max_wood"] = N(s.Limits.MaxWood), ["max_stone"] = N(s.Limits.MaxStone) },
            ["heal"] = s.Heal == null ? null : new JsonObject { ["radius"] = N(s.Heal.Radius), ["amount"] = N(s.Heal.Amount), ["mod"] = N(s.Heal.Mod) },
            ["healMod"] = new JsonObject { ["rel"] = N(s.HealModRel), ["abs"] = N(s.HealModAbs) },
            ["abilities"] = ab, ["maxworkers"] = N(s.MaxWorkers), ["agility"] = N(s.Agility), ["maxPassengers"] = N(s.MaxPassengers),
            ["captain"] = S(s.Captain),
        };
    }

    public static JsonNode? DefN(ObjectDef? d)
    {
        if (d == null) return null;
        var levels = new JsonArray();
        for (int l = 1; l <= 5; l++) levels.Add(StatsN(d.Levels[l]));
        return new JsonObject
        {
            ["name"] = S(d.Name), ["tribe"] = S(d.Tribe), ["type"] = S(d.Type), ["path"] = S(d.Path), ["desc"] = S(d.Desc), ["script"] = S(d.Script),
            ["aggressive"] = N(d.Aggressive), ["can_build"] = N(d.CanBuild), ["can_harvest"] = N(d.CanHarvest), ["maxhelpers"] = N(d.MaxHelpers),
            ["repletion"] = N(d.Repletion), ["agility"] = N(d.Agility), ["density"] = N(d.Density), ["growup"] = N(d.Growup),
            ["nightactive"] = B(d.NightActive), ["maxworkers"] = N(d.MaxWorkers), ["caste"] = S(d.Caste), ["standanim"] = S(d.StandAnim),
            ["fx"] = S(d.Fx), ["formationPos"] = N(d.FormationPos), ["wall"] = B(d.Wall), ["gate"] = B(d.Gate), ["coastal"] = B(d.Coastal),
            ["wallKind"] = S(d.WallKind), ["unlimited"] = Arr(d.Unlimited), ["maxPassengers"] = N(d.MaxPassengers),
            ["buildupAutoattack"] = B(d.BuildupAutoattack), ["delivery"] = Arr(d.Delivery), ["captain"] = S(d.Captain),
            ["captain_gfx"] = S(d.CaptainGfx), ["startTT"] = S(d.StartTT), ["globalStartTT"] = S(d.GlobalStartTT), ["unique"] = B(d.Unique),
            ["attackType"] = N(d.AttackType), ["ladder"] = B(d.Ladder), ["transportClass"] = N(d.TransportClass),
            ["levels"] = levels,
        };
    }

    static JsonObject Dict(Dictionary<string, double> d) { var o = new JsonObject(); foreach (var (k, v) in d) o[k] = N(v); return o; }

    public static JsonNode? WeaponN(Weapon? w)
    {
        if (w == null) return null;
        var anims = new JsonArray();
        foreach (var a in w.Anims) anims.Add(new JsonObject { ["name"] = S(a.Name), ["delay"] = N(a.Delay), ["shootdelay"] = N(a.ShootDelay), ["combo"] = N(a.Combo), ["follow"] = S(a.Follow) });
        var parts = new JsonArray();
        foreach (var p in w.Parts) parts.Add(new JsonObject { ["gfx"] = Arr(p.Gfx), ["link"] = S(p.Link), ["kind"] = S(p.Kind) });
        return new JsonObject
        {
            ["id"] = S(w.Id), ["path"] = S(w.Path), ["level"] = N(w.Level), ["secondary"] = N(w.Secondary), ["slot"] = N(w.Slot), ["dmg"] = N(w.Dmg),
            ["mindmg"] = N(w.MinDmg), ["enddmg"] = N(w.EndDmg), ["range"] = N(w.Range), ["minrange"] = N(w.MinRange), ["freq"] = N(w.Freq),
            ["splash"] = N(w.Splash), ["def"] = N(w.Def), ["rdef"] = N(w.RDef), ["ap"] = N(w.Ap), ["poison"] = N(w.Poison),
            ["poisonTicks"] = N(w.PoisonTicks), ["bulletspeed"] = N(w.BulletSpeed), ["falloff"] = N(w.Falloff), ["jitter"] = N(w.Jitter),
            ["size"] = N(w.Size), ["caste"] = S(w.Caste), ["penetration"] = B(w.Penetration), ["penAngle"] = N(w.PenAngle), ["fx"] = S(w.Fx),
            ["projectile"] = S(w.Projectile), ["bonus"] = Dict(w.Bonus), ["defBonus"] = Dict(w.DefBonus), ["anims"] = anims, ["parts"] = parts,
        };
    }

    public static JsonNode? WsN(WeaponStats? s) => s == null ? null : new JsonObject
    {
        ["w"] = S(s.W.Id), ["dmg"] = N(s.Dmg), ["endDmg"] = N(s.EndDmg), ["ap"] = N(s.Ap), ["splash"] = N(s.Splash), ["prot"] = N(s.Prot),
        ["rprot"] = N(s.RProt), ["range"] = N(s.Range), ["minRange"] = N(s.MinRange), ["dur"] = N(s.Dur), ["poison"] = N(s.Poison), ["poisonTicks"] = N(s.PoisonTicks),
    };

    public static JsonNode ActionN(ActionDef a)
    {
        var locs = new JsonArray();
        foreach (var l in a.Locs) locs.Add(new JsonObject { ["at"] = S(l.At), ["icon"] = S(l.Icon), ["desc"] = S(l.Desc), ["hidden"] = B(l.Hidden), ["localflags"] = ArrOrNull(l.LocalFlags), ["ui"] = l.Ui?.ToJson() });
        var res = new JsonArray();
        foreach (var r in a.Results) res.Add(new JsonObject { ["obj"] = S(r.Obj), ["path"] = S(r.Path), ["type"] = S(r.Type), ["cls"] = S(r.Cls), ["level"] = N(r.Level), ["rider"] = S(r.Rider), ["resultActions"] = Arr(r.ResultActions) });
        return new JsonObject
        {
            ["id"] = S(a.Id), ["tribe"] = S(a.Tribe), ["kind"] = S(a.Kind), ["cat"] = S(a.Cat), ["type"] = S(a.Type), ["path"] = S(a.Path),
            ["time"] = N(a.Time), ["visible"] = B(a.Visible), ["disabled"] = B(a.Disabled),
            ["cost"] = new JsonObject { ["food"] = N(a.Cost.Food), ["wood"] = N(a.Cost.Wood), ["stone"] = N(a.Cost.Stone), ["skulls"] = N(a.Cost.Skulls), ["power"] = N(a.Cost.Power) },
            ["req"] = Arr(a.Req), ["breq"] = Arr(a.BReq), ["levelReq"] = N(a.LevelReq), ["casteReq"] = Arr(a.CasteReq), ["tribes"] = Arr(a.Tribes),
            ["locs"] = locs, ["results"] = res, ["target"] = ArrOrNull(a.Target), ["targetOwner"] = ArrOrNull(a.TargetOwner), ["loc"] = S(a.Loc?.At),
        };
    }

    sealed class User : IActionUser { public int Level => 3; public ISet<string>? LocalInvents { get; } = new HashSet<string>(); }

    public static JsonNode ClassNode(Rules R, string n, IRulesOwner? o)
    {
        var ix = R.Info(n)!;
        var d = R.Def(n, o);
        var weapons = new JsonObject(); var lf = new JsonObject();
        for (int l = 1; l <= 5; l++)
        {
            var s = R.WeaponSetOf(n, l, o);
            weapons[l.ToString()] = new JsonObject { ["long"] = S(s.Long?.Id), ["medium"] = S(s.Medium?.Id), ["short"] = S(s.Short?.Id), ["all"] = Arr(s.All.Select(w => w.Id)) };
            lf[l.ToString()] = Arr(R.LevelFilters(n, l));
        }
        var wst = new JsonObject();
        foreach (var w in R.WeaponSetOf(n, 5, o).All) wst[w.Path] = new JsonObject { ["raw"] = WeaponN(w), ["stats"] = WsN(R.WeaponStatsOf(w, n, o)) };
        var statsAt = new JsonArray();
        for (int l = 1; l <= 5; l++) { var s = R.StatsAt(n, l, o); statsAt.Add(s == null ? null : N(s.Level)); }
        string kind = ix.Type == "BLDG" ? "building" : "unit";
        var acts = R.ActionsOf(o, n, kind, d?.Type ?? ix.Type).Where(a => !a.Remake).ToList();     // (the remake's own actions: not in the JS)
        var actions = new JsonArray(); foreach (var a in acts) actions.Add(ActionN(a));
        JsonNode? checks = null;
        if (o != null) { var ca = new JsonArray(); var u = new User(); foreach (var a in acts) ca.Add(S(R.Check(o, a, u))); checks = ca; }
        var tx = R.Text(n, o);
        return new JsonObject
        {
            ["def"] = DefN(d), ["minLevel"] = N(R.MinLevel(n, o)), ["column"] = S(R.Column(n, o)), ["weapons"] = weapons, ["weaponStats"] = wst,
            ["actions"] = actions, ["checks"] = checks, ["levelFilters"] = lf, ["statsAt"] = statsAt,
            ["text"] = new JsonObject { ["name"] = S(tx.Name), ["medium"] = S(tx.Medium), ["long"] = S(tx.Long) },
        };
    }

    sealed class FreshOwner : IRulesOwner
    {
        public FreshOwner(TechTree tt, string tribe) { Tt = tt; Tribe = tribe; }
        public TechTree? Tt { get; }
        public string Tribe { get; }
        public bool Debug => false;
        public int MaxEpoch => 0;
        public bool ForeignActions => false;
    }

    public static JsonNode Misc(Rules R, JsonNode js)
    {
        var o = new JsonObject();
        var pr = new JsonObject(); foreach (var (k, _) in js["prettify"]!.AsObject()) pr[k] = S(Rules.Prettify(k)); o["prettify"] = pr;
        var cf = new JsonObject(); foreach (var (k, _) in js["corpseFood"]!.AsObject()) cf[k] = N(R.CorpseFood(k)); o["corpseFood"] = cf;
        var ig = new JsonObject(); foreach (var (k, _) in js["isGate"]!.AsObject()) ig[k] = B(R.IsA(k, "CGate")); o["isGate"] = ig;
        var iw = new JsonObject(); foreach (var (k, _) in js["isWild"]!.AsObject()) iw[k] = B(R.IsWildClass(k)); o["isWild"] = iw;
        var hl = new JsonObject();
        foreach (var (k, _) in js["hasLevel"]!.AsObject()) { var a = new JsonArray(); for (int l = 1; l <= 5; l++) a.Add(B(R.HasLevel(k, l))); hl[k] = a; }
        o["hasLevel"] = hl;
        var byPath = new Dictionary<string, ActionDef>();
        foreach (var t in TT.TRIBES)
        {
            if (R.Base.Start["Objects"]?[t] == null) continue;
            foreach (var a in R.Actions(new FreshOwner(R.NewTree(t), t))) if (a.Kind == "Build") byPath[a.Path] = a;
        }
        var sl = new JsonObject();
        foreach (var (k, _) in js["startLevel"]!.AsObject()) sl[k] = byPath.TryGetValue(k, out var a) ? N(R.StartLevel(a)) : null;
        o["startLevel"] = sl;
        return o;
    }
}
