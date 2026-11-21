// Fight module of the computer player: the strategist (CAiGoalDisturbAttack: which attack, when, on whom), the
// attacks themselves (the attack goals with their squads: CAiGoalGeneralAttack and its children, CAiTaskBuildSquad,
// CAiTaskAttackObject) and the scripted waves of campaign triggers (CAiGoalSingleplayerAttack).
// docs/spec/ai.md §5, §6. `Fight` = TribeAI methods; S = the census.
//
// Ported from remake/src/game/ai/attack.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Ai;

/// <summary>a unit an attack still has to train: { cls, level, flag, job }</summary>
public sealed class SquadOrder
{
    public string Cls = "";
    public int Level;
    public string? Flag;
    public (Entity Producer, QueueItem? Item)? Job;
    public bool Done;
    public int Fail;
}

/// <summary>
/// One running attack: its units and its state.
///   new -> build (the squad is trained, with a timeout) -> gather (at the edge of the village) -> [sail] -> walk
///   (together) -> fight (targets in class order) -> over: survivors go back to the pool. There is no retreat.
/// </summary>
public sealed class Attack : IClaimer
{
    public readonly TribeAI Ai;
    public readonly int Id;
    /// <summary>plan entry or army name</summary>
    public string Type;
    /// <summary>pyramid | suicide | pureviolence | blitz | stealth | rider | siege | quick | singleplayer | ship</summary>
    public string Goal;
    public string? ArmyName;
    /// <summary>the player it goes against (plan attacks)</summary>
    public Player? Enemy;
    /// <summary>explicit targets (pest patrol, scripted waves)</summary>
    public List<Entity>? Targets;
    /// <summary>way point / attack position</summary>
    public Pt? Pos;
    public bool Scripted;
    /// <summary>a Domination attack on a flag (pos): its units hold the flag afterwards</summary>
    public bool Koth;
    /// <summary>pest patrol, hunting for food / skulls: not one of the plan's three attacks</summary>
    public bool Hunt;
    public bool OnTheWay = true, TargetOnly, Spawned;
    public readonly OrderedSet<Unit> Units = new();
    /// <summary>classes still to train</summary>
    public List<SquadOrder> Orders = new();
    public List<(string Cls, Entity Producer, QueueItem? Item)> Reinforce = new();
    public string State = "new";
    public double T0, StateT;
    public string? Result;
    public int StartCount;
    /// <summary>landing: the transports</summary>
    public List<Unit>? Ship;
    public Pt? Land;
    // the march, the fight
    public Pt? Rally, Aim;
    public bool NoGather, Amove, Crossed, Timer, Ordered;
    public double BuildUntil;
    public List<Unit>? Core;
    public double? BestLead;
    public double BestT;
    public int? LastCount;
    public int Empty;
    /// <summary>a crossing of the water (Naval.cs)</summary>
    public Ferry? FerryState;

    public Attack(TribeAI ai, string type, string goal, string? armyName = null, Player? enemy = null, List<Entity>? targets = null, Pt? pos = null,
        bool scripted = false, bool koth = false, bool hunt = false, bool? onTheWay = null, bool targetOnly = false, bool spawned = false)
    {
        Ai = ai; Id = ++ai.AttackSeq;
        Type = type; Goal = goal; ArmyName = armyName; Enemy = enemy; Targets = targets?.ToList(); Pos = pos;
        Scripted = scripted; Koth = koth; Hunt = hunt; OnTheWay = onTheWay ?? true; TargetOnly = targetOnly; Spawned = spawned;
        T0 = ai.W.Time; StateT = T0;
    }
    public void Drop(Unit u) => Units.Delete(u);
    public List<Unit> Alive()
    {
        var outp = new List<Unit>();
        foreach (var u in Units.ToList()) if (u.Alive && u.Owner == Ai.P) outp.Add(u); else Units.Delete(u);
        return outp;
    }
    public void Add(IEnumerable<Unit> units) { var l = units.ToList(); foreach (var u in l) Units.Add(u); Ai.Claim(l, this); }
    public static Pt Centre(IReadOnlyCollection<Unit> units) { double x = 0, z = 0; foreach (var u in units) { x += u.Pos.X; z += u.Pos.Z; } int n = units.Count != 0 ? units.Count : 1; return new Pt(x / n, z / n); }
    public void Set(string state) { State = state; StateT = Ai.W.Time; }
    public void End(string result)
    {
        if (State == "over") return;
        Result = result;
        State = "over";
        var ai = Ai; var us = Alive();
        ai.Release(us);
        ai.FerryDone(this);                                                     // (a crossing still on: its transports free, home)
        foreach (var u in us) if (u.Stance != 3) u.Stance = 2;               // fighters aggressive again (GA:417)
        // back to the pool = back to the village guard (GuardVillage orders its pool to the guard position): without
        // this the survivors would walk on to wherever the attack was going. Scripted waves stay where they are.
        // a Domination attack (and any PureViolence attack on a map with flags, PV:36): who stands within 30 m of the
        // flag stays there (GA:414-461)
        bool hold = (Koth || (Goal == "pureviolence" && ai.GameType == "KingOfTheHill")) && Pos != null;
        if (!Scripted && result != "brainwash" && Type != "transfer") ai.SendHome(hold ? us.Where(u => Pos!.Value.Dist(u) >= 30).ToList() : us);
        if (Ship != null) foreach (var s in Ship) if (s.Alive) { s.SilentDeath = true; ai.W.Kill(s, null); }
        if (result == "done" && Regex.IsMatch(Type, "^(blitz|quick|towersiege)$")) ai.Pest = null;          // (DA:466-483)
        ai.Attacks.Remove(this);
        var log = ai.Launched.FirstOrDefault(l => l.Id == Id);
        // a request that never set out (no units, enemy too strong, squad not built) is not an attack
        if (log != null && log.Go == null) ai.Launched.Remove(log);
        else if (log != null) { log.Result = result; log.End = Math.Floor(ai.W.Time + 0.5); }
        if (!Scripted && !Hunt) ai.AttackEnded(this, result);
    }
    public override string ToString() => $"attack #{Id} {Type} ({Goal}) {State} {Units.Count} units";
}

public sealed partial class TribeAI
{
    /// <summary>the attack goal behind a plan entry and the army it asks for (FM:287-563, the goals' Think)</summary>
    static readonly Dictionary<string, (string Goal, string? Army)> GOALS = new(StringComparer.Ordinal)
    {
        ["suicide"] = ("suicide", "SuicideAttack_"), ["violence"] = ("pureviolence", "PureViolenceAttack_"), ["blitz"] = ("blitz", "BlitzAttack_"), ["stealth"] = ("stealth", "StealthAttack_"),
        ["rider"] = ("rider", "RiderAttack_"), ["siege"] = ("siege", "SiegeAttack_"), ["towersiege"] = ("siege", "SiegeAttack_"), ["siege_warpgate"] = ("siege", "SiegeAttack_"),
        ["guerilla"] = ("guerilla", "SuicideAttack_"), ["traps"] = ("suicide", "SuicideAttack_"), ["quick"] = ("quick", "Quick"), ["pyramid"] = ("pyramid", null),
        // Domination (DA:866-941, FM:287-383: SetKotH; GameTypes.cs)
        ["KotH Pyramid"] = ("pyramid", null), ["KotH Violence"] = ("pureviolence", "PureViolenceAttack_"), ["KotH Suicide"] = ("suicide", "SuicideAttack_"), ["KotH Siege"] = ("siege", "SiegeAttack_"),
    };
    /// <summary>FM:42</summary>
    const int MAX_ATTACKS = 3;
    /// <summary>of an army row's min-max span, by difficulty</summary>
    static readonly double[] SHARE = { 0, 0.08, 0.17, 0.25, 0.33, 0.42, 0.55, 0.7, 0.85, 1 };
    /// <summary>"attack must be finished in 20 minutes" (GA:943)</summary>
    const double TIME_LIMIT = 1200;
    /// <summary>a hunt (food, skulls, pest) or a support squad: at most this long at its prey (s; twice that in all)</summary>
    const double HUNT_LIMIT = 120;
    /// <summary>target classes in the order a squad takes them on (AO:1240-1438), nearest first inside a class</summary>
    static int TargetRank(Entity e)
    {
        if (e is Unit u)
        {
            if (u.Def!.Unique) return 2;
            if (u.Cls == "VHCL") return 3;
            if (u.Cls == "ANML") return 4;
            if (u.IsWorker) return 6;
            return u.Cls == "SHIP" ? 13 : 5;
        }
        string n = e.Name, s = e.Def?.Script ?? "";
        var wk = e.Def?.WallKind;
        if (n == "defender_object") return 0;                           // a Defender game's crystal first (AO:1314)
        if (PwRemake.Sim.World.World.IsWarpGateScript(s)) return 1;
        if (wk == "wall" || wk == "gate" || wk == "trap") return 11;
        if (wk == "tower" || e.Weapons.Long?.Projectile != null) return 10;
        if (Regex.IsMatch(n, "_arena|_animal_farm|_machine|_weapons|_engineer|barracks|garage|rodeo|dojo")) return 7;
        if (s == "CFireplace" || Regex.IsMatch(n, "headquarters|resource_collector")) return 8;
        return 9;
    }

