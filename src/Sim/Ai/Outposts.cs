// Outposts of the computer player: secondary bases with a crew and buildings of their own (goals/AiGoalBuildOutpost,
// AiGoalIslandSurveillance, AiGoalBuildArmy's attack outpost; Modules/AiModuleEconomy StartOutpost;
// docs/spec/ai_group6.md §2).
//
//   * StartOutpost(pos, type): at most 5 per game - a start that fails counts too (EM:378, kept) - not in
//     "Single 06"; an enemy within 60 m of the place makes it ask for an attack there instead (CheckForEnemy).
//   * The crew: RessourceOutpost, AttackOutpost and OP_ResAll get the army of their name (settings tables; Hu and
//     Ninigi, which have none: 2 workers and 1-2 of their first infantry) for free, once (CheatMgr, BO:234-249);
//     EmptyOutpost (the trade outpost) takes 1-3 of the village's workers. Then 1-2 units of the pool join, and they
//     go there. The crew belongs to the outpost: its workers build there and gather within 60 m of it.
//   * The buildings: one request at a time, the first row of the type's list (ai.json outposts) the outpost lacks
//     (BuildIfNotExist per outpost: buildings within 60 m of it count).
//   * Fewer than 2 objects or no worker left: it starts again (the crew is not given a second time).
//   * On skirmish maps, when the trade route is ready or the village defends, the fighters of the outposts go home
//     (CheckNumUnitsInOutpost, BO:670-703).
//   * Island surveillance (every ~100 s; skirmish, epoch 2+, not a Turtle, not "Single 09"): the neutral stone
//     nearest the start location without own objects within 60 m gets an OP_ResAll outpost - one on the home island,
//     one on another land (maps with harbours; its crew crosses by ship: OutpostTransfer) - when nothing foreign is
//     within 60 m, or 1-9 foreign objects of which the first is an enemy's (then a suicide attack is asked for them
//     too, IS:157-231).
//   * The attack outpost (BA:128-140): on skirmish maps from epoch 2, once, 45 % of the way to the enemy on the home
//     island [?engine GetPossibleAtkOutpostPosition]; every 10th think it wants its 3 rows (arena, farm, 2 towers).
//   * Campaign triggers start RessourceOutpost / AttackOutpost at their position (FM:918-921).
//
// Ported from remake/src/game/ai/outposts.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Ai;

/// <summary>an outpost: a place with the buildings within 60 m of it and a crew</summary>
public sealed class Outpost : IAiSite
{
    public int Id;
    public string Type = "";
    public Pt Pos { get; set; }
    public int Island;
    public string State = "building";
    public bool Spawned;
    public double T;
    public int N;
}

public sealed partial class TribeAI
{
    const int MAX_OUTPOSTS = 5;
    const double R_OUT = 60;
    static readonly HashSet<string> SPAWNED = new(StringComparer.Ordinal) { "RessourceOutpost", "AttackOutpost", "OP_ResAll" };
    /// <summary>BA:96-110: what an attack outpost wants besides its list</summary>
    static readonly Dictionary<string, (string Request, string Flag, int Count)[]> ATK_EXTRA = new()
    {
        ["Hu"] = new[] { ("BLDG/hu_arena", "", 1), ("BLDG/hu_small_animal_farm", "", 1), ("BLDG/hu_large_tower", "", 2) },
        ["Aje"] = new[] { ("BLDG/aje_rodeo", "", 1), ("BLDG/aje_small_farm", "medium_farm", 1), ("BLDG/aje_medium_tower", "", 2) },
        ["Ninigi"] = new[] { ("BLDG/ninigi_engineer", "", 1), ("BLDG/ninigi_animal_farm", "", 1), ("BLDG/ninigi_small_tower", "", 2) },
    };

    public List<Outpost> Outposts = new();
    int _outpostStarts;
    /// <summary>unit -> its outpost (the crew is out of the census' pool)</summary>
    public OrderedMap<Unit, Outpost>? Crew;
    double _isT;
    List<Pt>? _stoneSpots;
    /// <summary>the attack outpost: its id, -1 = none possible / not started</summary>
    int? _atkOutpost;

