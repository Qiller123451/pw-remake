// Economy module of the computer player: the build list (CAiGoalBuildVillage), realising its requests (the engine's
// planner, replaced by one step of prerequisite resolution and a running resource reserve), workers
// (CAiModuleEconomyDefault), gathering (CAiGoalCollectResources), housing, storage, repair, hunting and fishing.
// docs/spec/ai.md §4. All functions are TribeAI methods (Brain.cs); S = the census.
//
// Ported from remake/src/game/ai/economy.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Ai;

/// <summary>a request resolved to an action: kind 'bldg' | 'unit' | 'upgrade', the action, the class it makes, the
/// upgrade an ObjFlag names</summary>
public sealed class Resolved
{
    public string Kind = "";
    public ActionDef Action = null!;
    public string? Cls;
    public ActionDef? Flag;
}

/// <summary>something a building request is placed at and counted at (an outpost, a trade route's security point)</summary>
public interface IAiSite { Pt Pos { get; } }

/// <summary>an entry of the build list in realisation (a list row with its resolved action), or one on top of it</summary>
public sealed class BuildEntry
{
    public string Request = "", ObjFlag = "";
    public int Count = 1;
    public bool Unique = true;
    public Resolved R = null!;
    public int Failed;
    public bool Done;
    public double T;
    public Pt? Pos;
    public IAiSite? Outpost;
    /// <summary>on top: housing, a tower of the defence</summary>
    public bool Housing, Tower;
    /// <summary>the mode it waits to pay for (an ObjFlag on an existing building)</summary>
    public ActionDef? FlagNow;
    /// <summary>why it failed last (statistics, the AI report): place | req | build:&lt;reason&gt; | producer</summary>
    public string? Why;
    /// <summary>the last answer of the realisation: started | cost | wait | fail (null: not looked at, e.g. at the unit limit)</summary>
    public string? Last;
    public override string ToString() => $"{Request}{(ObjFlag != "" ? "/" + ObjFlag : "")} x{Count}{(Last != null ? " " + Last + (Why != null ? ":" + Why : "") : "")}{(Failed > 0 ? $" failed {Failed}" : "")}";
}

public sealed partial class TribeAI
{
    static readonly string[] RES = { "food", "wood", "stone" };
    static readonly string[] COSTS = { "food", "wood", "stone", "skulls" };
    /// <summary>list entries that name objects the tech tree calls differently</summary>
    static readonly Dictionary<string, string> ALIAS = new() { ["hu_large_animal_farm"] = "hu_big_animal_farm" };
    /// <summary>BV:1166: still built at the unit limit</summary>
    static readonly Regex HOUSING = new("stone_cottage|_fireplace|_tent|headquarters|resource_collector", RegexOptions.Compiled);
    /// <summary>distance of a point from the segment a-b</summary>
    static double SegDist(double px, double pz, double ax, double az, double bx, double bz)
    {
        double dx = bx - ax, dz = bz - az, l = dx * dx + dz * dz;
        double t = l != 0 ? Math.Max(0, Math.Min(1, ((px - ax) * dx + (pz - az) * dz) / l)) : 0;
        return Hyp(px - ax - dx * t, pz - az - dz * t);
    }
    static double JsRound(double v) => Math.Floor(v + 0.5);

    // economy state
    public List<BuildEntry>? BuildList;
    public List<BuildEntry> Extra = new();
    int _buildN, _heroN, _heroSet;
    public bool UnitLimit;
    /// <summary>resources the requests in realisation wait for (the planner's lock)</summary>
    public Dictionary<string, double> Reserve = NewRes();
    /// <summary>the next worker's cost while the village has fewer than the epoch allows</summary>
    public Cost? WorkerCost;
    /// <summary>what the next unit of the army needs</summary>
    public Cost? UnitNeed;
    Dictionary<string, Resolved?>? _res;
    int _resV = -1;
    Dictionary<string, (ActionDef? Hit, int V, double T)>? _modes;
    double _skullT = -99, _huntT = -99, _workerHuntT = -99, _swapT = -9, _hintT = -99;
    Pt? _hint;
    readonly Dictionary<string, double> _noSrc = new(StringComparer.Ordinal);
    /// <summary>building class (@ position) -> until when no place is looked for again</summary>
    readonly Dictionary<string, double> _placeFail = new(StringComparer.Ordinal);
    /// <summary>statistics: building places not found (the AI report, tests)</summary>
    public int PlaceFails;
    public Unit? FoodPrey;
    /// <summary>skulls when the last skull hunt set out (-1: none yet)</summary>
    double _skullsAtHunt = -1;
    /// <summary>skull hunts in a row that brought nothing (looked at when the hunt is over)</summary>
    int _skullHuntFails;
    /// <summary>a new stone site / the defence wants a tower here</summary>
    public Pt? StoneSite, TowerAt;

    static Dictionary<string, double> NewRes() => new(StringComparer.Ordinal) { ["food"] = 0, ["wood"] = 0, ["stone"] = 0, ["skulls"] = 0 };
    double ResV(Dictionary<string, double> d, string r) => d.TryGetValue(r, out var v) ? v : 0;
    static double Limit(Limits l, string key) => key switch { "max_units" => l.MaxUnits, "max_food" => l.MaxFood, "max_wood" => l.MaxWood, "max_stone" => l.MaxStone, _ => 0 };
    static string? ObjOf(ActionDef x) => x.Kind == "Build" && x.Results.Count > 0 ? x.Results[0].Obj : null;