    // fight state
    public AiData.AttackPlanRow? Plan;
    public bool Scouted;
    public int FirstAttack;
    int _stratN;
    int? _attackStrategy;
    /// <summary>a pest patrol asked for (DA:944-948)</summary>
    public Pt? Pest;
    /// <summary>the human / enemy player who attacked last (DA:368)</summary>
    public Player? LastAttacker;
    /// <summary>an attack started (the interface's warning; main.js G.onAiAttack)</summary>
    public Action<TribeAI, Attack>? OnAiAttack;

    // ------------------------------------------------------------------ the strategist (DA:551-589, 816-1630)
    void StrategyThink(Census S)
    {
        if (Params.NoFight) return;
        CheckGetItem(S);                                                    // an item to fetch (DA:574, Items.cs)
        // Domination: the flags and nothing else (DA:866-941, GameTypes.cs)
        if (GameType == "KingOfTheHill") { KothThink(S); return; }
        int age = S.Age;
        _stratN++;
        if (_attackStrategy == null || _stratN % 20 == 0) _attackStrategy = Rnd(6);       // DA:581
        Plan ??= Data.AttackPlan(Ctx().With(Letter));
        string entry = Plan.Attacks.ElementAtOrDefault(Math.Min(5, age) - 1) ?? "none";
        var enemy = PickEnemy(S);
        // a pest patrol asked for (DA:944-948): what disturbs that spot, else the request is dropped
        Disturb? pest = null;
        if (Pest != null) { pest = Multimap ? Disturbance(S, Pest.Value) : null; if (pest == null || !pest.Found) { Pest = null; pest = null; } }
        // an enemy warp gate is hunted before anything else (DA:1532). (The JS also skips a b.disabled gate - a flag
        // nothing sets in the remake.)
        var gate = Multimap ? W.Buildings.FirstOrDefault(b => b.Alive && PwRemake.Sim.World.World.IsWarpGateScript(b.Def!.Script) && IsEnemyEntity(b)) : null;
        if (gate != null) RequestAttack(S, "siege_warpgate", gate.Owner, new List<Entity> { gate });
        // scouting, once: a "scout" entry, or nobody to attack yet (DA:1536)
        if (!Scouted && Pest == null && (entry == "scout" || (enemy == null && entry != "blitz")) && LevelName != "Single 09")
        {
            SendScout(S);
            Scouted = true;
            return;
        }
        if (pest != null) RequestAttack(S, pest.Type!, pest.Wild ? null : pest.List[0].Owner, pest.List, pest.Pos);    // DA:1546
        if (ShipAttackCheck(S)) return;                                     // a ship attack before the plan's entry (DA:1534)
        if (entry == "none") return;
        if (Rnd(100) <= 10) entry = "pyramid";                              // 11 %: everything it has (DA:1550)
        // difficulty 0 and 1 in the first epoch of a skirmish: only every sixth think, and then a small raid (DA:1552)
        if ((Difficulty == 0 || Difficulty == 1) && age == 1 && Multimap)
        {
            if (FirstAttack >= 5) { entry = "blitz"; FirstAttack = 0; } else entry = "none";
            FirstAttack++;
        }
        if (entry == "none" || entry == "scout" || enemy == null) return;
        // guerilla raids and trap detection are not ported: the entry itself is requested (ship attacks: Naval.cs)
        RequestAttack(S, entry, enemy);
        Pest = null;                                                         // (DA:1628)
    }
    /// <summary>whom to attack (GetValidStartLocationToAttack, DA:368-421): by a strategy drawn anew every 20 thinks -
    /// 0 anybody, 1-2 the weakest, 3-4 a human, 5 the human who attacked last</summary>
    Player? PickEnemy(Census S)
    {
        var list = Enemies().Where(o => BaseOf(o) != null).ToList();
        if (list.Count == 0) return null;
        double Strength(Player o)                                           // CheckUnitLevel (DA:2048)
        {
            double s = 0, workers = 0;
            foreach (var u in W.Units) if (u.Alive && u.Owner == o && !u.Naval) { if (u.IsWorker) workers++; else s += u.Level + (u.Def!.Unique ? 2 : 0); }
            return s != 0 ? s : workers != 0 ? workers : 1;
        }
        Player Weakest(List<Player> l) => l.Aggregate(l[0], (b, o) => Strength(o) < Strength(b) ? o : b);
        var humans = list.Where(o => !o.Ai).ToList();
        int st = _attackStrategy ?? 0;
        if (st == 0) return list[Rnd(list.Count)];
        if (st <= 2) return Weakest(list);
        if (st == 5 && LastAttacker != null && list.Contains(LastAttacker) && !LastAttacker.Ai) return LastAttacker;
        return humans.Count > 0 ? humans[^1] : Weakest(list);
    }
    /// <summary>the fight module's gate (FM:205-241) and the goal for a plan entry; force: past the gate (the timer attack
    /// of the last 5 minutes, FM:1126-1180)</summary>
    public Attack? RequestAttack(Census S, string type, Player? enemy, List<Entity>? targets = null, Pt? pos = null, bool force = false)
    {
        // debug option "passive computer" (game/cheats.js): no attack of its own on the human player (a mission's scripted
        // attacks, StartAttack, still come)
        bool passive = W.Cheats?.Ai == "passive" || Owner?.Passive == true;
        int human = W.Cheats?.Human ?? Owner?.Human ?? 0;
        if (passive && (enemy == null || enemy.Id == human)) return null;
        // melee or ranged army? By the range of what is to be attacked (CheckTargetTypeOfEnemy, DA:1857)
        if ((type == "suicide" || type == "violence") && enemy != null)
        {
            int lng = 0, shrt = 0;
            void Test(Entity e) { if (!e.Alive || e.Owner != enemy) return; var cs = e.Weapons.Long != null ? e.Cs(e.Weapons.Long) : null; if (cs != null && cs.Range >= 10) lng++; else shrt++; }
            foreach (var u in W.Units) Test(u);
            foreach (var b in W.Buildings) Test(b);
            type = lng > shrt ? "violence" : "suicide";
        }
        var plan = Attacks.Where(a => !a.Scripted && !a.Hunt).ToList();
        if (!force)
        {
            if (Defending && type != "pyramid" && type != "quick") return null;          // no attack while the village is under attack
            if (type != "pyramid" && plan.Any(a => a.Type == type)) return null;           // "repeating attack request ignored"
            if (plan.Count >= MAX_ATTACKS) return null;                                    // "maximum attack count reached"
        }
        var g = GOALS.TryGetValue(type, out var gg) ? gg : GOALS["suicide"];
        // the siege army: always SiegeAttack_4 (SI:152 "Max(Min(age, 3), 4)" [bug])
        string? armyName = g.Army == null ? null : g.Goal == "siege" ? g.Army + "4" : g.Army + (g.Army.EndsWith('_') ? Math.Min(5, S.Age).ToString() : "");
        var a = new Attack(this, type, g.Goal, armyName, enemy, targets, pos, koth: type.StartsWith("KotH ", StringComparison.Ordinal));
        return Launch(S, a);
    }
    Attack? Launch(Census S, Attack a)
    {
        Attacks.Add(a);
        Launched.Add(new LaunchLog { Id = a.Id, T = Math.Floor(W.Time + 0.5), Type = a.Type, Goal = a.Goal, Scripted = a.Scripted, Hunt = a.Hunt, N = 0 });
        AttackThink(S, a);
        return a.State == "over" ? null : a;
    }
    public void AttackEnded(Attack a, string result) { }
    /// <summary>units that are not fighting walk (attack-move) to the guard point</summary>
    public void SendHome(List<Unit> units)
    {
        var S = this.S ?? Census();
        if (S.Home == null || units.Count == 0) return;
        var g = GuardPoint(S);
        var far = units.Where(u => u.Alive && !u.Inside && !u.Naval && u.Task.Type != "attack" && DistP(u, g) > 30).ToList();
        foreach (var u in far) u.AiWait = false;
        if (far.Count > 0) Hands.Order(P, far, new Order("attackmove") { X = g.X, Z = g.Z, Auto = true });
    }