    OrderedMap<Unit, Outpost> OutpostCrew() => Crew ??= new OrderedMap<Unit, Outpost>();
    /// <summary>EM.StartOutpost -> the outpost's id | null</summary>
    public int? StartOutpost(Pt pos, string type)
    {
        if (LevelName == "Single 06") return null;
        if (_outpostStarts >= MAX_OUTPOSTS) return null;
        _outpostStarts++;
        var foes = WallFoes(pos.X, pos.Z, R_OUT);
        if (foes.Count > 0)
        {
            var S = this.S ?? Census();
            int blds = foes.Count(e => e is Building);
            RequestAttack(S, blds * 2 > foes.Count ? "siege" : "suicide", foes[0].Owner, foes);
            return null;
        }
        var o = new Outpost { Id = (Outposts.Count > 0 ? Outposts.Max(x => x.Id) : 0) + 1, Type = type, Pos = pos, Island = W.LandIsland(pos.X, pos.Z), State = "building", Spawned = false, T = W.Time, N = 0 };
        Outposts.Add(o);
        return o.Id;
    }
    Outpost? OutpostOf(Entity e) => Outposts.FirstOrDefault(o => o.Pos.Dist(e) <= R_OUT);
    List<Unit> OutpostUnits(IAiSite o)
    {
        var outp = new List<Unit>();
        foreach (var (u, x) in OutpostCrew()) if (x == o) { if (u.Alive && u.Owner == P) outp.Add(u); else Crew!.Delete(u); }
        return outp;
    }
    void JoinOutpost(Outpost o, IEnumerable<Unit> units) { foreach (var u in units) { OutpostCrew().Set(u, o); Claims.Delete(u); } S = null; }
    void LeaveOutpost(IEnumerable<Unit> units) { foreach (var u in units) OutpostCrew().Delete(u); S = null; }
    /// <summary>the crew's workers for a building request of the outpost (Economy.cs Realise)</summary>
    List<Unit> OutpostWorkers(IAiSite o) => OutpostUnits(o).Where(u => u.IsWorker && !u.Inside).ToList();
    void OutpostsThink(Census S)
    {
        var p = P;
        if (Multimap && S.Age >= 2)
        {
            if (GameType != "Defender" && GameType != "KingOfTheHill") IslandSurveillance(S);   // (AM:64-66, IS:276)
            AttackOutpostThink(S);
        }
        var h = S.Home!.Value;
        foreach (var o in Outposts.ToList())
        {
            o.N++;
            var units = OutpostUnits(o);
            // the fighters of the outposts on the home island go home (BO:188-197)
            if (Multimap && (TradeReady || Defending) && o.Island == W.LandIsland(h.X, h.Z))
            {
                var back = units.Where(u => !u.IsWorker && !System.Text.RegularExpressions.Regex.IsMatch(u.Name, "_cart$|trade_dino")).ToList();
                if (back.Count > 0) { LeaveOutpost(back); SendHome(back); }
                if (Defending) continue;
            }
            if (Defending) continue;
            var foes = WallFoes(o.Pos.X, o.Pos.Z, R_OUT);
            if (foes.Count > 0) { int blds = foes.Count(e => e is Building); RequestAttack(S, blds * 2 > foes.Count ? "siege" : "suicide", foes[0].Owner, foes); continue; }
            switch (o.State)
            {
                case "building":
                {
                    if (SPAWNED.Contains(o.Type) && !o.Spawned)
                    {
                        o.Spawned = true;
                        var army = Data.Army(p.Tribe, Cfg?.ValueTable ?? "Dodo", o.Type) ?? OutpostFallback(S);
                        var made = army != null ? SpawnArmy(S, army, o.Pos, true, o.Type) : new List<Unit>();
                        JoinOutpost(o, made);
                    }
                    else if (!o.Spawned)
                    {
                        o.Spawned = true;
                        var ws = new List<Unit>();
                        for (int i = 0, n = 1 + Rnd(3); i < n; i++) { var w = PickWorker(S, o.Pos, ws); if (w != null && S.Workers.Count - ws.Count > 3) ws.Add(w); }
                        JoinOutpost(o, ws);
                    }
                    o.State = "waiting";
                    break;
                }
                case "waiting":
                {
                    var extra = QueryPool(S, 1, 2, null, true) ?? new List<Unit>();
                    JoinOutpost(o, extra.Take(1 + Rnd(2)));
                    var all = OutpostUnits(o);
                    // an outpost on another land: the units cross in transports (BO:301 RequestTransfer -> the area module)
                    if (o.Island != W.LandIsland(h.X, h.Z)) { OutpostTransfer(S, o, all.Where(u => W.LandIsland(u.Pos.X, u.Pos.Z) != o.Island).ToList()); o.State = "supervising"; break; }
                    foreach (var u in all)
                    {
                        double ox = (R.Next() - 0.5) * 12, oz = (R.Next() - 0.5) * 12;
                        Hands.Order(p, new[] { u }, new Order(u.IsWorker ? "move" : "attackmove") { X = o.Pos.X + ox, Z = o.Pos.Z + oz, Auto = true });
                    }
                    o.State = "supervising";
                    break;
                }
                case "supervising":
                {
                    var objs = W.Buildings.Where(b => b.Alive && b.Owner == p && o.Pos.Dist(b) <= R_OUT).ToList();
                    var ws = units.Where(u => u.IsWorker).ToList();
                    if ((objs.Count + units.Count < 2 || ws.Count == 0) && W.Time - o.T > 120) { o.State = "building"; break; }
                    OutpostRequestNext(S, o, objs);
                    OutpostWork(S, o, ws, objs);
                    break;
                }
            }
        }
    }
    /// <summary>Hu and Ninigi have no settings tables (the installation holds Aje's and SEAS's): 2 workers and 1-2 of the
    /// tribe's first infantry at level 2, like Aje's rows (worker 1-2, spearman level 2 1-2)</summary>
    List<ArmyRow> OutpostFallback(Census S)
    {
        string t = P.Tribe;
        string? worker = t switch { "Hu" => "hu_worker", "Aje" => "aje_worker", "Ninigi" => "ninigi_worker", "SEAS" => "seas_worker", _ => null };
        string? foot = t switch { "Hu" => "hu_warrior", "Aje" => "aje_spearman", "Ninigi" => "ninigi_warrior", "SEAS" => "seas_warrior", _ => null };
        var outp = new List<ArmyRow> { new() { Min = 2, Max = 2, Alternatives = { new ArmyAlt { Cls = worker ?? "undefined" } } } };
        if (foot != null && D.Exists(foot)) outp.Add(new ArmyRow { Min = 1, Max = 2, Alternatives = { new ArmyAlt { Cls = foot, Level = 1 } } });
        return outp;
    }
    /// <summary>RequestNext: the first row the outpost lacks, one request at a time</summary>
    void OutpostRequestNext(Census S, Outpost o, List<Building> objs)
    {
        if (Extra.Any(e => e.Outpost == o && Missing(S, e, e.R) > 0)) return;
        var rows = (Data.Outpost(o.Type, P.Tribe) ?? new List<AiRow>()).Select(r => (r.Request, r.ObjFlag ?? "", r.Count != 0 ? r.Count : 1)).ToList();
        if (o.Type == "AttackOutpost" && o.N % 10 == 0 && ATK_EXTRA.TryGetValue(P.Tribe, out var ex)) rows.AddRange(ex);
        foreach (var (request, flag, count) in rows)
        {
            var R0 = Resolve(request, flag);
            if (R0 == null || R0.Kind != "bldg") continue;
            int have = objs.Count(b => b.Name == R0.Cls);
            if (have >= count) continue;
            AddOnTop(request, flag, count: count, pos: o.Pos, outpost: o);
            return;
        }
    }
    /// <summary>the crew's workers: build what stands unfinished at the outpost, else gather within 60 m of it</summary>
    void OutpostWork(Census S, Outpost o, List<Unit> ws, List<Building> objs)
    {
        var open = objs.Where(b => !b.Built).ToList();
        foreach (var w in ws)
        {
            if (w.Inside || w.Task.Type == "build" || w.Task.Type == "attack") continue;
            if (open.Count > 0) { Hands.Order(P, new[] { w }, new Order("build") { Target = open[0], Auto = true }); continue; }
            if (w.Task.Type == "gather" && w.Task.Node != null && o.Pos.Dist(w.Task.Node) <= R_OUT + 10) continue;
            foreach (var r in o.Type == "OP_ResAll" ? new[] { "stone", "wood", "food" } : new[] { "wood", "stone", "food" })
            {
                var n = W.NearestResource(o.Pos.X, o.Pos.Z, r, R_OUT);
                if (n != null) { Hands.Order(P, new[] { w }, new Order("gather") { Target = n, Auto = true }); break; }
            }
        }
    }
    /// <summary>AiGoalIslandSurveillance: stone outposts (IS:147-231)</summary>
    void IslandSurveillance(Census S)
    {
        if (Personality == "Turtle" || LevelName == "Single 09" || Start == null) return;
        if (W.Time < _isT) return;
        _isT = W.Time + (500 + 1 + Rnd(W.Players.Count + 1)) * 0.2;
        var st0 = Start.Value;
        _stoneSpots ??= W.Resources.Where(n => n.Alive && n.Res == "stone" && !n.Water).OrderBy(n => st0.Dist(n)).Select(Pt.Of).ToList();
        var h = S.Home!.Value;
        int home = W.LandIsland(h.X, h.Z);
        List<Entity> Near(Pt pt, Func<Entity, bool> f)
        {
            var outp = new List<Entity>();
            W.UHash.Query(pt.X, pt.Z, R_OUT, u => { if (u.Alive && !u.Inside && pt.Dist(u) <= R_OUT && f(u)) outp.Add(u); });
            W.BHash.Query(pt.X, pt.Z, R_OUT + 10, b => { if (b.Alive && pt.Dist(b) <= R_OUT && f(b)) outp.Add(b); });
            return outp;
        }
        foreach (var pt in _stoneSpots)
        {
            if (Near(pt, e => e.Owner == P).Count > 0) continue;
            var foreign = Near(pt, e => e.Owner != P && (e is Building || System.Text.RegularExpressions.Regex.IsMatch(e.Cls ?? "", "CHTR|ANML|VHCL")));
            int isle = W.LandIsland(pt.X, pt.Z);
            string kind = isle == home ? "inner" : "outer";
            // (another land: only with harbours on the map - the crew crosses by ship -, one such outpost: IS:204-207)
            if (kind == "outer" && (W.WaterNav == null || !MapOptions.Harbour)) continue;
            if (foreign.Count == 0)
            {
                if (Outposts.Any(o => o.Type == "OP_ResAll" && (kind == "outer" ? o.Island != home : o.Island == isle))) continue;
                StartOutpost(pt, "OP_ResAll");
                return;
            }
            if (foreign.Count < 10 && IsEnemyEntity(foreign[0]))
            {
                RequestAttack(S, "suicide", foreign[0].Owner, foreign);
                StartOutpost(pt, "OP_ResAll");
                return;
            }
        }
    }
    /// <summary>the crew of an outpost on another land goes there by ship: a crossing of its own (Naval.cs, as the area
    /// module's transfers)</summary>
    void OutpostTransfer(Census S, Outpost o, List<Unit> units)
    {
        if (units.Count == 0) return;
        var a = new Attack(this, "transfer", "quick", pos: o.Pos, hunt: true);
        Attacks.Add(a);
        Launched.Add(new LaunchLog { Id = a.Id, T = Math.Floor(W.Time + 0.5), Type = "transfer", Goal = "quick", Scripted = false, Hunt = true, N = units.Count });
        a.Add(units);
        AttackGo(S, a, units);
    }
    /// <summary>AiGoalBuildArmy: the attack outpost (BA:128-140)</summary>
    void AttackOutpostThink(Census S)
    {
        if ((_atkOutpost != null && _atkOutpost != 0) || Personality == "Turtle") return;
        var e = EnemyHint(S); var h = S.Home!.Value;
        int isle = W.LandIsland(h.X, h.Z);
        foreach (var f in new[] { 0.45, 0.4, 0.5, 0.35, 0.55 })
        {
            double x = h.X + (e.X - h.X) * f, z = h.Z + (e.Z - h.Z) * f;
            if (W.LandIsland(x, z) != isle || !W.Nav.IsFree(x, z)) continue;
            _atkOutpost = StartOutpost(new Pt(x, z), "AttackOutpost") ?? -1;
            return;
        }
        _atkOutpost = -1;
    }
}