    // ------------------------------------------------------------------ requests -> actions
    /// <summary>"BLDG/hu_arena" | "CHTR/Cole_s0" | "age_2" | "hu_stone_cottage" (a partial name, GetNodeInstanceFromPartialName)</summary>
    public Resolved? Resolve(string name, string? objFlag)
    {
        var p = P; int v = p.Tt?.Version ?? 0;
        if (_res == null || _resV != v) { _res = new Dictionary<string, Resolved?>(StringComparer.Ordinal); _resV = v; }
        string key = name + "|" + (objFlag ?? "");
        if (_res.TryGetValue(key, out var hit)) return hit;
        var acts = D.Actions(p);
        string? typ = name.Contains('/') ? name.Split('/')[0] : null;
        string tail = name.Split('/')[^1];
        // (an object's own action first: Rules.OwnActionOf - MIRAGE's hidden farm-mode Build actions also make aje_small_farm)
        ActionDef? ByObj(string t) => acts.FirstOrDefault(x => ObjOf(x) == t && x.Id == t) ?? acts.FirstOrDefault(x => ObjOf(x) == t && x.Visible) ?? acts.FirstOrDefault(x => ObjOf(x) == t);
        ActionDef? Find(string t) => (typ != null ? ByObj(t) : null) ?? acts.FirstOrDefault(x => x.Kind != "Moves" && x.Id == t)
            ?? ByObj(t) ?? (typ == null ? acts.FirstOrDefault(x => ObjOf(x) is { } o && o.EndsWith(t, StringComparison.Ordinal)) : null);
        var a = Find(tail);
        if (a == null && ALIAS.TryGetValue(tail, out var al)) a = Find(tail = al);
        Resolved? outp = null;
        if (a != null)
        {
            var cls = ObjOf(a);
            var flag = !string.IsNullOrEmpty(objFlag) && objFlag != cls ? acts.FirstOrDefault(x => x.Kind == "Upgrades" && x.Id == objFlag) : null;
            outp = new Resolved { Kind = a.Kind == "Upgrades" ? "upgrade" : a.Cat == "Build/BLDG" ? "bldg" : "unit", Action = a, Cls = cls, Flag = flag };
        }
        _res[key] = outp;
        return outp;
    }
    /// <summary>own entities that offer an action right now</summary>
    public List<Entity> ProducersOf(Census S, ActionDef a)
    {
        var at = new HashSet<string>(a.Locs.Select(l => l.At), StringComparer.Ordinal);
        var outp = new List<Entity>();
        foreach (var b in S.Blds) if (b.Built && b.Dismantling == null && at.Contains(b.Name)) outp.Add(b);
        foreach (var u in S.Units) if ((at.Contains(u.Name) || (at.Contains(u.Cls) && u.IsWorker)) && !u.Inside) outp.Add(u);
        return outp.Where(e => Acts(e).Any(x => x.Path == a.Path)).ToList();
    }
    /// <summary>the action as an entity offers it: a building with a mode of its own (local tree) sees other values</summary>
    public ActionDef LocalAction(Entity e, ActionDef a) => Acts(e).FirstOrDefault(x => x.Path == a.Path) ?? a;
    static string FilterPath(string? path) => Regex.Replace(path ?? "", @"^/?Filters/", "");
    /// <summary>does a building carry the upgrade an ObjFlag names (Aje farm modes, the big tent)?</summary>
    bool HasFlag(Entity e, ActionDef? f)
    {
        if (f == null) return true;
        if (e.AiFlag == f.Id || e.Queue.Any(q => q.Action.Id == f.Id)) return true;
        var local = f.Results.Where(r => r.Cls == "local").ToList();
        if (local.Count == 0) return P.Techs.Has(f.Id);
        return e.LocalTT != null && local.All(r => e.LocalTT.Has(FilterPath(r.Path)));
    }
    /// <summary>how many more of a list entry are wanted (0 = satisfied)</summary>
    public int Missing(Census S, BuildEntry e, Resolved R)
    {
        var p = P; var a = R.Action;
        if (R.Kind == "upgrade") return p.Techs.Has(a.Id) || W.AnyQueued(p, a.Id) ? 0 : 1;
        // MIRAGE's building limits: no more than the limit leaves room for (a farm mode: the room of its type) - an entry
        // at its limit is done, it does not hold one of the five places of the requests
        int room = R.Kind == "bldg" ? W.LimitRoom(p, R.Flag ?? a) : int.MaxValue;
        if (room <= 0) return 0;
        if (!e.Unique) return e.Done ? 0 : 1;
        int have = 0;
        if (R.Kind == "bldg")
        {
            // with an ObjFlag only the buildings that carry it count (and the ones still being built); an outpost's
            // request: only those at the outpost (BuildIfNotExist per outpost)
            var o = e.Outpost;
            foreach (var b in S.Blds) if (b.Name == R.Cls && (!b.Built || HasFlag(b, R.Flag)) && (o == null || DistP(b, o.Pos) <= 60)) have++;
        }
        else
        {
            have = S.CountOf(R.Cls ?? "");
            foreach (var x in S.Blds) foreach (var q in x.Queue) if (q.Action.Path == a.Path) have++;
            foreach (var x in S.Units) foreach (var q in x.Queue) if (q.Action.Path == a.Path) have++;
        }
        return Math.Max(0, Math.Min(room, e.Count - have));
    }
    /// <summary>can the player put up (or upgrade to) this now: the tech tree's check, then MIRAGE's building limit
    /// (World.LimitWhy: "limit") - the original's AI tried and the server refused; here the planner does not ask</summary>
    public string? BuildCheck(Player p, ActionDef a) => D.Check(p, a) ?? W.LimitWhy(p, a);
    public bool CanPay(Cost cost, Dictionary<string, double> avail) => P.Debug || COSTS.All(r => cost[r] <= 0 || ResV(avail, r) >= cost[r]);
    void Spend(Cost cost, Dictionary<string, double> avail) { foreach (var r in COSTS) avail[r] = ResV(avail, r) - cost[r]; }
    Dictionary<string, double> ResCopy() => new(P.Res, StringComparer.Ordinal);

    // ------------------------------------------------------------------ the build list (BV:417-1232)
    void BuildThink(Census S)
    {
        _buildN++;
        if (BuildList != null && _buildN % 6 != 0 && Requests.Count > 0) return;     // BV:1210: every 6th think
        UpdateBuildList(S);
    }
    bool AtUnitLimit(Census S)
    {
        var p = P;
        double cap = p.PopMax + (p.AiMods != null ? p.AiMods.UnitLimit : 0);
        return p.Units + p.QueuedUnits >= p.MaxUnits && p.MaxUnits < cap;
    }
    void UpdateBuildList(Census S)
    {
        var ctx = Ctx();
        var list = new List<BuildEntry>();
        // CheckNumFightingUnits (BV:432, 1388): from the third epoch nothing is built with fewer than ten characters,
        // animals and vehicles that are not workers (carts and collectors count)
        bool hold = Multimap && S.Age >= 3 && S.Units.Count - S.Workers.Count - S.Fleet.Count < 10;
        UnitLimit = AtUnitLimit(S);
        if (!hold || !Data.HasAiData)
        {
            foreach (var r in Data.BuildList(ctx))
            {
                var m = Regex.Match(r.Request, @"^age_(\d)$");
                if (m.Success && double.Parse(r.Request[4..], System.Globalization.CultureInfo.InvariantCulture) > (Params.MaxAge != 0 ? Params.MaxAge : 5)) continue;
                list.Add(Entry(r));
            }
            // heroes: every 5th update, from epoch 3, not on Easy (UpdateSpecialBuildList)
            if (Multimap && Kindergarten && Cls != "Easy" && S.Age >= 3)
            {
                _heroN++;
                if (_heroN >= 5 || _heroSet == 0) { _heroN = 0; _heroSet = 1 + (int)Math.Floor(R.Next() * 3); }
                foreach (var h in Data.HeroRequests(_heroSet)) if (!S.Heroes.Any(u => u.Name == h.Request.Split('/')[^1])) list.Add(Entry(h));
            }
            // one more warship whenever an enemy has more (CheckForEnemyShips), on water maps
            if (Multimap && ctx.Watermap)
            {
                static bool War(Unit u) => u.Alive && u.Naval && !u.CannotFight && !u.Stationary;
                int mine = S.Fleet.Count(War);
                int most = 0;
                foreach (var o in Enemies()) most = Math.Max(most, W.Units.Count(u => u.Owner == o && War(u)));
                if (mine < most) foreach (var r in Data.CounterShips(ctx)) { var e = Entry(r); e.Count = mine + 1; list.Add(e); }
            }
        }
        // resolvable, unsatisfied entries in script order
        var outp = new List<BuildEntry>();
        foreach (var e in list)
        {
            var R0 = Resolve(e.Request, e.ObjFlag);
            if (R0 == null) continue;
            if (R0.Kind == "unit" && D.Info(R0.Cls) is { Type: "SHIP" } && W.WaterNav == null) continue;
            if (R0.Kind == "bldg" && D.Def(R0.Cls!, P) is { Coastal: true } && W.WaterNav == null) continue;
            if (R0.Kind == "bldg" && PwRemake.Sim.World.World.IsWarpGateScript(D.Script(R0.Cls)) && !W.Opts.Warpgate) continue;
            e.R = R0; e.Failed = 0;
            outp.Add(e);
        }
        BuildList = outp;
        // a resource at its storage cap: more storage (BV:1455, before the list's requests: the JS asked only when a request
        // cost more than the cap - an Aje village stood with its stores full and its workers idle)
        if (!UnitLimit) StorageFor(S, RES.FirstOrDefault(r => Cap(r) > 0 && P[r] >= Cap(r) - 20));
        PickRequests(S);
    }
    static BuildEntry Entry(AiRow r) => new() { Request = r.Request, ObjFlag = r.ObjFlag, Count = r.Count, Unique = r.Unique };
    /// <summary>the first five unsatisfied entries are in realisation (RequestNext(5)); failed ones wait for the next list</summary>
    void PickRequests(Census S)
    {
        Extra = Extra.Where(e => Missing(S, e, e.R) > 0 && W.Time - e.T < 180).ToList();
        var outp = new List<BuildEntry>();
        foreach (var e in BuildList ?? new List<BuildEntry>())
        {
            if (outp.Count >= 5) break;
            if (e.Failed > 2 || Missing(S, e, e.R) <= 0) continue;
            outp.Add(e);
        }
        Requests = outp;
    }
    /// <summary>a request on top of the list: housing, storage, prerequisites (pos / outpost: a building of an outpost -
    /// placed there, counted there: Outposts.cs)</summary>
    public bool AddOnTop(string request, string objFlag = "", int count = 1, bool unique = true, Pt? pos = null, IAiSite? outpost = null)
    {
        var R0 = Resolve(request, objFlag);
        if (R0 == null) return false;
        if (Extra.Any(e => e.R.Action.Path == R0.Action.Path && e.Outpost == outpost)) return true;
        if (Extra.Count >= 4) return false;
        Extra.Add(new BuildEntry { Request = request, ObjFlag = objFlag, Count = count != 0 ? count : 1, Unique = unique, R = R0, Failed = 0, T = W.Time, Done = false, Pos = pos, Outpost = outpost });
        return true;
    }