    // ------------------------------------------------------------------ squads
    /// <summary>how many of an army row to take: the engine's choice inside min-max is unknown; a share that grows with
    /// the difficulty is used (0: the minimum, 9: the maximum)</summary>
    int SquadCount(ArmyRow e) => (int)Math.Max(e.Min != 0 ? 1 : 0, Math.Floor(e.Min + (e.Max - e.Min) * SHARE[Difficulty] + 1e-6));
    /// <summary>an army for a tribe whose table is missing: units of the derived mix</summary>
    List<ArmyRow>? FallbackArmy(Census S, string? name, bool fix = false)
    {
        var mix = DerivedMix(S);
        var m = Regex.Match(name ?? "", @"(\d)\s*$");
        int tier = m.Success ? m.Groups[1].Value[0] - '0' : S.Age;
        var all = new List<string>();
        foreach (var k in mix.Keys.OrderBy(k => k, StringComparer.Ordinal)) foreach (var g in mix[k]) all.Add(g.Units[0].Cls);
        if (all.Count == 0) return null;
        bool ranged = Regex.IsMatch(name ?? "", "Violence|ranged|siege", RegexOptions.IgnoreCase);
        bool IsR(string c) => D.WeaponSetOf(c, D.MinLevel(c, P), P).Long?.Projectile != null;
        var pref = all.Where(c => IsR(c) == ranged).ToList();
        var src = pref.Count > 0 ? pref : all;
        var list = src.Skip(Math.Max(0, src.Count - 3)).Reverse().ToList();
        // rows like the shipped tables' ("1-4" in the first epoch, "1-6" later); a scripted wave gets fixed numbers
        int n = (int)Math.Ceiling((2 + 2.0 * tier) / list.Count);
        return list.Select(cls => new ArmyRow { Min = fix ? n : 1, Max = fix ? n : tier <= 1 ? 4 : 6, Alternatives = { new ArmyAlt { Cls = cls } } }).ToList();
    }
    /// <summary>the pool units an attack adds to its squad: "bad" ones first (not in the unit mix any more: used up),
    /// then the lowest levels (MOD.QueryUnits). -> null when fewer than min are there</summary>
    public List<Unit>? QueryPool(Census S, double min, double max, ICollection<Unit>? exclude = null, bool all = false)
    {
        var pool = S.Pool.Where(u => !Claims.Has(u) && !(exclude != null && exclude.Contains(u)) && !u.Def!.Unique && u.Item == null).ToList();   // (a unit with an item stays home)
        if (pool.Count < min) return null;
        // how much of the pool an attack may take: the behaviour's "riskiness" (Dodo 100, Giraffe 50, Schnecke 10 %).
        // The scripts only store that number; here it keeps a home guard for the careful personalities
        double risk = Math.Max(0.1, Math.Min(1, Params.Riskiness / 100));
        if (!all) max = Math.Min(max, Math.Max(min, Math.Ceiling(pool.Count * risk)));
        var mix = MixOf(S);
        var good = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gs in mix.Values) foreach (var g in gs) foreach (var u in g.Units) good.Add(u.Cls);
        pool = pool.OrderBy(u => good.Contains(u.Name) ? 1 : 0).ThenBy(u => u.Level).ToList();
        return pool.Take((int)Math.Max(min, max)).ToList();
    }
    /// <summary>"Enemy is too strong!" unless own level score >= strength x enemy level score (the goals' CompareLevel)</summary>
    bool StrongEnough(Attack a, IEnumerable<Unit> units, double strength)
    {
        if (a.Enemy == null) return true;
        double them = 0;
        foreach (var u in W.Units) if (u.Alive && u.Owner == a.Enemy && !u.IsWorker && !u.Naval) them += u.Level;
        return Score(units) >= strength * them;
    }

    // ------------------------------------------------------------------ the attack goals' Think
    void AttacksThink()
    {
        var S = this.S ?? Census();
        foreach (var a in Attacks.ToList()) AttackThink(S, a);
    }
    void AttackThink(Census S, Attack a)
    {
        double now = W.Time;
        if (a.State == "over") return;
        if (now - a.T0 > TIME_LIMIT) { a.End("timeout"); return; }
        // a hunt or a support squad ends HUNT_LIMIT after it reached its prey, or after 2 x HUNT_LIMIT in all (fixed
        // 2026-11-11: hunters after a fleeing deer chased it for the whole 20 minutes of an attack - an Aje co-op ally's
        // one skull hunt of mission 1 held its squad from minute 1 to minute 9, and no other hunt could start)
        if (a.Hunt && !a.Scripted && (now - a.T0 > 2 * HUNT_LIMIT || (a.State == "fight" && now - a.StateT > HUNT_LIMIT))) { a.End("timeout"); return; }
        if (a.Goal == "ship") { ShipAttackThink(S, a); return; }                     // (Naval.cs)
        var us = a.Alive();
        switch (a.State)
        {
            case "new": AttackNew(S, a); return;
            case "build": AttackBuild(S, a, us); return;
            case "gather":
            {
                if (us.Count == 0) { a.End("dead"); return; }
                var g = a.Rally!.Value;
                int near = us.Count(u => DistP(u, g) < 24);
                if (near >= Math.Ceiling(us.Count * 0.8) || now - a.StateT > 40) { AttackGo(S, a, us); return; }
                foreach (var u in us) if (u.Task.Type == "idle" && DistP(u, g) > 14)
                {
                    double ox = (R.Next() - 0.5) * 10, oz = (R.Next() - 0.5) * 10;
                    Hands.Order(P, new[] { u }, new Order("move") { X = g.X + ox, Z = g.Z + oz, Auto = true });
                }
                return;
            }
            case "sail": AttackSail(S, a, us); return;
            case "ferry": return;                                                       // (its own think: FerryThinks, Naval.cs)
            case "walk": AttackWalk(S, a, us); return;
            case "fight": AttackFight(S, a, us); return;
        }
    }
    void AttackNew(Census S, Attack a)
    {
        var lv = Lv; var sq = Data.Squad(a.Goal, Cls);
        if (a.Goal == "pyramid")
        {
            // everything the pool has (PY:101-137). In the first epoch the script lets any number go, one by one as they
            // leave the barracks, and later whatever passes the strength check - a single unit against a weak enemy.
            // Here a raid waits for four (two / three at difficulty 0 / 1: the script's "should have" count of the later
            // epochs, capped at what a first-epoch pool holds) and goes together, in every epoch: without that the units
            // trained after an all-in follow it one by one
            var units = S.Pool.Where(u => !u.Def!.Unique || S.Age > 1).ToList();
            // (the original's way - extraSquads false, the campaign below Nightmare: in the first epoch the units go as
            // they are, without waiting for each other)
            bool orig = !ExtraSquads && S.Age <= 1;
            double need = !a.Scripted && !orig ? Math.Min(4, lv.PyramidAttackUnits != 0 ? lv.PyramidAttackUnits : 4) : 1;
            if (units.Count < need) { a.End("no units"); return; }
            if (S.Age > 1 && !a.Scripted && !StrongEnough(a, units, lv.AttackStrength) && units.Count < (lv.PyramidAttackUnits != 0 ? lv.PyramidAttackUnits : 10)) { a.End("too strong"); return; }
            a.Add(units);
            a.NoGather = orig;
            AttackGather(S, a);
            return;
        }
        string table = Cfg?.ValueTable ?? "Dodo";
        // a tribe without any army table (its settings folder is missing: Hu, Ninigi): the original takes pool units only
        // (below); the remake's own squads from the tech tree where extraSquads allows them (skirmish, the campaign's
        // Nightmare)
        bool own = ExtraSquads && !Data.HasArmies(P.Tribe) && a.Goal != "quick";
        var army = a.ArmyName != null ? Data.Army(P.Tribe, table, a.ArmyName) ?? (own ? FallbackArmy(S, a.ArmyName) : null) : null;
        if (army != null) army = SpecUnits(S, army);
        if (army == null)
        {
            // no army table (GetUnits fails): pool units only (SU:116-151)
            double extra = a.Goal == "pureviolence" ? S.Age * 2 : 0;
            var units = QueryPool(S, Math.Max(1, sq.Min), sq.Max + extra);
            if (units == null) { a.End("no units"); return; }
            if (!StrongEnough(a, units, lv.AttackStrength)) { a.End("too strong"); return; }
            a.Add(units);
            AttackGather(S, a);
            return;
        }
        // the squad: pool units of the wanted classes first (QueryEssentialsFromDefensePool), the rest is trained
        var taken = new OrderedSet<Unit>();
        foreach (var e in army)
        {
            int n = Scaled(SquadCount(e));
            foreach (var alt in e.Alternatives)
                foreach (var u in S.Pool) { if (n <= 0) break; if (u.Name == alt.Cls && !taken.Has(u) && !Claims.Has(u)) { taken.Add(u); n--; } }
            // (a class the tribe has no build action for is an empty need of the solution: nothing is ordered)
            var alt0 = e.Alternatives.FirstOrDefault(x => Resolve("CHTR/" + x.Cls, "") != null);
            if (alt0 == null) { if (n > 0) L.Debug($"player {P.Id}: {a.Type}: no build action for {string.Join("/", e.Alternatives.Select(x => x.Cls))}"); continue; }
            for (; n > 0; n--) a.Orders.Add(new SquadOrder { Cls = alt0.Cls, Level = alt0.Level != null ? alt0.Level.Value + 1 : 0, Flag = alt0.ObjFlag });
        }
        a.Add(taken.ToList());
        SendHome(taken.ToList());                     // the squad forms at the guard point
        a.BuildUntil = W.Time + Data.SquadTimeout(Cls);
        a.Set("build");
        AttackBuild(S, a, a.Alive());
    }
    /// <summary>BuildSquad: train what is missing; when the squad stands, pool units join and the strength is checked</summary>
    void AttackBuild(Census S, Attack a, List<Unit> us)
    {
        var p = P; var lv = Lv; var sq = Data.Squad(a.Goal, Cls);
        foreach (var o in a.Orders)
        {
            if (o.Done) continue;
            if (o.Job is { } job)
            {
                if (job.Producer.Alive && job.Item != null && job.Producer.Queue.Contains(job.Item)) continue;
                // it left the queue: the new unit of that class joins
                var u = S.Pool.FirstOrDefault(x => x.Name == o.Cls && !Claims.Has(x));
                if (u != null) { a.Add(new[] { u }); o.Done = true; if (o.Level > u.Level && W.SkullCost(u.Level, o.Level) <= p["skulls"]) Hands.ChangeLevel(p, u, o.Level); }
                else o.Job = null;
                continue;
            }
            var T = TrainableOf(S, o.Cls);
            if (T == null || T.Producer == null) { if (T == null || T.Why == "none" || T.Why == "req") o.Fail++; continue; }
            if (!ArmyCanPay(S, T.Action.Cost)) { UnitNeed = T.Action.Cost; continue; }
            if (Hands.QueueAction(p, T.Producer, T.Action) == null) o.Job = (T.Producer, T.Producer.Queue.LastOrDefault());
        }
        var open = a.Orders.Where(o => !o.Done).ToList();
        bool late = W.Time > a.BuildUntil;
        if (open.Count > 0 && !late) return;
        us = a.Alive();
        // the timeout with an incomplete squad (BS:566-577, both configurations): the task "spawns" the missing units -
        // the cheat manager does that only on skirmish maps at difficulty 9 (CH:139), in the campaign nothing comes -
        // cancels its requests and reports itself running with the units it has; the goal then adds its pool units
        // (QueryUnits) and sets out. So a squad whose units cannot be trained goes as pool units: MIRAGE mission 3's
        // SP2Attack_1 asks the Amazons for 3-4 dilophosaurs, which only a farm in small-farm mode makes (all of theirs
        // are medium farms by then) - after 80 s (Medium) the wave goes with 0-6 pool units.
        // (The JS ended such a squad as "squad failed" unless 60 % of it was there; the scripts have no such rule.)
        if (open.Count > 0) L.Debug($"player {P.Id}: {a.Type}: squad incomplete after the timeout ({string.Join(", ", open.Select(o => o.Cls))} missing): goes with {us.Count} + pool units");
        foreach (var o in open) if (o.Job is { } job && job.Producer.Alive) { int i = job.Item != null ? job.Producer.Queue.IndexOf(job.Item) : -1; if (i > 0) Hands.CancelQueue(p, job.Producer, i); }
        // SpawnUnits (BS:482-509 MIRAGE, BS:546-577 base): the missing units come from the cheat manager - on skirmish
        // maps at difficulty 9 only (CheatSpawn)
        if (open.Count > 0 && CheatSpawnAllowed)
        {
            int made = 0;
            foreach (var o in open) if (CheatSpawn(S, o.Cls, o.Flag, o.Level, S.Home ?? Start) is { } u) { a.Add(new[] { u }); o.Done = true; made++; }
            if (made > 0) L.Debug($"player {P.Id}: {a.Type}: the cheat manager spawned {made} of {open.Count} missing units");
            us = a.Alive();
        }
        a.Orders = new List<SquadOrder>();
        if (!a.Scripted || !a.Spawned)
        {
            double min = a.Goal == "singleplayer" ? 0 : sq.Min;
            var more = QueryPool(S, min, sq.Max + (a.Goal == "pureviolence" ? S.Age * 2 : 0), a.Units.ToList());
            if (more == null) { a.End("no pool units"); return; }
            if (!a.Scripted && !StrongEnough(a, us.Concat(more), lv.AttackStrength)) { a.End("too strong"); return; }
            a.Add(more);
        }
        AttackGather(S, a);
    }
    void AttackGather(Census S, Attack a)
    {
        var us = a.Alive();
        if (us.Count == 0) { a.End("no units"); return; }
        a.StartCount = us.Count;
        a.Core = us.ToList();                                                  // the squad that sets out (reinforcements come later)
        var log = Launched.FirstOrDefault(l => l.Id == a.Id);
        if (log != null) { log.N = us.Count; log.Go = Math.Floor(W.Time + 0.5); }
        foreach (var u in us) if (u.Stance != 3) u.Stance = 2;                 // SetAggroState(actors, 2) (AO:764)
        if (!a.Scripted && !a.Hunt) { OnAiAttack?.Invoke(this, a); Owner?.OnAiAttack?.Invoke(this, a); }
        var g = GuardPoint(S);
        a.Rally = g;
        var far = us.Where(u => DistP(u, g) > 24).ToList();
        if (a.NoGather || far.Count <= us.Count * 0.2) { AttackGo(S, a, us); return; }
        Hands.Order(P, far, new Order("move") { X = g.X, Z = g.Z, Auto = true });
        a.Set("gather");
    }
    /// <summary>where an attack goes: the way point, the explicit targets, or the enemy's village</summary>
    Pt? AttackAim(Attack a)
    {
        if (a.Pos != null) return a.Pos;
        if (a.Targets != null) { var t = a.Targets.FirstOrDefault(e => e.Alive); if (t != null) return Pt.Of(t); }
        return a.Enemy != null ? BaseOf(a.Enemy) : null;
    }
    void AttackGo(Census S, Attack a, List<Unit> us)
    {
        var aim = AttackAim(a);
        if (aim == null) { a.End("no target"); return; }
        a.Aim = aim;
        // across the water: in transports first (Naval.cs)
        if (S.Home != null)
        {
            int cross = NeedsCrossing(S, a, aim.Value, us);
            if (cross < 0) { a.End("transfer"); return; }                               // (a level's rule: GA:269-290)
            if (cross > 0 && StartCrossing(S, a, us, aim.Value)) return;
        }
        a.Set("walk");
        // skirmish squads attack-move; campaign waves walk unless told to fight on the way (AO:880-896)
        // (hunters walk: an attack-move would take on every animal they pass)
        a.Amove = !a.Hunt && (Multimap || a.OnTheWay || !a.Scripted);
        Hands.Order(P, us, new Order(a.Amove ? "attackmove" : "move") { X = aim.Value.X, Z = aim.Value.Z, Auto = true });
    }
    /// <summary>the squad marches together: whoever is far ahead of the last one waits for it</summary>
    void AttackWalk(Census S, Attack a, List<Unit> us)
    {
        if (us.Count == 0) { a.End("dead"); return; }
        var aim = a.Aim!.Value;
        double Dist(Unit u) => DistP(u, aim);
        double lead = 1e9, tail = 0; int fighting = 0;
        foreach (var u in us.ToList())
        {
            double d = Dist(u); var t = u.Task.Type;
            // a unit that does not come along (it cannot get there, it sits in a building, it is busy with something
            // else) leaves the squad after 45 s instead of keeping everybody waiting for it
            if (u.AiBest == null || u.AiAtk != a.Id || d < u.AiBest - 4 || t == "attack" || u.AiWait)
            { u.AiBest = Math.Min(d, u.AiAtk == a.Id && u.AiBest != null ? u.AiBest.Value : d); u.AiAtk = a.Id; u.AiBestT = W.Time; }
            else if (W.Time - u.AiBestT > 45 && us.Count > 1) { a.Drop(u); Release(new[] { u }); us.Remove(u); u.AiStuckT = W.Time; continue; }
            lead = Math.Min(lead, d); tail = Math.Max(tail, d); if (t == "attack") fighting++;
        }
        // no progress for a minute and a half (a unit that cannot get there): the attack is over, the units go home
        if (a.BestLead == null || lead < a.BestLead - 8 || fighting > 0) { a.BestLead = fighting > 0 ? lead + 8 : lead; a.BestT = W.Time; }
        else if (W.Time - a.BestT > 90 && !a.Scripted) { a.End("stuck"); return; }
        double arrive = a.Scripted && a.Pos != null ? 40 : 55;
        if (lead < arrive || (fighting >= Math.Max(1, us.Count * 0.3) && lead < 110)) { a.Set("fight"); AttackFight(S, a, us); return; }
        double spread = tail - lead;
        SquadPickup(us);                                                      // items on the way (AO:1056, Items.cs)
        foreach (var u in us)
        {
            var t = u.Task.Type;
            if (t == "attack" || t == "pickup") continue;
            double d = Dist(u);
            if (spread > 35 && d < tail - 35 && W.Time - a.StateT > 4) { if (t != "hold") Hands.Order(P, new[] { u }, new Order("hold")); u.AiWait = true; continue; }
            if (t == "idle" || t == "hold" || u.AiWait) { u.AiWait = false; if (u.Stance != 3) u.Stance = 2; Hands.Order(P, new[] { u }, new Order(a.Amove ? "attackmove" : "move") { X = aim.X, Z = aim.Z, Auto = true }); }
        }
    }
    /// <summary>the target of an attack in the order of AO:1240, best first</summary>
    Entity? AttackTargets(Attack a, Pt c)
    {
        Entity? best = null; double bv = 1e18;
        void Test(Entity e)
        {
            if (!e.Alive || e.Inside || e.Untargetable) return;
            if (e is Unit u && (u.Naval || W.HiddenFrom(e, P))) return;
            if (Regions.Count > 0 && RegionValue("Enemy", e.Pos.X, e.Pos.Z) < 0) return;
            double v = TargetRank(e) * 1e5 + c.Dist(e);
            if (v < bv) { bv = v; best = e; }
        }
        if (a.Targets != null)
        {
            a.Targets = a.Targets.Where(e => e.Alive).ToList();
            foreach (var e in a.Targets) Test(e);
            if (best != null || a.TargetOnly || a.Hunt) return best;
        }
        if (a.Koth)
        {
            // at a flag: the enemies around it only (CheckForMissingTargets is off in KotH, AO:1449)
            var at = a.Pos!.Value;
            void Q(Entity e) { if (IsEnemyEntity(e) && at.Dist(e) < 60) Test(e); }
            W.UHash.Query(at.X, at.Z, 60, Q); W.BHash.Query(at.X, at.Z, 70, Q);
        }
        else if (a.Enemy != null)
        {
            foreach (var u in W.Units) if (u.Owner == a.Enemy) Test(u);
            foreach (var b in W.Buildings) if (b.Owner == a.Enemy) Test(b);
        }
        else if (a.Scripted)
        {
            // a wave whose listed targets are gone goes on against whatever enemy is near the attack position
            void Q(Entity e) { if (IsEnemyEntity(e) && c.Dist(e) < 60) Test(e); }
            W.UHash.Query(c.X, c.Z, 60, Q); W.BHash.Query(c.X, c.Z, 70, Q);
        }
        return best;
    }
    void AttackFight(Census S, Attack a, List<Unit> us)
    {
        if (us.Count == 0) { a.End("dead"); return; }
        // the squad itself is dead and only late reinforcements are on their way: they turn round
        if (a.Core != null && !a.Core.Any(u => u.Alive) && !a.Scripted) { a.End("dead"); return; }
        // reinforcements: a squad of more than five that loses a unit asks for two more of its kind (GA:1036-1043)
        if (!a.Scripted && !a.Hunt && a.StartCount > 5 && us.Count < (a.LastCount ?? a.StartCount) && a.Reinforce.Count < 4)
        {
            var cls = us.FirstOrDefault(u => u.Cls != "VHCL");
            if (cls != null) for (int i = 0; i < 2; i++)
            {
                var T = TrainableOf(S, cls.Name);
                if (T != null && T.Producer != null && ArmyCanPay(S, T.Action.Cost) && Hands.QueueAction(P, T.Producer, T.Action) == null) a.Reinforce.Add((cls.Name, T.Producer, T.Producer.Queue.LastOrDefault()));
            }
        }
        a.LastCount = us.Count;
        foreach (var r in a.Reinforce.ToList())
        {
            if (r.Producer.Alive && r.Item != null && r.Producer.Queue.Contains(r.Item)) continue;
            a.Reinforce.Remove(r);
            var u = S.Pool.FirstOrDefault(x => x.Name == r.Cls && !Claims.Has(x));
            if (u != null) { a.Add(new[] { u }); us.Add(u); }
        }
        var c = Attack.Centre(us);
        var tgt = AttackTargets(a, c);
        if (tgt == null && a.Koth)
        {
            // an area attack on the flag (AO:884-925): to the flag, then three looks within 100 m without an enemy -> done
            var fp = a.Pos!.Value;
            if (D2(c, fp) < 100) { a.Empty++; if (a.Empty >= 3) { a.End("done"); return; } } else a.Empty = 0;
            var idle = us.Where(u => u.Task.Type != "attack" && DistP(u, fp) > 10).ToList();
            if (idle.Count > 0) Hands.Order(P, idle, new Order("attackmove") { X = fp.X, Z = fp.Z, Auto = true });
            return;
        }
        if (tgt == null) { a.End("done"); return; }
        bool building = tgt is Building;
        SquadPickup(us);
        foreach (var u in us)
        {
            var t = u.Task.Type;
            if (t == "attack" && u.Task.Target != null && u.Task.Target.Alive) continue;
            if (t == "pickup") continue;
            if (u.AiTgt == tgt && (t == "attackmove" || t == "move") && W.Time - u.AiTgtT < 12) continue;
            u.AiTgt = tgt; u.AiTgtT = W.Time; u.AiWait = false;
            double d = DistE(u, tgt);
            // buildings are attacked directly once the squad stands in front of them; everything else by attack-move
            if ((building && d < 45) || a.Hunt) Hands.Order(P, new[] { u }, Order.Attack(tgt));
            else Hands.Order(P, new[] { u }, new Order("attackmove") { X = tgt.Pos.X, Z = tgt.Pos.Z, Auto = true });
        }
    }

    // ------------------------------------------------------------------ pest patrol (DA:689-812) and the scout
    // RequestPestPatrol (DA:689-699, skirmish maps): a place where something hurt the computer player - a worker killed
    // by a wild animal (Defense.cs), every unit of a task gone (TargetRealization:226), enemies at an item it wants
    // from epoch 3 (GetItem:315, Items.cs). The strategy think looks what disturbs it (GameTypes.cs Disturbance) and
    // asks for that attack until a blitz / quick / towersiege attack succeeds (DA:466-483) or a think gets as far as
    // its plan's attack (DA:1628).
    public void RequestPest(Pt? at) { if (Multimap && at != null) Pest = at; }
    /// <summary>a small squad of pool units against wild animals (pest patrol, food, skulls): one of each kind at a time</summary>
    bool Hunt(Census S, string type, List<Unit> prey, double min, double max)
    {
        if (Attacks.Any(a => a.Type == type)) return true;
        var units = QueryPool(S, min, max, null, true);
        if (units == null) return false;
        var a = new Attack(this, type, "quick", targets: prey.Cast<Entity>().ToList(), hunt: true);
        Attacks.Add(a);
        Launched.Add(new LaunchLog { Id = a.Id, T = Math.Floor(W.Time + 0.5), Type = type, Goal = "quick", Scripted = false, Hunt = true, N = 0 });
        a.Add(units);
        AttackGo(S, a, a.Alive());
        if (a.State != "over") { a.StartCount = units.Count; var log = Launched.FirstOrDefault(l => l.Id == a.Id); if (log != null) { log.N = units.Count; log.Go = Math.Floor(W.Time + 0.5); } }
        return a.State != "over";
    }
    /// <summary>RequestExploration: one fast unit runs to the enemy's village and back</summary>
    bool SendScout(Census S)
    {
        var enemy = Enemies().Select(BaseOf).FirstOrDefault(b => b != null);
        if (enemy == null || S.Home == null) return false;
        var u = S.Pool.OrderByDescending(x => x.Speed).FirstOrDefault();
        if (u == null) return false;
        var h = S.Home.Value; var e = enemy.Value;
        double ang = SMath.Atan2(h.Z - e.Z, h.X - e.X);
        Hands.Order(P, new[] { u }, new Order("move") { X = e.X + SMath.Cos(ang) * 45, Z = e.Z + SMath.Sin(ang) * 45, Auto = true, Then = new Order("move") { X = h.X, Z = h.Z, Auto = true } });
        return true;
    }

    // ------------------------------------------------------------------ campaign triggers (AIFT, §6.5)
    /// <summary>AIFT with custom_attack = 1: { type, targets, position, attackOnTheWay, spawn, spawnPosition,
    /// ignoreLocations, targetOnly, ship, shipLand, behaviour }</summary>
    public sealed class AttackRequest
    {
        public string Type = "";
        public List<Entity> Targets = new();
        public Pt? Position, SpawnPosition;
        /// <summary>ShipLand: where the transports land (null: Position) - the remake's co-op waves land on the coast
        /// nearest their seat and walk to it (CoopRuntimeEnemies)</summary>
        public Pt? Landing;
        public bool AttackOnTheWay, Spawn, IgnoreLocations, TargetOnly, Ship, ShipLand;
        /// <summary>attack_behavior: the editor's hint, never read by the game</summary>
        public string? Behaviour;
    }
    /// <summary>custom_attack = 1. Bypasses the three-attack limit and the defending check. -> the attack's id | null
    /// (an outpost type: the outpost's id)</summary>
    public int? StartAttack(AttackRequest q)
    {
        // an outpost at the position (FM:918-921, Outposts.cs) -> its id
        if (q.Type == "RessourceOutpost" || q.Type == "AttackOutpost") return q.Position != null ? StartOutpost(q.Position.Value, q.Type) : null;
        var targets = q.Targets.Where(e => e != null && e.Alive).ToList();
        if (targets.Count == 0) return null;
        var S = Census(true);
        if (S.Base == null) return null;                     // nothing left of this player
        if (q.Type == "PyramidAttack")
        {
            var pa = new Attack(this, q.Type, "pyramid", targets: targets, pos: q.Position, scripted: true, onTheWay: q.AttackOnTheWay, targetOnly: q.TargetOnly);
            return Launch(S, pa) != null ? pa.Id : null;
        }
        var a = new Attack(this, q.Type, "singleplayer", armyName: q.Type, targets: targets, pos: q.Position, scripted: true, onTheWay: q.AttackOnTheWay, targetOnly: q.TargetOnly, spawned: q.Spawn);
        var army = Data.Army(P.Tribe, "Singleplayer", q.Type) ?? (ExtraSquads ? FallbackArmy(S, q.Type, true) : null);
        if (army == null && ExtraSquads) return null;
        Attacks.Add(a);
        Launched.Add(new LaunchLog { Id = a.Id, T = Math.Floor(W.Time + 0.5), Type = q.Type, Goal = a.Goal, Scripted = true, N = 0 });
        if (army == null)
        {
            // no table (GetUnits fails): pool units only, 1-4 / 1-6 / 2-8 by difficulty, nothing spawned (SP:198-229)
            var sq = Data.Squad("singleplayer", Cls); var units = QueryPool(S, sq.Min, sq.Max);
            if (units == null) { a.End("no units"); return null; }
            a.Add(units);
            AttackGather(S, a);
            return a.State == "over" ? null : a.Id;
        }
        if (q.Spawn)
        {
            var at = q.SpawnPosition ?? Start ?? S.Home!.Value;
            var made = SpawnArmy(S, army, at, q.IgnoreLocations, q.Type);
            if (made.Count == 0) { a.End("no units"); return null; }
            a.Add(made);
            if (q.ShipLand && q.Position != null && Embark(a, made, at, q.Landing ?? q.Position.Value)) { a.StartCount = made.Count; a.Set("sail"); return a.Id; }
            // (ship: a ship attack runs as the same wave - no naval goal)
            a.StartCount = made.Count;
            var log = Launched.FirstOrDefault(l => l.Id == a.Id);
            if (log != null) { log.N = made.Count; log.Go = Math.Floor(W.Time + 0.5); }
            AttackGo(S, a, made);
            return a.State == "over" ? null : a.Id;
        }
        // from the village: the army's units out of the pool, the rest trained, plus a few pool units (SP:145-369)
        army = SpecUnits(S, army);
        var taken = new OrderedSet<Unit>();
        foreach (var e in army)
        {
            int n = SquadCount(e);
            foreach (var alt in e.Alternatives) foreach (var u in S.Pool) { if (n <= 0) break; if (u.Name == alt.Cls && !taken.Has(u) && !Claims.Has(u)) { taken.Add(u); n--; } }
            var alt0 = e.Alternatives.FirstOrDefault(x => Resolve("CHTR/" + x.Cls, "") != null);
            if (alt0 == null) { if (n > 0) L.Debug($"player {P.Id}: {q.Type}: no build action for {string.Join("/", e.Alternatives.Select(x => x.Cls))}"); continue; }
            for (; n > 0; n--) a.Orders.Add(new SquadOrder { Cls = alt0.Cls, Level = alt0.Level != null ? alt0.Level.Value + 1 : 0, Flag = alt0.ObjFlag });
        }
        a.Add(taken.ToList());
        SendHome(taken.ToList());
        a.BuildUntil = W.Time + Data.SquadTimeout(Cls);
        a.Set("build");
        AttackBuild(S, a, a.Alive());
        return a.State == "over" ? null : a.Id;
    }
    /// <summary>MakeUnit (SP:528-662): the units of an army appear for free and outside the population limit; without
    /// ignore_locations each class needs its finished producing building, and appears there</summary>
    List<Unit> SpawnArmy(Census S, List<ArmyRow> army, Pt at, bool ignoreLocations, string name)
    {
        var p = P; var outp = new List<Unit>();
        int k = 0;
        foreach (var e in army)
        {
            var alt = e.Alternatives.FirstOrDefault(x => D.Exists(x.Cls));
            if (alt == null) continue;
            if (Regex.IsMatch(LevelName ?? "", "Single 04") && alt.Cls == "hu_mammoth_log_cannon") continue;
            if (Regex.IsMatch(LevelName ?? "", "Single 06") && alt.Cls == "ninigi_firecannon") continue;
            double wx = at.X, wz = at.Z, wr = 3;
            if (!ignoreLocations)
            {
                var R0 = Resolve("CHTR/" + alt.Cls, "");
                var b = R0 != null ? ProducersOf(S, R0.Action).Where(x => x is Building && x.Hp >= 5).OrderBy(x => at.Dist(x)).FirstOrDefault() : null;
                if (b == null) continue;
                wx = b.Pos.X; wz = b.Pos.Z; wr = b.Radius + 3;
            }
            int n = ScriptedCount(e);
            for (int i = 0; i < n; i++)
            {
                int lv = alt.Level != null ? alt.Level.Value + 1 : Data.SpawnLevel(alt.Cls) is var sl && sl != 0 ? sl : D.MinLevel(alt.Cls, p);
                while (lv > 1 && !(D.Stats(alt.Cls, lv, p) is { Exists: true })) lv--;
                lv = Math.Max(lv, D.MinLevel(alt.Cls, p));
                double ang = k * 2.4, r = (wr != 0 ? wr : 3) + 2 + Math.Sqrt(k) * 2.2; k++;
                if (D.Info(alt.Cls) is { Type: "SHIP" } && W.WaterNav == null) continue;
                var u = W.SpawnUnit(alt.Cls, p, wx + SMath.Cos(ang) * r, wz + SMath.Sin(ang) * r, lv, 0);
                if (u == null) continue;
                if (alt.ObjFlag != null && alt.ObjFlag != alt.Cls) { var up = Acts(u).FirstOrDefault(x => x.Kind == "Upgrades" && x.Id == alt.ObjFlag); if (up != null) W.CompleteUpgrade(u, up); }
                outp.Add(u);
            }
        }
        return outp;
    }
    /// <summary>MIRAGE's UpdateSolutionWithSpecUnits, flyers part (AiGoalGeneralAttack.usl:642-686, called by GetUnits
    /// :526-528 for a squad of the main village from difficulty 4): with the game option FlyingEnabled and from the third
    /// epoch every attack squad also asks for 2 flyers - Aje / Hu pteranodons, Dragon Clan cargolifters (and from the
    /// fourth epoch 1 dirigible), SEAS flying helicopters (ObjFlag seas_helicopter_flying). They come out of the pool
    /// first, the rest is trained like the squad's other units. (Its hero part - Babbage's mobile suit, Schliemann's
    /// zombies ... - the remake does not have yet. The spawned waves (SpawnUnits) and the ship attacks' own squads
    /// (Naval.cs) do not get them.)</summary>
    public List<ArmyRow> SpecUnits(Census S, List<ArmyRow> army)
    {
        if (!OptFlying || Difficulty < 4 || S.Age < 3) return army;
        var add = new List<(string Cls, string? Flag, int N)>();
        switch (P.Tribe)
        {
            case "Aje": add.Add(("aje_pteranodon", null, 2)); break;
            case "Hu": add.Add(("hu_pteranodon", null, 2)); break;
            case "Ninigi": add.Add(("ninigi_cargolifter", null, 2)); if (S.Age >= 4) add.Add(("ninigi_dirigible", null, 1)); break;
            case "SEAS": add.Add(("seas_helicopter", "seas_helicopter_flying", 2)); break;
        }
        if (add.Count == 0) return army;
        var outp = new List<ArmyRow>(army);
        foreach (var (cls, flag, n) in add)
            if (D.Exists(cls)) outp.Add(new ArmyRow { Min = n, Max = n, Alternatives = { new ArmyAlt { Cls = cls, ObjFlag = flag } } });
        return outp;
    }

    // ------------------------------------------------------------------ the cheat manager's free units
    // CAiCheatMgr.SpawnUnit (AiImprovementMgr.usl:125-296, MIRAGE master; the base game's has the same rules without
    // m_bCustommap): it returns at once when !m_bMultimap (MIRAGE: || m_bCustommap - a CampaignAI player of
    // CustomMapSettings.txt), and on a skirmish map unless the difficulty (GetOldDifficultyInt) is 9 (:156); there the
    // location is never ignored (:158), so the unit
    //   * is no trade unit (hu_cart, aje_trade_dino, ninigi_cart; MIRAGE also seas_trade_dino) (:169),
    //   * stays below the tech tree's instance limit (CheckMaxInstanceCount: the heroes and other unique classes),
    //   * finds a free place in the pyramid plus 5 / 2 / 1 / 1 / 0 extra places per level (SolarsSponsoredUnitTuner
    //     :794-847: the AI pyramid 25 / 15 / 8 / 3 / 1, the units of that level; MIRAGE adds its virtual_units_<n>,
    //     which the remake does not have),
    //   * is no fishing boat / floating harbour (MIRAGE also seas_carrier) (:179),
    //   * is a worker only while the player has fewer than 15 (CheckNumWorker :1239),
    //   * has a visible build action (no hidden one - the options' flying_disabled / titans_disabled, a later epoch)
    //     whose producing building stands, finished, with 5 hit points or more, nearest to the position (:189-244),
    // and appears there, gets its ObjFlag upgrade (upgradecheatai) and its level (setlevelcheatai: the need's "level"
    // flag, else GetProperLevel :446) and walks out (CTheLite).
    // Callers in the scripts: BuildSquad's timeout (AttackBuild), GeneralAttack's spawned solutions, the outposts, the
    // ministry of defence's p_bSpawn, the SEAS special attack's triceratops and the naval scouting - the remake has the
    // squads' (the others are goals it does not run).
    static readonly HashSet<string> CHEAT_TRADE = new(StringComparer.Ordinal) { "hu_cart", "aje_trade_dino", "ninigi_cart" };
    static readonly HashSet<string> CHEAT_BOATS = new(StringComparer.Ordinal) { "hu_fishing_boat", "ninigi_fishing_boat", "aje_floating_harbour" };
    static readonly HashSet<string> CHEAT_WORKERS = new(StringComparer.Ordinal) { "hu_worker", "aje_worker", "ninigi_worker", "seas_worker" };
    static readonly int[] CHEAT_EXTRA = { 5, 2, 1, 1, 0 };
    /// <summary>GetProperLevel (CHT:446-470): the level (0-based) of a class whose need has no "level" flag</summary>
    static readonly Dictionary<string, int> CHEAT_LEVEL = new(StringComparer.Ordinal)
    {
        ["hu_jetpack_warrior"] = 1, ["hu_spearman"] = 1, ["hu_pikeman"] = 1, ["hu_archer"] = 1, ["hu_marksman"] = 2,
        ["seas_cannon_turtle"] = 2, ["seas_rex"] = 3, ["seas_gigantosaurus"] = 2,
    };
    /// <summary>does the cheat manager spawn units for this player at all (CHT:139, 156): a skirmish map (not a
    /// CampaignAI player), difficulty 9; never for a co-op AI commander (an AI assist: its cheater is disabled, CM:495)</summary>
    public bool CheatSpawnAllowed => Multimap && !Custommap && Difficulty == 9 && !CoopAlly;
    /// <summary>CAiCheatMgr.SpawnUnit on a skirmish map (see above) -> the new unit, or null</summary>
    public Unit? CheatSpawn(Census S, string cls, string? flag, int level, Pt? at)
    {
        var p = P;
        if (!CheatSpawnAllowed || at == null) return null;
        bool mirage = D.Extra.Mod == "MIRAGE";
        if (CHEAT_TRADE.Contains(cls) || (mirage && cls == "seas_trade_dino")) return null;
        if (CHEAT_BOATS.Contains(cls) || (mirage && cls == "seas_carrier")) return null;
        if (D.Def(cls, p) is not { } def || (def.Unique && p.Heroes.Has(cls))) return null;
        int lv = level > 0 ? level : (CHEAT_LEVEL.TryGetValue(cls, out var pl) ? pl : 0) + 1;
        lv = Math.Clamp(Math.Max(lv, D.MinLevel(cls, p)), 1, 5);
        var pyr = p.Pyramid ?? D.Pyramid;
        if (!(pyr[lv - 1] + CHEAT_EXTRA[lv - 1] > p.AtLevel[lv - 1])) return null;
        if (CHEAT_WORKERS.Contains(cls) && S.Workers.Count >= 15) return null;
        var R0 = Resolve("CHTR/" + cls, "");
        if (R0 == null || R0.Kind != "unit") return null;
        Building? best = null; double bd = double.MaxValue;
        foreach (var pr in ProducersOf(S, R0.Action))
        {
            if (pr is not Building b || b.Hp < 5) continue;
            var why = D.Check(b.RulesOwner(), LocalAction(b, R0.Action), b);
            if (why == "hidden" || why == "disabled") continue;
            double d = at.Value.Dist(b);
            if (d < bd) { bd = d; best = b; }
        }
        if (best == null) return null;
        var g = S.Home != null ? GuardPoint(S) : at.Value;
        var nv = W.NavFor(false, Unit.AMPHIBIOUS.IsMatch(cls));
        var (x, z) = best.SpawnPoint(g.X, g.Z, nv);
        var u = W.SpawnUnit(cls, p, x, z, lv, Geo.HeadingTo(g.X - x, g.Z - z));
        if (u == null) return null;
        if (flag != null && flag != cls) { var up = Acts(u).FirstOrDefault(y => y.Kind == "Upgrades" && y.Id == flag); if (up != null) W.CompleteUpgrade(u, up); }
        best.SpawnAnim();
        W.Emit("spawnfx", new Args { ["unit"] = u });
        var (qx, qz) = best.ExitPoint(g.X, g.Z, nv);
        W.Order(u, new Order("move") { X = qx, Z = qz, Auto = true });
        return u;
    }
    /// <summary>a scripted wave is as big as its table says: the fixed number, else by the campaign difficulty of the slot</summary>
    int ScriptedCount(ArmyRow e) => Scaled(e.Min == e.Max ? (int)e.Min : SquadCount(e));
    /// <summary>a scripted wave's count with the co-op campaign's enemy scaling (WaveScale, docs/COOP.md)</summary>
    int Scaled(int n) => WaveScale == 1 || n <= 0 ? n : Math.Max(n, (int)Math.Round(n * WaveScale, MidpointRounding.AwayFromZero));
    /// <summary>landing (ship_land): the wave boards transports at the water nearest to the spawn position and sails to
    /// the landing point; -> false when there is no water or no transport class (the wave then walks)</summary>
    bool Embark(Attack a, List<Unit> units, Pt from, Pt to)
    {
        var p = P;
        if (W.WaterNav == null) return false;
        var wn = W.WaterNav;
        var cls = TransportClass();
        if (cls == null) return false;
        int c0 = wn.NearestFree(wn.Idx(from.X, from.Z), 60), c1 = wn.NearestFree(wn.Idx(to.X, to.Z), 40);
        if (wn.Block[c0] != 0 || wn.Block[c1] != 0) return false;
        var (sx, sz) = wn.Center(c0); var (tx, tz) = wn.Center(c1);
        a.Ship = new List<Unit>();
        a.Land = new Pt(tx, tz);
        Unit? ship = null;
        foreach (var u in units)
        {
            if (ship == null || (ship.Passengers?.Count ?? 0) >= W.Capacity(ship))
            {
                ship = W.SpawnUnit(cls, p, sx + a.Ship.Count * 6, sz, 1, 0);
                if (ship == null) return a.Ship.Count > 0;
                a.Ship.Add(ship);
                Claims.Set(ship, a);
            }
            if (W.CanBoard(u, ship)) W.EnterTransport(u, ship);
        }
        Hands.Order(p, a.Ship, new Order("move") { X = tx, Z = tz, Auto = true });
        return true;
    }
    void AttackSail(Census S, Attack a, List<Unit> us)
    {
        var ships = (a.Ship ?? new List<Unit>()).Where(s => s.Alive).ToList();
        if (ships.Count == 0) { a.Ship = null; if (us.Any(u => !u.Inside)) AttackGo(S, a, us.Where(u => !u.Inside).ToList()); else a.End("dead"); return; }
        var land = a.Land!.Value;
        int to = (a.Pos ?? a.Aim) is { } am ? AimIsland(am, a) : -1;                   // (the land of the landing)
        foreach (var s in ships)
        {
            double d = DistP(s, land);
            // there: near the landing, or stopped (SP "swim": a ship that has not moved since the last think has arrived)
            // with the target's land at hand - one stopped short of it sails on instead of waiting out the four minutes
            var shore = W.ShorePoint(s, 30);
            bool stopped = s.Task.Type == "idle";
            if (d < 18 || (stopped && (d < 60 || (shore is { } sp && to >= 0 && W.LandIsland(sp.X, sp.Z) == to))) || W.Time - a.StateT > 240)
            {
                if (shore != null) { Hands.Order(P, new[] { s }, new Order("unload")); s.SilentDeath = true; Claims.Delete(s); W.Kill(s, null); }
                else if (stopped) { double ox = (R.Next() - 0.5) * 20, oz = (R.Next() - 0.5) * 20; Hands.Order(P, new[] { s }, new Order("move") { X = land.X + ox, Z = land.Z + oz, Auto = true }); }
            }
            else if (stopped) Hands.Order(P, new[] { s }, new Order("move") { X = land.X, Z = land.Z, Auto = true });
        }
        if (!(a.Ship ?? new List<Unit>()).Any(s => s.Alive)) { a.Ship = null; var outp = a.Alive().Where(u => !u.Inside).ToList(); if (outp.Count == 0) { a.End("dead"); return; } AttackGo(S, a, outp); }
    }
    /// <summary>which arms an automatic attack may use (AIFT custom_attack = 0)</summary>
    public sealed class AutoUse { public bool Ranged = true, Melee = true, Infantry = true, Cavalry = true, Vehicles = true, Ships; }
    /// <summary>custom_attack = 0: a squad picked against the targets from the arms the trigger allows. -> id | null</summary>
    public int? StartAutoAttack(List<Entity> targets0, AutoUse? use = null)
    {
        use ??= new AutoUse();
        var targets = targets0.Where(e => e != null && e.Alive).ToList();
        if (targets.Count == 0) return null;
        var S = Census(true);
        if (S.Base == null) return null;
        bool IsRanged(Unit u) => u.Weapons.Long?.Projectile != null;
        bool Ok(Unit u) => (IsRanged(u) ? use.Ranged : use.Melee) && (u.Cls == "CHTR" ? use.Infantry : u.Cls == "ANML" ? use.Cavalry : use.Vehicles);
        var pool = S.Pool.Where(Ok).OrderByDescending(x => x.Level).ToList();
        double need = 2;
        foreach (var e in targets) if (e is Unit tu && !tu.IsWorker) need += tu.Level * 1.2;
        var units = new List<Unit>();
        double sc = 0;
        foreach (var u in pool) { if (sc >= need && units.Count >= 2) break; units.Add(u); sc += u.Level; }
        if (units.Count == 0) return null;
        var a = new Attack(this, "auto", "quick", targets: targets, scripted: true, onTheWay: true);
        Attacks.Add(a);
        Launched.Add(new LaunchLog { Id = a.Id, T = Math.Floor(W.Time + 0.5), Type = "auto", Goal = "quick", Scripted = true, N = units.Count });
        a.Add(units);
        AttackGather(S, a);
        return a.State == "over" ? null : a.Id;
    }
}