    // ------------------------------------------------------------------ realising (EM:523-652)
    void EconomyThink(Census S)
    {
        var p = P;
        var PF = W.Profile; long t0 = PF != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        void T(string k) { if (PF == null) return; long t1 = System.Diagnostics.Stopwatch.GetTimestamp(); PF.Add(k, t1 - t0); t0 = t1; }
        var avail = ResCopy();
        var reserve = NewRes();
        KeepBuilders(S); T("eco.keep");
        WorkerThink(S, avail); T("eco.workers");
        HousingThink(S); T("eco.housing");
        PickRequests(S); T("eco.pick");
        int pending = S.Blds.Count(b => !b.Built && b.Def!.WallKind != "wall" && b.Def.WallKind != "gate");
        int started = 0;
        var all = Extra.Concat(Requests).ToList();
        foreach (var e in all)
        {
            var R0 = e.R;
            // at the unit limit only housing and research go on (BV:1165)
            if (UnitLimit && !(R0.Kind == "upgrade" || e.Housing || HOUSING.IsMatch(R0.Cls ?? ""))) continue;
            e.Why = null;
            var why = Realise(S, e, avail, pending + started);
            e.Last = why;
            if (why == "started") { started++; if (!e.Unique) e.Done = true; Spend(R0.Action.Cost, avail); continue; }
            if (why == "cost")
            {
                // the planner locks the resources of a request it cannot pay yet: later requests and the army leave them.
                // A request that waits for skulls (a hero, an Aje epoch) locks only those: skulls come from fighting, and
                // food that nobody may touch until then would stop the army that is to earn them.
                var c = (e.FlagNow ?? R0.Action).Cost;
                bool skulls = c.Skulls > p["skulls"];
                reserve["skulls"] += c.Skulls;
                if (!skulls) foreach (var r in RES) { double need = Math.Max(0, c[r]); reserve[r] += need; avail[r] = Math.Max(0, ResV(avail, r) - need); }
                StorageThink(S, c);
            }
            else if (why == "fail") e.Failed++;
            e.FlagNow = null;
        }
        // a request on top that cannot be done (a building the mission hides - the Aje temple of mission 1 - or no place for
        // it) leaves its slot after three tries: the JS kept it for its three minutes, retried on every think
        Extra.RemoveAll(e => e.Failed > 2);
        T("eco.requests");
        Reserve = reserve;
        RepairThink(S); T("eco.repair");
        if (W.WaterNav != null) FishingThink(S); T("eco.fishing");
        FoodThink(S); T("eco.food");
        SkullThink(S); T("eco.skulls");
    }
    /// <summary>-> 'started' | 'cost' | 'wait' | 'fail'</summary>
    string Realise(Census S, BuildEntry e, Dictionary<string, double> avail, int building)
    {
        var p = P; var R0 = e.R; var a = R0.Action;
        if (R0.Kind == "bldg")
        {
            // an ObjFlag on an existing building: research its mode (Aje farm modes, the big tent)
            if (R0.Flag != null)
            {
                // a building of the class without a mode of its own yet takes this one (a mode replaces the other modes)
                var free = S.Blds.Where(x => x.Name == R0.Cls && x.AiFlag == null && !HasFlag(x, R0.Flag)).ToList();
                var b0 = free.FirstOrDefault(x => x.Built && x.Queue.Count == 0);
                if (b0 != null)
                {
                    var why0 = W.CanQueue(p, R0.Flag, b0);
                    if (why0 == null && CanPay(R0.Flag.Cost, avail)) { Hands.QueueAction(p, b0, R0.Flag); b0.AiFlag = R0.Flag.Id; Spend(R0.Flag.Cost, avail); return "wait"; }
                    if (why0 == null || why0 == "cost") { e.FlagNow = R0.Flag; return "cost"; }
                    if (why0 == "done") { b0.AiFlag = R0.Flag.Id; return "wait"; }
                    return "wait";
                }
                if (free.Count > 0) return "wait";
            }
            if (S.Blds.Any(x => x.Name == R0.Cls && !x.Built)) { e.Why = "site"; return "wait"; }      // one of a kind at a time
            if (building >= 3) { e.Why = "sites"; return "wait"; }
            var why = D.Check(p, a);
            if (why != null) { e.Why = "req " + why; return Prerequisites(S, a, null) ? "wait" : "fail"; }
            if (W.LimitWhy(p, a) is { } lim) { e.Why = lim; return "fail"; }      // (MIRAGE's building limits)
            if (!CanPay(a.Cost, avail)) return "cost";
            // (a class no place was found for is not looked for again for 30 s: the search out to 130 m is not cheap)
            string pk = R0.Cls + (e.Pos is { } ep ? $"@{ep.X:0},{ep.Z:0}" : "");
            if (_placeFail.TryGetValue(pk, out var pf) && W.Time < pf) { e.Why = "place"; return "fail"; }
            long fs0 = W.Profile != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var spot = FindSpot(S, R0.Cls!, a, e.Pos);
            W.Profile?.Add("eco.findspot", System.Diagnostics.Stopwatch.GetTimestamp() - fs0);
            if (spot == null) { _placeFail[pk] = W.Time + 30; PlaceFails++; e.Why = "place"; return "fail"; }
            var ws = new List<Unit>();
            int n = S.Workers.Count >= 9 ? 3 : S.Workers.Count >= 5 ? 2 : 1;
            if (e.Outpost != null) ws = OutpostWorkers(e.Outpost).Take(n).ToList();          // its crew builds it
            if (ws.Count == 0) for (int i = 0; i < n; i++) { var w = PickWorker(S, new Pt(spot.Value.X, spot.Value.Z), ws); if (w != null) ws.Add(w); }
            if (ws.Count == 0) { e.Why = "workers"; return "wait"; }
            var res = Hands.StartConstruction(p, a, spot.Value.X, spot.Value.Z, spot.Value.Rot, ws);
            if (res is string s) { e.Why = "build " + s; return s == "cost" ? "cost" : "fail"; }
            S.Blds.Add((Building)res);
            return "started";
        }
        var prods = ProducersOf(S, a);
        if (prods.Count == 0)
        {
            // nobody offers it yet: wait for the building it is made at, or ask for that building (one step of the planner)
            if (S.Blds.Any(x => !x.Built && a.Locs.Any(l => l.At == x.Name))) return "wait";
            var at = a.Locs.Select(l => l.At).FirstOrDefault(nm => D.Info(nm) is { Type: "BLDG" });
            e.Why = "producer " + (at ?? "none");
            return at != null && AddOnTop("BLDG/" + at) ? "wait" : "fail";
        }
        bool cost = false; Entity? req = null;
        foreach (var pr in prods.OrderBy(x => x.Queue.Count).ToList())
        {
            if (pr.Queue.Count >= (R0.Kind == "unit" ? 2 : 1)) continue;
            var la = LocalAction(pr, a);
            var why = W.CanQueue(p, la, pr);
            if (why == null)
            {
                if (!CanPay(la.Cost, avail)) { cost = true; continue; }
                if (Hands.QueueAction(p, pr, la) == null) return "started";
            }
            else if (why == "cost") cost = true;
            else if (why == "req") req = pr;
            else if (why == "done" || why == "unique" || why == "housing" || why == "level") { e.Why = why; return "wait"; }
            else e.Why = why;
        }
        if (cost) return "cost";
        if (req != null) { e.Why = "req at producer"; return Prerequisites(S, a, req) ? "wait" : "fail"; }
        return "wait";
    }
    /// <summary>what an action still needs: research it / build it first. -> false when nothing can be done about it</summary>
    bool Prerequisites(Census S, ActionDef a, Entity? producer)
    {
        var p = P;
        IRulesOwner? owner = producer != null ? producer.RulesOwner() : p;
        bool any = false;
        // not offered in the building's present mode (a plain Aje farm offers no animal): choose the mode that offers it
        if (producer is Building pb && Regex.IsMatch(D.Check(owner, LocalAction(producer, a), producer) ?? "", "hidden|disabled"))
        {
            var m = ModeFor(pb, a);
            if (m == null) return false;
            if (pb.AiFlag != null && pb.AiFlag != m.Id)
            {
                // this one has another mode: another building of the class takes it, or one more is built for it
                var S2 = this.S ?? S;
                var other = S2.Blds.FirstOrDefault(x => x.Name == pb.Name && (x.AiFlag == null || x.AiFlag == m.Id) && x != pb);
                if (other != null) return other.Built && other.AiFlag == null ? Prerequisites(S, a, other) : true;
                return S2.Blds.Count(x => x.Name == pb.Name) < 4 && AddOnTop("BLDG/" + pb.Name, "", unique: false);
            }
            if (pb.Queue.Count > 0) return true;
            var why = W.CanQueue(p, m, pb);
            if (why == null) { Hands.QueueAction(p, pb, m); pb.AiFlag = m.Id; return true; }
            return why == "cost";
        }
        foreach (var m in D.Missing(owner, a, producer))
        {
            if (Regex.IsMatch(m, @"^age_\d$")) { any = true; continue; }        // the epoch comes from the list
            if (Regex.IsMatch(m, @"_s\d$")) continue;                         // a hero at level 5: nothing to request
            if (producer != null)
            {
                // a mode of the producing building itself (Aje farm: small / medium / huge)
                var lo = Acts(producer).FirstOrDefault(x => x.Kind == "Upgrades" && x.Id == m);
                if (lo != null && lo.Results.Any(r => r.Cls == "local"))
                {
                    if (producer.AiFlag != null && producer.AiFlag != m) continue;        // it has another mode: the list brings more farms
                    if (producer.Queue.Count > 0) { any = true; continue; }
                    var why = W.CanQueue(p, lo, producer);
                    if (why == null) { Hands.QueueAction(p, producer, lo); producer.AiFlag = m; any = true; continue; }
                    if (why == "cost") { any = true; continue; }
                    continue;
                }
            }
            if (D.Info(m) is { Type: "BLDG" }) { if (AddOnTop("BLDG/" + m)) any = true; continue; }
            if (Resolve(m, "") != null && AddOnTop(m)) any = true;
        }
        return any;
    }

    /// <summary>the local upgrade ("mode") of a building class that makes it offer an action: tried on a copy of the
    /// player's tree, once per class and action</summary>
    ActionDef? ModeFor(Building b, ActionDef a)
    {
        var p = P;
        // (which mode offers an action does not change during a game: a hit is kept for good; a miss is tried again
        // when the tree has changed, at most every 30 s - trying costs a copy of the tree per mode)
        string key = b.Name + "|" + a.Path; int v = p.Tt?.Version ?? 0;
        _modes ??= new Dictionary<string, (ActionDef?, int, double)>(StringComparer.Ordinal);
        if (_modes.TryGetValue(key, out var c) && (c.Hit != null || c.V == v || W.Time - c.T < 30)) return c.Hit;
        ActionDef? hit = null;
        if (p.Tt != null) foreach (var m in Acts(b))
        {
            if (m.Kind != "Upgrades" || !m.Results.Any(r => r.Cls == "local" && r.Type != "deactivate")) continue;
            var T = new LocalTree(p.Tt);
            foreach (var r in m.Results) if (r.Cls == "local" && r.Type != "deactivate") T.Enable(FilterPath(r.Path));
            var a2 = D.Actions(new LocalOwner(p, T)).FirstOrDefault(x => x.Path == a.Path);
            if (a2 != null && a2.Visible && !a2.Disabled) { hit = m; break; }
        }
        _modes[key] = (hit, v, W.Time);
        return hit;
    }
    /// <summary>skulls a waiting request needs (the Aje pay for epochs with them): a few fighters hunt wild animals (the
    /// fight module's "skulls" attack, CAiTaskGetScalps)</summary>
    void SkullThink(Census S)
    {
        var p = P;
        // not only the request that waits for resources: every request in realisation (the Aje's second epoch costs
        // skulls, and the village has none until somebody hunts)
        double need = ResV(Reserve, "skulls");
        foreach (var e in Requests) need = Math.Max(need, e.R.Action.Cost.Skulls);
        if (need <= p["skulls"] || W.Time - _skullT < 12) return;
        _skullT = W.Time;
        // a squad, not single hunters (GetScalps: the army modifier is 3 before epoch 3, FM:412); the nearest harmless
        // herd first (the task looks through its "friendly animal" map before the others)
        // farther out when nothing is left within 220 m (fixed 2026-11-11: the Aje's second epoch costs 10 skulls; an Aje
        // village whose surroundings were hunted empty - co-op allies in missions 1 and 4, who never fight on their own -
        // stood at 8 skulls in epoch 1 for good, its workers idle at the full stores. The scripts' GetScalps searched the
        // AI maps, which cover the whole map)
        double maxHp = S.Age < 3 ? 400 : 1000;
        bool running = Attacks.Any(a => a.Type == "skulls");
        // the last skull hunt brought nothing (fixed 2026-11-12: its prey ran away - the Megaloceros of mission 1 are
        // faster than the hunters; the Aje ally's hunts timed out one after the other and it stood at 8 of the 10 skulls
        // of its second epoch from minute 5 on): animals that fight back, as big as the pool can take on (a woolly rhino
        // takes six hunters)
        if (!running && _skullsAtHunt >= 0) { _skullHuntFails = p["skulls"] <= _skullsAtHunt ? _skullHuntFails + 1 : 0; _skullsAtHunt = -1; }
        bool failed = _skullHuntFails > 0;
        var prey = new List<Unit>();
        if (failed) prey = Prey(S, 450, maxHp * 4, true).Where(u => u.Def!.Aggressive >= 0 && Hunters(u) + 3 <= S.Pool.Count && !W.IsWater(u.Pos.X, u.Pos.Z, 0.5)).ToList();
        if (prey.Count == 0) prey = Prey(S, 220, maxHp, true);
        // (farther out only with a guard of three left at home: MIRAGE 9's Aje ally lost its collector to four Barbarian
        // warriors while its fighters hunted 400 m away - CoopPhaseTests.CoopHandOverAiBases)
        bool far = S.Pool.Count >= (S.Age < 3 ? 3 : 2) + 3;
        if (prey.Count == 0 && far) prey = Prey(S, 450, maxHp, true);
        if (prey.Count == 0 && far) prey = Prey(S, 1000, maxHp, true);
        // (then bigger animals - a woolly rhino takes six hunters, Hunters(); a village's deer were gone in minutes)
        if (prey.Count == 0 && S.Pool.Count >= 8) prey = Prey(S, 1000, maxHp * 4, true);
        prey = Walkable(S, prey);
        // a co-op AI commander with no prey it can walk to, or whose hunts keep failing: a skull every think (12 s) up to
        // what it needs (the remake's own, 2026-11-12 - as the cheat manager's food: the Aje ally of MIRAGE mission 1 has
        // the mission's walls between its village and every herd but one and stood in its first epoch for good; the
        // original has no Dustrider ally in these missions)
        if (CoopAlly && !running && (prey.Count == 0 || _skullHuntFails >= 2)) p.Res["skulls"] = p["skulls"] + 1;
        if (prey.Count == 0) return;
        int n = Math.Max(S.Age < 3 ? 3 : 2, Hunters(prey[0]));
        double before = p["skulls"];
        Hunt(S, "skulls", prey.Take(prey[0].Def!.Aggressive < 0 ? 3 : 4).ToList(), n, n + 2);
        if (!running && Attacks.Any(a => a.Type == "skulls")) _skullsAtHunt = before;          // (one set out now)
    }

    // ------------------------------------------------------------------ workers (EM:626-651, DMo:118)
    void WorkerThink(Census S, Dictionary<string, double> avail)
    {
        var p = P;
        WorkerCost = null;
        if (DefendMode) return;
        int cap = Data.MaxWorkers(Cls, S.Age);
        bool IsWorkerAct(ActionDef a) => a.Kind == "Build" && a.Results.Count > 0 && D.Def(a.Results[0].Obj, p) is { } df && df.CanHarvest != 0 && !a.Cat.Contains("SHIP");
        Entity? bse = S.Base;
        var wa = bse != null ? Acts(bse).FirstOrDefault(IsWorkerAct) : null;
        if (wa == null)
        {
            // the main building is gone: any other place that trains workers, else that building is asked for first
            var any = D.Actions(p).FirstOrDefault(IsWorkerAct);
            if (any == null) return;
            bse = ProducersOf(S, any).FirstOrDefault();
            if (bse == null)
            {
                // (the Aje's workers come from their collector animal, which a small farm makes)
                var at = any.Locs.Select(l => l.At).FirstOrDefault(nm => D.Info(nm) != null && Resolve(nm, "") != null);
                if (at != null && !S.Blds.Any(b => b.Name == at)) AddOnTop((D.Info(at)!.Type == "BLDG" ? "BLDG/" : "") + at);
                return;
            }
            wa = LocalAction(bse, any);
        }
        int queued = bse!.Queue.Count(q => q.Action.Path == wa.Path);
        // the army leaves this much for the next worker while the village has fewer than the epoch allows
        if (S.Workers.Count + queued < cap) WorkerCost = wa.Cost;
        // one more whenever every worker is busy (AllWorkersLocked), up to the cap of the epoch
        if (S.Workers.Count + queued >= cap || queued >= 1 || S.Idle.Count > 1) return;
        if (W.CanQueue(p, wa, bse) == null && CanPay(wa.Cost, avail)) { Hands.QueueAction(p, bse, wa); Spend(wa.Cost, avail); }
    }
    /// <summary>the cheapest building that raises the population limit, on top of the list (CheckForUnitLimit, BV:1424)</summary>
    void HousingThink(Census S)
    {
        var p = P;
        UnitLimit = AtUnitLimit(S);
        double room = p.MaxUnits - p.Units - p.QueuedUnits;
        double cap = p.PopMax + (p.AiMods != null ? p.AiMods.UnitLimit : 0);
        if (room >= 2 || p.MaxUnits >= cap) return;
        if (S.Blds.Any(b => !b.Built && b.Stats != null && b.Stats.Limits.MaxUnits > 0)) return;
        if (Extra.Any(e => e.Housing)) return;
        void Mark() { if (Extra.Count > 0) Extra[^1].Housing = true; }
        foreach (var h in Data.Housing(p.Tribe))
        {
            var R0 = Resolve(h.Request, h.ObjFlag);
            if (R0 == null || R0.Kind != "bldg" || BuildCheck(p, R0.Action) != null) continue;
            if (AddOnTop(h.Request, "", unique: false)) { Mark(); return; }
        }
        var worker = S.Workers.FirstOrDefault();
        if (worker == null) return;
        var houses = Acts(worker).Where(a =>
        {
            if (a.Cat != "Build/BLDG") return false;
            var s = D.Stats(a.Results[0].Obj, 1, p);
            return s != null && s.Limits.MaxUnits > 0 && BuildCheck(p, a) == null;
        }).OrderBy(a => a.Cost.Wood + a.Cost.Stone + a.Cost.Food).ToList();
        if (houses.Count > 0 && AddOnTop("BLDG/" + houses[0].Results[0].Obj, "", unique: false)) Mark();
    }
    /// <summary>a request that costs more than the storage holds: more storage first (RequestMoreResourceBuilding, BV:1455)</summary>
    void StorageThink(Census S, Cost cost) => StorageFor(S, RES.FirstOrDefault(x => cost[x] > Cap(x)));
    /// <summary>more storage for a resource (RequestMoreResourceBuilding, BV:1455-1541: also whenever a resource stands at
    /// its storage cap, on every 6th build think - UpdateBuildList)</summary>
    void StorageFor(Census S, string? r)
    {
        var p = P;
        if (r == null || S.Blds.Any(b => !b.Built && b.Stats != null && Limit(b.Stats.Limits, "max_" + r) > 0)) return;
        string key = "max_" + r;
        ActionDef? best = null; double bc = 1e9;
        foreach (var a in D.Actions(p))
        {
            if (a.Kind != "Build" || a.Results.Count == 0 || D.Check(p, a) == "req" || W.LimitWhy(p, a) != null) continue;
            var s = D.Stats(a.Results[0].Obj, 1, p);
            if (s == null || !(Limit(s.Limits, key) > 0)) continue;
            double c = a.Cost.Food + a.Cost.Wood + a.Cost.Stone + a.Cost.Skulls * 3;
            if (RES.Any(x => a.Cost[x] > Cap(x))) continue;
            if (c < bc) { bc = c; best = a; }
        }
        if (best != null) AddOnTop((best.Cat == "Build/BLDG" ? "BLDG/" : "ANML/") + best.Results[0].Obj, "", unique: false);
    }

    // ------------------------------------------------------------------ builders, repair
    public Unit? PickWorker(Census S, Pt pos, List<Unit>? exclude = null)
    {
        Unit? best = null; double bd = 1e9;
        foreach (var w in S.Workers)
        {
            var t = w.Task.Type;
            if ((exclude != null && exclude.Contains(w)) || w.Inside || t == "build" || t == "attack" || t == "entrench" || t == "board") continue;
            double d = DistP(w, pos) + (t == "idle" ? -30 : 0) + (w.Carry != null && w.Carry.Amount > 0 ? 25 : 0);
            if (d < bd) { bd = d; best = w; }
        }
        return best;
    }
    void KeepBuilders(Census S)
    {
        foreach (var b in S.Blds)
        {
            if (b.Built || b.Def!.WallKind == "wall" || b.Def.WallKind == "gate") continue;        // (the village wall: Walls.cs)
            var active = b.Builders.Where(u => u.Alive && u.Task.Type == "build" && u.Task.Building == b).ToList();
            if (active.Count < (S.Workers.Count >= 8 ? 2 : 1)) { var w = PickWorker(S, Pt.Of(b), active); if (w != null) Hands.Order(P, new[] { w }, new Order("build") { Target = b, Auto = true }); }
        }
    }
    void RepairThink(Census S)
    {
        if (Alarm != null) return;
        foreach (var b in S.Blds)
        {
            if (!b.Built || b.Hp >= b.MaxHp * 0.6 || b.Builders.Count > 0 || W.Time - W.LastHitOf(b) < 10) continue;
            var w = PickWorker(S, Pt.Of(b));
            if (w != null) Hands.Order(P, new[] { w }, new Order("repair") { Target = b, Auto = true });
            break;
        }
    }

    // ------------------------------------------------------------------ gathering (CR:408-453)
    void CollectThink(Census S)
    {
        var p = P;
        if (Params.NoCollect) return;
        var cnt = new Dictionary<string, double>(StringComparer.Ordinal) { ["food"] = 0, ["wood"] = 0, ["stone"] = 0 };
        foreach (var w in S.Workers)
        {
            if (w.Task.Type != "gather") continue;
            var res = w.Task.Res ?? "undefined";
            cnt[res] = ResV(cnt, res) + 1;
            // waiting at a full storehouse: back to the idle workers
            if (w.Task.FullT > 2) { cnt[res]--; W.ReleaseTask(w); w.Task = UnitTask.Idle(); w.Path.Clear(); S.Idle.Add(w); }
        }
        // what the pending requests and the next unit need (the planner's resource needs)
        var need = new Dictionary<string, double>(StringComparer.Ordinal) { ["food"] = 60, ["wood"] = 40, ["stone"] = 30 };
        foreach (var r in RES) need[r] += ResV(Reserve, r) + (UnitNeed != null ? UnitNeed[r] : 0);
        foreach (var e in Requests) foreach (var r in RES) need[r] += e.R.Action.Cost[r] * 0.5;
        if (S.Idle.Count == 0) { Rebalance(S, cnt, need); return; }
        foreach (var w in S.Idle.ToList())
        {
            if (w.Inside || Locked.Has(w)) continue;
            // idle workers take what is short; equal needs in the script's order stone, wood, food
            string? best = null; double bs = -1;
            foreach (var r in new[] { "stone", "wood", "food" })
            {
                if (p[r] >= Cap(r) - 20) continue;
                double shortage = Math.Max(0, need[r] - p[r]);
                double s = (1 + shortage / 40) / (cnt[r] + 1);
                if (s > bs + 1e-9) { bs = s; best = r; }
            }
            if (best == null) continue;
            var order = new List<string> { best };
            order.AddRange(new[] { "stone", "wood", "food" }.Where(r => r != best && p[r] < Cap(r) - 20));
            foreach (var r in order) { if (Assign(S, w, r)) { cnt[r]++; break; } if (r == "food" && r == best) MoreFarms(S); }
        }
    }
    // The original's workers come back to the idle list whenever their tree, rock or bush is used up, and are then
    // sent to what is short (CR:408). The remake's workers go on to the next tree by themselves, so the brain moves
    // one over (every few seconds) when a resource is short while another has more gatherers than its share.
    void Rebalance(Census S, Dictionary<string, double> cnt, Dictionary<string, double> need)
    {
        var p = P; double now = W.Time;
        if (now - _swapT < 3) return;
        var w8 = new Dictionary<string, double>(StringComparer.Ordinal);
        double sum = 0, n = 0;
        foreach (var r in RES)
        {
            bool dead = p[r] >= Cap(r) - 20 || now - (_noSrc.TryGetValue(r, out var ns) ? ns : -99) < 20;
            w8[r] = dead ? 0 : 1 + Math.Min(7, Math.Max(0, need[r] - p[r]) / 100);
            sum += w8[r]; n += ResV(cnt, r);
        }
        if (sum == 0 || n < 2) return;
        string? to = null, from = null; double lack = 0.99, over = 0.49;
        foreach (var r in RES)
        {
            double want = n * w8[r] / sum, d = want - ResV(cnt, r);
            if (d > lack) { lack = d; to = r; }
            if (-d > over && ResV(cnt, r) > 0) { over = -d; from = r; }
        }
        if (to == null || from == null) return;
        Unit? best = null;
        foreach (var w in S.Workers) if (w.Task.Type == "gather" && w.Task.Res == from && !Locked.Has(w) && (best == null || (w.Carry?.Amount ?? 0) < (best.Carry?.Amount ?? 0))) best = w;
        if (best == null) return;
        _swapT = now;
        if (!Assign(S, best, to)) { _noSrc[to] = now; if (to == "food") MoreFarms(S); }
    }
    bool Assign(Census S, Unit w, string res)
    {
        var h = S.Home!.Value;
        // fields / slaughterhouses first (they never run out), then the nearest node
        if (res == "food" || res == "wood")
        {
            var farm = S.Blds.FirstOrDefault(b => b.Built && W.IsFarm(b) && b.Def!.Unlimited.Contains(res) && b.Workers.Count < W.FarmSlots(b));
            if (farm != null) { Hands.Order(P, new[] { w }, new Order("gather") { Target = farm, Auto = true }); return true; }
        }
        bool Ok(ResNode n) => RegionValue(res == "wood" ? "WOOD" : res == "stone" ? "STON" : "FOOD", n.Pos.X, n.Pos.Z) >= 0 && !DangerAt(n.Pos.X, n.Pos.Z);
        // food: the carcass of the animal the hunters just killed, wherever it lies
        var kill = res == "food" && FoodPrey != null && !FoodPrey.Alive ? FoodPrey.CorpseNode : null;
        var node = W.NearestResource(h.X, h.Z, res, 90, null, Ok) ?? (kill != null && kill.Alive && kill.Amount > 0 && kill.Workers.Count < kill.MaxWorkers && Ok(kill) ? kill : null)
            ?? W.NearestResource(w.Pos.X, w.Pos.Z, res, 170, null, Ok);
        if (node == null) return false;
        Hands.Order(P, new[] { w }, new Order("gather") { Target = node, Auto = true });
        // a new stone site gets a tower (CR:365-393)
        if (res == "stone" && (StoneSite == null || D2(StoneSite.Value, Pt.Of(node)) > 40)) { StoneSite = Pt.Of(node); if (D2(Pt.Of(node), h) > 45) TowerAt = Pt.Of(node); }
        return true;
    }
    /// <summary>enemy fighters near a spot (CheckForEnemy, UM:615): workers are not sent there</summary>
    public bool DangerAt(double x, double z)
    {
        bool hit = false;
        W.UHash.Query(x, z, 30, u => { if (!hit && u.Alive && u.Owner != null && u.Owner != P && P.IsEnemy(u.Owner) && !u.IsWorker && DistP(u, x, z) < 30) hit = true; });
        return hit;
    }
    /// <summary>Wild animals worth hunting from the village, best first: like the scripts' search (ComputeBestAreaInBestAiMap:
    /// rings around the start location; peaceful herds before neutral animals before hunters), the nearest first inside
    /// a kind. Never next to a big predator or to enemy fighters (CheckForEnemy).</summary>
    public List<Unit> Prey(Census S, double range, double maxHp = 1e9, bool skulls = false)
    {
        var h = S.Home!.Value; var outp = new List<(double V, Unit U)>();
        var beasts = W.Units.Where(u => u.Alive && u.Wild && !u.Naval && u.Def!.Aggressive > 0 && u.MaxHp >= 1500).ToList();
        int reg = GroundRegion(h.X, h.Z);
        foreach (var u in W.Units)
        {
            if (!u.Alive || !u.Wild || u.Naval || u.MaxHp > maxHp) continue;
            double d = DistP(u, h);
            if (d > range || RegionValue("FOOD", u.Pos.X, u.Pos.Z) < 0) continue;
            // only on the village's ground (fixed 2026-11-12: the hunters of an Aje co-op ally in MIRAGE mission 1 were
            // sent after Megaloceros on ground they had no way to - each attack order ended at once, "no way", and was
            // given again by the next think; the hunts timed out for ten minutes and the village stayed without skulls)
            if (reg >= 0 && GroundRegion(u.Pos.X, u.Pos.Z, 3) is var ur && ur >= 0 && ur != reg) continue;
            if (beasts.Any(x => SegDist(x.Pos.X, x.Pos.Z, h.X, h.Z, u.Pos.X, u.Pos.Z) < 60) || DangerAt(u.Pos.X, u.Pos.Z)) continue;     // on the hunters' way
            int kind = u.Def!.Aggressive < 0 ? 0 : u.Def.Aggressive > 0 ? 2 : 1;
            double v = kind * 120 + d + u.MaxHp * 0.03 - (skulls ? Math.Min(40, (u.Stats?.Scalps ?? 0) * 2) : Math.Min(60, D.CorpseFood(u.Name) * 0.05));
            outp.Add((v, u));
        }
        return outp.OrderBy(x => x.V).Select(x => x.U).ToList();
    }
    /// <summary>The first prey (up to `max`, in their order) that the village's units can walk to: a way from the guard
    /// point that reaches them (Nav.Find, cached per cell pair and grid version). The ground regions are labelled before
    /// anything is built (World), so a mission's palisades, a village's buildings or a forest closing a valley later are
    /// not in them (fixed 2026-11-12: the Aje ally of MIRAGE mission 1 sent its skull hunts after Megaloceros behind
    /// the walls north of its village for ten minutes - every attack order ended "no way" in a dead end; the remake's
    /// own check: the scripts had the engine's prey search [?engine]).</summary>
    List<Unit> Walkable(Census S, List<Unit> prey, int max = 4)
    {
        if (prey.Count == 0 || S.Home == null) return prey;
        var nav = W.Nav;
        var from = GuardPoint(S);
        int fc = nav.Idx(from.X, from.Z);
        if (_walkCache.Count > 256 || _walkVer != nav.Version) { _walkCache.Clear(); _walkVer = nav.Version; }
        var res = new List<Unit>();
        int tries = 0;
        foreach (var u in prey)
        {
            int tc = nav.Idx(u.Pos.X, u.Pos.Z);
            if (!_walkCache.TryGetValue((fc, tc), out bool ok))
            {
                if (tries++ >= 6) break;                     // (a few searches a think at most)
                nav.Find(from.X, from.Z, u.Pos.X, u.Pos.Z, 0.45, 20000, P);
                ok = nav.Found;
                _walkCache[(fc, tc)] = ok;
            }
            if (ok) { res.Add(u); if (res.Count >= max) break; }
        }
        return res;
    }
    readonly Dictionary<(int From, int To), bool> _walkCache = new();
    int _walkVer = -1;
    // Nothing left to eat around the village, or every place at its fields is taken while food is what it lacks: one
    // more field / greenhouse / slaughterhouse on top of the list, up to eight places. (Not in the scripts: the lists
    // name one to three such buildings and the villages live on the gifts and on hunting beyond that.)
    bool MoreFarms(Census S)
    {
        var p = P;
        var farms = S.Blds.Where(b => W.IsFarm(b) && b.Def!.Unlimited.Contains("food")).ToList();
        if (farms.Any(b => !b.Built) || farms.Sum(b => W.FarmSlots(b)) >= 8) return false;
        var a = D.Actions(p).Where(x => x.Cat == "Build/BLDG" && x.Results.Count > 0 && (D.Def(x.Results[0].Obj, p)?.Unlimited ?? new List<string>()).Contains("food") && BuildCheck(p, x) == null)
            .OrderBy(x => x.Cost.Food + x.Cost.Wood + x.Cost.Stone).FirstOrDefault();
        return a != null && AddOnTop("BLDG/" + a.Results[0].Obj, "", unique: false);
    }
    /// <summary>how many hunters an animal takes: a peaceful one runs, the others fight back (and call their herd)</summary>
    static int Hunters(Unit u) => u.Def!.Aggressive < 0 ? (u.MaxHp > 300 ? 2 : 1) : (int)Math.Max(2, Math.Min(6, Math.Ceiling(u.MaxHp / 150)));
    /// <summary>SpawnResources("food", 10) of the cheat manager: what the scripts fall back to when there is nothing to eat</summary>
    void CheatFood()
    {
        var p = P; double n = Multimap ? (Cls == "Easy" ? 5 : Cls == "Medium" ? 20 : 10) : 20;
        p.Res["food"] = Math.Max(p["food"], Math.Min(Cap("food"), p["food"] + n));
    }
    // No food left around the village (CAiTaskPickAnimalFood): the workers hunt a harmless animal nearby themselves
    // (the task's own game command on the animal); otherwise the fight module is asked for a "quick" attack on the
    // nearest animals and the workers take the carcass. With nothing to hunt, and on campaign maps, the cheat manager
    // gives a little food instead. The market sells food for skulls.
    void FoodThink(Census S)
    {
        var p = P; var h = S.Home!.Value;
        if (W.Time - _huntT < 12) return;
        // a village that cannot even pay a worker any more is helped whatever lies around it (not in the scripts, where
        // such a village is simply dead: without it the easy levels, which get no gifts, can lock up for good)
        if (S.Workers.Count < 3 && WorkerCost != null && p["food"] < WorkerCost.Food) { _huntT = W.Time; CheatFood(); return; }
        if (p["food"] >= 250) return;
        var corpse = FoodPrey != null && !FoodPrey.Alive ? FoodPrey.CorpseNode : null;
        if (corpse != null && corpse.Alive && corpse.Amount > 0) return;                                           // still eating
        if (S.Blds.Any(b => W.IsFarm(b) && b.Def!.Unlimited.Contains("food"))) return;
        if (W.NearestResource(h.X, h.Z, "food", 90, null, n => !DangerAt(n.Pos.X, n.Pos.Z)) != null) return;
        _huntT = W.Time;
        MoreFarms(S);
        if (Alarm != null && !Alarm.Animal) { CheatFood(); return; }             // nobody leaves the village now
        var market = S.Blds.FirstOrDefault(b => b.Built && Acts(b).Any(a => a.Id == "buy_food" && D.Check(b.RulesOwner(), a, b) == null));
        if (market != null && p["skulls"] >= 100 + ResV(Reserve, "skulls")) { Hands.UseMove(p, market, Acts(market).First(a => a.Id == "buy_food")); return; }
        if (Attacks.Any(x => x.Type == "food") || (FoodPrey != null && FoodPrey.Alive && W.Time - _workerHuntT < 45)) return;     // hunters are out
        // the last hunt brought nothing (the hunters died, the animal got away): the cheat manager's food
        if (FoodPrey != null && (FoodPrey.Alive || corpse == null)) CheatFood();
        // (a co-op ally hunts too: the campaign's computer players live off the cheat manager's food, 20 per 12 s - an Aje
        // ally whose bushes were gone stood in its second epoch with 100-200 food a minute and its workers idle)
        var prey = Multimap || CoopAlly ? Walkable(S, Prey(S, 170, S.Age < 3 ? 700 : 3000)) : new List<Unit>();
        FoodPrey = prey.FirstOrDefault();
        if (prey.Count == 0) { CheatFood(); return; }
        var a0 = prey[0];
        // the workers themselves, when the animal is harmless and close
        bool near = DistP(a0, h) < 120;
        if (near && Alarm == null && a0.MaxHp < 300 && !(a0.Def!.Aggressive > 0))
        {
            var w = S.Workers.Where(x => !x.Inside && x.Task.Type != "build" && !(MilitiaSet != null && MilitiaSet.Has(x)))
                .OrderBy(x => x.Task.Type == "idle" ? 0 : 1).ThenBy(x => DistE(x, a0)).Take(3).ToList();
            if (w.Count >= 2) { Hands.Order(p, w, Order.Attack(a0)); _workerHuntT = W.Time; return; }
        }
        int n = Hunters(a0);
        if (!Hunt(S, "food", prey.Take(1).ToList(), n, n + 2)) CheatFood();
    }
    /// <summary>idle fishing boats back to the nearest shoal</summary>
    void FishingThink(Census S)
    {
        foreach (var b in S.Fleet) if (b.Name.Contains("fishing_boat") && b.Task.Type == "idle") { var n = W.NearestResource(b.Pos.X, b.Pos.Z, "food", 400, null, _ => true, water: true); if (n != null) W.StartFishing(b, n); }
    }

    // ------------------------------------------------------------------ placement
    // storehouses next to their resource, towers where the defence wants them (else towards the enemy), harbours on
    // the coast near the fish, the rest around the base; never inside a region the map forbids (BuildModifier < 0)
    (double X, double Z, double Rot)? FindSpot(Census S, string name, ActionDef a, Pt? at)
    {
        var p = P; var home = S.Home!.Value;
        double hx = home.X, hz = home.Z;
        var e = EnemyHint(S);
        double toE = SMath.Atan2(e.Z - hz, e.X - hx);
        var def = D.Def(name, p);
        bool tower = def != null && Regex.IsMatch(def.Script ?? "", "Tower|Bunker");
        var deliv = def != null && def.Delivery.Count > 0 && def.Delivery.Count < 3 ? def.Delivery : null;
        if (def != null && def.Coastal)
        {
            var shoal = W.NearestResource(hx, hz, "food", 260, null, _ => true, water: true);
            double tx = shoal != null ? shoal.Pos.X : e.X, tz = shoal != null ? shoal.Pos.Z : e.Z;
            for (int k = 0; k <= 14; k++)
            {
                double f = k / 14.0, x = hx + (tx - hx) * f, z = hz + (tz - hz) * f;
                if (!W.IsWater(x, z, 1.5)) continue;
                var pl = W.PlacementOf(name, x, z, 0, p);
                if (pl.Ok) return (pl.X, pl.Z, pl.Rot);
            }
            // no shore on the way to the fish / the enemy (fixed 2026-11-11: the Ninigi's harbour in mission 8 failed for
            // minutes): the nearest shore around the base, out to 220 m
            for (double r = 20; r <= 220; r += 10)
            {
                int n = Math.Max(12, (int)(2 * Math.PI * r / 10));
                for (int i = 0; i < n; i++)
                {
                    double ang = toE + i * 2 * Math.PI / n, x = hx + SMath.Cos(ang) * r, z = hz + SMath.Sin(ang) * r;
                    if (!W.IsWater(x, z, 1.5)) continue;
                    var pl = W.PlacementOf(name, x, z, 0, p);
                    if (pl.Ok) return (pl.X, pl.Z, pl.Rot);
                }
            }
            return null;
        }
        Pt? near = null;
        if (at != null) near = at;
        else if (tower && TowerAt != null) near = TowerAt;
        if (deliv != null && near == null)
        {
            var n = W.NearestResource(hx, hz, deliv.Contains("wood") ? "wood" : deliv.Contains("stone") ? "stone" : "food", 90);
            if (n != null) { hx = (hx + n.Pos.X * 2) / 3; hz = (hz + n.Pos.Z * 2) / 3; }
        }
        if (near != null) { hx = near.Value.X; hz = near.Value.Z; }
        double rot0 = JsRound((toE + Math.PI / 2) / (Math.PI / 2)) * (Math.PI / 2);
        for (int k = 0; k < 90; k++)
        {
            double r = (deliv != null || near != null ? 6 : 18) + (k / 90.0) * 45 + R.Next() * 6;
            double ang;
            if (near != null) ang = R.Next() * 6.283;
            else if (tower) ang = toE + (R.Next() - 0.5) * 1.4;
            else if (deliv != null) ang = R.Next() * 6.283;
            else { double f = Math.PI * (0.35 + R.Next() * 1.3); ang = toE + f * (R.Next() < 0.5 ? 1 : -1); }
            double x = JsRound(hx + SMath.Cos(ang) * r), z = JsRound(hz + SMath.Sin(ang) * r);
            if (Regions.Count > 0 && RegionValue("BuildModifier", x, z) < 0) continue;
            // placement() = where it really goes (wall-grid snapping for towers / traps)
            var pl = W.PlacementOf(name, x, z, rot0, p);
            if (pl.Ok && pl.Existing == null) { if (tower && near == TowerAt) TowerAt = null; return (pl.X, pl.Z, pl.Rot); }
        }
        // nothing on the preferred side (a base at the water or a cliff: death_valley): any side, farther out (the
        // engine's planner places them itself [?engine])
        for (int k = 0; k < 60 && near == null; k++)
        {
            double r = 14 + (k / 60.0) * 60 + R.Next() * 6, ang = R.Next() * 6.283;
            double x = JsRound(hx + SMath.Cos(ang) * r), z = JsRound(hz + SMath.Sin(ang) * r);
            if (Regions.Count > 0 && RegionValue("BuildModifier", x, z) < 0) continue;
            var pl = W.PlacementOf(name, x, z, rot0, p);
            if (pl.Ok && pl.Existing == null) return (pl.X, pl.Z, pl.Rot);
        }
        // still nothing (fixed 2026-11-11: a base in a forest or grown full - the Aje on multi_2_jun_001 found no place
        // for a rodeo, a slaughterhouse or a cook house within 80 m from minute 5 on and starved for food; the Hu
        // warehouse, the SEAS tents of mission 13): every place on rings out to 80 m, the nearest first, at two facings.
        // The original's engine planner placed the buildings itself [?engine]; random tries alone miss the few free
        // places. (No random numbers: the tries above draw the same ones as before.)
        // Each ring from the side away from the enemy; only on the ground region of home (a place across a river or up a
        // cliff is one the workers cannot walk to).
        // Only for a village with a main building, not for a co-op AI commander in a hero phase (its first tent went up
        // where the heroes stood - CoopPhaseTests.CoopHeroPhase08) and no farther out than the random tries (a mission's island seat put its garage on
        // the far side, towards seat 1's landing - CoopPhaseTests).
        if (near == null && S.Base != null && !HeroPhase)
        {
            hx = home.X; hz = home.Z;
            var nav = W.Nav;
            int reg = GroundRegion(hx, hz);
            for (double r = 12; r <= 80; r += 4)
            {
                int n = Math.Max(8, (int)(2 * Math.PI * r / 6));
                for (int i = 0; i < n; i++)
                {
                    // (0, +1, -1, +2, -2 ... steps from the back)
                    int s = (i + 1) / 2 * (i % 2 == 0 ? -1 : 1);
                    double ang = toE + Math.PI + s * 2 * Math.PI / n;
                    double x = JsRound(hx + SMath.Cos(ang) * r), z = JsRound(hz + SMath.Sin(ang) * r);
                    if (Regions.Count > 0 && RegionValue("BuildModifier", x, z) < 0) continue;
                    if (reg >= 0 && nav.Region![nav.Idx(x, z)] is var rg && rg >= 0 && rg != reg) continue;
                    for (int q = 0; q < 2; q++)
                    {
                        var pl = W.PlacementOf(name, x, z, rot0 + q * Math.PI / 2, p);
                        if (pl.Ok && pl.Existing == null) return (pl.X, pl.Z, pl.Rot);
                    }
                }
            }
        }
        return null;
    }
    /// <summary>roughly where the enemy is (for facing buildings and the guard point): the nearest enemy village</summary>
    public Pt EnemyHint(Census S)
    {
        double now = W.Time;
        if (_hint != null && now - _hintT < 10) return _hint.Value;
        Pt? best = null; double bd = 1e9;
        foreach (var o in Enemies()) { var b = BaseOf(o); if (b == null) continue; double d = D2(b.Value, S.Home!.Value); if (d < bd) { bd = d; best = b; } }
        _hint = best ?? new Pt(0, 0); _hintT = now;
        return _hint.Value;
    }
}
