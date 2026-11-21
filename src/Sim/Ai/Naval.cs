// The computer player at sea (docs/spec/ai_group6.md §4): attacks on the enemy's ships and harbours
// (goals/AiGoalAttackShips.usl, the strategist DA:951-958, 1534, 1884-1901) and squads that cross the water in
// transports (goals/AiGoalWatercrossing.usl, AiGoalGeneralAttack.usl:236-365, Modules/AiModuleArea.usl:248-272).
//
//   * Ship attack: on maps with water, from epoch 3, when an enemy owns a harbour and the AI knows enemy ships or
//     harbours, 6 % of the strategist's thinks (not in Single 01 / 02 / 10, Single 03 player 1) - before the plan's
//     entry. The squad: its idle warships, else the army ShipAttack_<epoch 2-4> trained at its harbour. The targets
//     nearest its start location first, ships before harbours (AS:101-110 meant that; the original's test makes
//     every building rank lower); within 40 m it fights; done, the ships go back to the harbour.
//   * Crossing (a squad's target on another land - World.LandIsland - or, on skirmish water maps with harbours, one
//     attack in nine of a tribe other than Aje as a flank by sea; the levels' own rules first; GA:236-290): the tribe's
//     transport (WC:646-655), ceil(n / 10) of them, idle ones first, else trained at the harbour (2 minutes at most;
//     none to be had: the transfer fails at once); the crossing thinks every 10 + r ticks (WC:80); the squad boards at
//     once (BoardTransporter: a ship comes to the shore near them, an amphibious transport to the squad; again every
//     7th think, 7 rounds at most), sails to the water off the enemy's village and gets out at the shore within 30 m;
//     then the attack goes on by land and the transports go home. At most 5 crossings at once (AM:75). A transport lost
//     or attacked while boarding: everybody out at home and the attack ends. (docs/AI.md "Transports that stood in
//     place")
//   * Units left on another land (idle for 10 area thinks) are fetched home the same way (AM:140-210).
//
// Ported from remake/src/game/ai/naval.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.World;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Ai;

/// <summary>a crossing of an attack: 'ferry' state (boarding, sailing, landing)</summary>
public sealed class Ferry
{
    public string Cls = "";
    public Pt Land, Aim;
    public List<Unit> Ships = new();
    public int Round, N, BoardN, Queued;
    public double T0, BoardT = double.PositiveInfinity, SailT;
    public string Phase = "ships";
    public bool Ordered, Lost;
    /// <summary>the hit points when the boarding began</summary>
    public Dictionary<Unit, double>? Hp;
}

public sealed partial class TribeAI
{
    static readonly Regex NO_SHIP_ATTACK = new("^Single (01|02|10)$", RegexOptions.Compiled);
    static readonly string[] TRANSPORTS = { "hu_transport_ship", "aje_transport_turtle", "ninigi_transport_boat", "seas_hovercraft" };
    static readonly Regex HARBOUR = new("harbour|seas_carrier", RegexOptions.Compiled);
    const int MAX_CROSSINGS = 5;
    OrderedMap<Unit, int>? _stranded;

    // ------------------------------------------------------------------ the ship attack
    /// <summary>DisturbAttack: true when a ship attack was asked for (the strategist stops there)</summary>
    bool ShipAttackCheck(Census S)
    {
        if (W.WaterNav == null || S.Age < 3 || NO_SHIP_ATTACK.IsMatch(LevelName ?? "") || (LevelName == "Single 03" && P.Id == 1)) return false;
        var targets = EnemyNaval();
        if (!targets.Any(e => e is Building) || Rnd(100) > 5) return false;
        return RequestShipAttack(S, targets) != null;
    }
    /// <summary>enemy ships and harbours the AI can see</summary>
    List<Entity> EnemyNaval()
    {
        var outp = new List<Entity>();
        foreach (var u in W.Units) if (u.Alive && u.Naval && !u.Inside && IsEnemyEntity(u) && !W.HiddenFrom(u, P)) outp.Add(u);
        foreach (var b in W.Buildings) if (b.Alive && IsEnemyEntity(b) && HARBOUR.IsMatch(b.Name)) outp.Add(b);
        return outp;
    }
    Attack? RequestShipAttack(Census S, List<Entity> targets)
    {
        var plan = Attacks.Where(a => !a.Scripted && !a.Hunt).ToList();
        if (Defending || plan.Any(a => a.Goal == "ship") || plan.Count >= 3) return null;
        var at = new Attack(this, "ship", "ship", "ShipAttack_" + Math.Max(2, Math.Min(4, S.Age)), targets[0].Owner, targets);
        Attacks.Add(at);
        Launched.Add(new LaunchLog { Id = at.Id, T = Math.Floor(W.Time + 0.5), Type = "ship", Goal = "ship", Scripted = false, Hunt = false, N = 0 });
        at.Set("fleet");
        at.BuildUntil = W.Time + 120;
        ShipAttackThink(S, at);
        return at.State == "over" ? null : at;
    }
    static bool IsWarship(Unit u) => u.Alive && u.Naval && !u.Stationary && !u.CannotFight && !(u.Def != null && u.Def.TransportClass >= 2) && !u.Name.Contains("fishing");
    Building? HarbourOf(Census S) => S.Blds.FirstOrDefault(b => b.Built && HARBOUR.IsMatch(b.Name));
    /// <summary>AttackShips' Think: new -> (train) -> walk -> fight</summary>
    void ShipAttackThink(Census S, Attack a)
    {
        var ships = a.Alive();
        if (a.State == "fleet")
        {
            foreach (var u in S.Fleet) if (IsWarship(u) && !Claims.Has(u) && ships.Count < 8) { a.Add(new[] { u }); ships.Add(u); }
            if (ships.Count == 0 && !a.Ordered)
            {
                a.Ordered = true;
                var army = Data.Army(P.Tribe, Cfg?.ValueTable ?? "Dodo", a.ArmyName!) ?? new List<ArmyRow>();
                foreach (var e in army) for (double n = Math.Max(1, e.Min); n > 0; n--)
                {
                    var T = TrainableOf(S, e.Alternatives[0].Cls);
                    if (T != null && T.Producer != null && ArmyCanPay(S, T.Action.Cost)) Hands.QueueAction(P, T.Producer, T.Action);
                }
            }
            if (ships.Count == 0) { if (W.Time > a.BuildUntil) a.End("no ships"); return; }
            a.StartCount = ships.Count;
            var log = Launched.FirstOrDefault(l => l.Id == a.Id);
            if (log != null) { log.N = ships.Count; log.Go = Math.Floor(W.Time + 0.5); }
            a.Set("walk");
        }
        if (ships.Count == 0) { a.End("dead"); return; }
        var st = Start ?? S.Home!.Value;
        a.Targets = (a.Targets ?? new List<Entity>()).Where(e => e.Alive).ToList();
        if (a.Targets.Count == 0) a.Targets = EnemyNaval();
        // ships before buildings, the nearest to the start location first
        a.Targets = a.Targets.OrderBy(x => x is Building ? 1 : 0).ThenBy(x => st.Dist(x)).ToList();
        var tgt = a.Targets.FirstOrDefault();
        if (tgt == null)
        {
            a.End("done");
            var h = HarbourOf(S);
            if (h != null) Hands.Order(P, ships, new Order("move") { X = h.Pos.X, Z = h.Pos.Z, Auto = true });
            return;
        }
        var c = Attack.Centre(ships);
        if (a.State == "walk" && c.Dist(tgt) > 40)
        {
            foreach (var u in ships) if (u.Task.Type != "attack") Hands.Order(P, new[] { u }, new Order("attackmove") { X = tgt.Pos.X, Z = tgt.Pos.Z, Auto = true });
            return;
        }
        a.Set("fight");
        foreach (var u in ships) if (!(u.Task.Type == "attack" && u.Task.Target != null && u.Task.Target.Alive)) Hands.Order(P, new[] { u }, Order.Attack(tgt));
    }

    // ------------------------------------------------------------------ crossing the water
    /// <summary>does a squad going to aim cross the water? 1 = by ship, 0 = by land, -1 = the attack fails (GA:236-290
    /// TransferUnits: the levels' own rules first, then the islands - the squad's own land: units fetched home from
    /// another land start there, AM:140-210 - and the flank by sea of skirmish water maps; "Timeout reached!" for a
    /// way a level forbids)</summary>
    /// <summary>the land an attack's aim is on (World.LandIsland), the same for every step of a crossing: where the aim's
    /// own cell is no land (-1: in the water - a harbour -, on a cliff, a start location on a building's levelled
    /// ground), the land nearest to it within 24 m, else the land of the attack's first living target, else the land
    /// within 60 m. (Before, -1 meant "no crossing": a co-op wave whose aim cell was bad walked at an island it could not
    /// reach, and one whose aim cell was good took a ferry - base 06's raiders' cove, docs/AI.md "Transports that stood in place", 10.)</summary>
    int AimIsland(Pt aim, Attack? a)
    {
        int to = W.LandIslandNear(aim.X, aim.Z, 24);
        if (to >= 0) return to;
        if (a?.Targets != null)
            foreach (var t in a.Targets)
                if (t.Alive && W.LandIslandNear(t.Pos.X, t.Pos.Z, t.Radius + 16) is var l and >= 0) return l;
        return W.LandIslandNear(aim.X, aim.Z, 60);
    }
    int NeedsCrossing(Census S, Attack a, Pt aim, List<Unit> us)
    {
        if (W.WaterNav == null || a.Goal == "ship" || a.Crossed) return 0;
        var on = us.Where(u => u.Alive && !u.Inside).ToList();
        var c = on.Count > 0 ? Attack.Centre(on) : S.Home!.Value;
        int from = W.LandIsland(c.X, c.Z);
        if (from < 0) from = W.LandIsland(S.Home!.Value.X, S.Home.Value.Z);
        int to = AimIsland(aim, a);
        string lvl = LevelName ?? ""; int id = P.Id;
        bool mirage = D.Extra.Mod == "MIRAGE";
        bool ship;
        if (lvl == "Single 08" && id == 2) ship = false;
        else if (lvl == "Single 08" && id == 1 && Rnd(2) == 0) ship = true;              // (iType = 1 + random % 2 == 1)
        else if (lvl == "Single 06" && (id == 1 || id == 2)) ship = false;
        else if (lvl == "Single 01") ship = false;
        else if (mirage && lvl == "Single B43" && (Multimap ? id == 2 : id == 6) && Rnd(2) == 0) ship = true;
        else if (from >= 0 && to >= 0 && from != to) ship = true;
        else ship = Multimap && (!mirage || !Custommap) && MapOptions.Watermap && MapOptions.Harbour && GameType != "KingOfTheHill" && GameType != "Defender"
            && P.Tribe != "Aje" && !a.Scripted && !a.Hunt && a.Enemy != null && Rnd(100) <= 10;
        if (lvl == "Single 09" && id == 3 && ship) return -1;
        if (lvl == "Single 15" && ship && (id == 1 || id == 2)) return -1;
        if (lvl == "Single 15" && !ship && !Multimap && (id == 3 || id == 4)) return -1;
        if (lvl == "Single 15" && !ship && Multimap && mirage && (id == 5 || id == 6)) return -1;
        return ship ? 1 : 0;
    }
    /// <summary>the tribe's transport: the class of the water crossing goal and of the scripted landings (WC:646-655,
    /// SP:476-487 - both configurations: hu_transport_ship, aje_transport_turtle, ninigi_transport_boat, seas_hovercraft);
    /// a tribe without one (a mod's own tribe): its first transport of class 2 at sea. (The scan came first: MIRAGE's
    /// Aje took the liopleurodon - Miyagi's special ship with 4 places - and of a squad of ten six stood at the shore
    /// for the seven boarding rounds and were left behind.)</summary>
    string? TransportClass()
    {
        var p = P;
        var own = TRANSPORTS.FirstOrDefault(n => D.Exists(n) && n.StartsWith(p.Tribe.ToLowerInvariant(), StringComparison.Ordinal));
        if (own != null) return own;
        foreach (var act in D.Actions(p))
        {
            var o = ObjOf(act);
            if (o == null) continue;
            var df = D.Def(o, p);
            if (df != null && df.TransportClass == 2 && D.Info(o)?.Type == "SHIP") return o;
        }
        return null;
    }
    /// <summary>the water off the target's shore nearest aim: a spot ships can reach with the target's own land within
    /// 14 m towards aim (not a shore of another island on the way), and no enemy water mine, water turret or tower
    /// within 30 m of it (WC: the landing is given up near those)</summary>
    Pt? LandingFor(Pt aim, Attack? a = null)
    {
        var wn = W.WaterNav;
        if (wn == null) return null;
        int to = AimIsland(aim, a);
        var danger = new List<Pt>();
        foreach (var u in W.Units) if (u.Alive && Regex.IsMatch(u.Name, "mineship_mine|water_turret") && IsEnemyEntity(u)) danger.Add(Pt.Of(u));
        foreach (var b in W.Buildings) if (b.Alive && b.Weapons.Long != null && IsEnemyEntity(b)) danger.Add(Pt.Of(b));
        for (double r = 8; r <= 160; r += 6)
        {
            Pt? best = null; double bd = double.PositiveInfinity;
            for (int i = 0; i < 48; i++)
            {
                double t = i / 48.0 * Math.PI * 2, x = aim.X + SMath.Cos(t) * r, z = aim.Z + SMath.Sin(t) * r;
                if (wn.Block[wn.Idx(x, z)] != 0) continue;
                double dx = (aim.X - x) / r, dz = (aim.Z - z) / r;
                bool land = false;
                for (double s = 4; s <= 14 && !land; s += 2) { double qx = x + dx * s, qz = z + dz * s; if (!W.IsWater(qx, qz, 0.4) && (to < 0 || W.LandIsland(qx, qz) == to)) land = true; }
                if (!land || danger.Any(p => Hyp(p.X - x, p.Z - z) < 30)) continue;
                double d = Hyp(x - aim.X, z - aim.Z);
                if (d < bd) { bd = d; best = new Pt(x, z); }
            }
            if (best != null) return best;
        }
        return null;
    }
    /// <summary>co-op waves (CoopRuntimeEnemies: a wave spawned at a camp that cannot walk to its seat): the landing of a
    /// scripted landing (AttackRequest.ShipLand - transports spawned with the wave, as the original's scripted landings):
    /// the water off the aim's land nearest to it (LandingFor), when this tribe has a transport and the water at `from`
    /// is the same water; null: no way by boat</summary>
    public Pt? WaveLanding(Pt from, Pt aim)
    {
        var wn = W.WaterNav;
        if (wn == null || TransportClass() == null) return null;
        int c0 = wn.NearestFree(wn.Idx(from.X, from.Z), 60);
        if (wn.Block[c0] != 0) return null;
        var land = LandingFor(aim);
        if (land == null) return null;
        int c1 = wn.NearestFree(wn.Idx(land.Value.X, land.Value.Z), 40);
        if (wn.Block[c1] != 0 || (wn.Region is { } rg && rg[c0] != rg[c1])) return null;
        return land;
    }
    /// <summary>the crossing of an attack: 'ferry' state (boarding, sailing, landing)</summary>
    bool StartCrossing(Census S, Attack a, List<Unit> us, Pt aim)
    {
        int crossings = Attacks.Count(x => x.FerryState != null && x.State == "ferry");
        if (crossings >= MAX_CROSSINGS) return false;
        var h = S.Home!.Value;
        if (Multimap && !MapOptions.Harbour && W.LandIsland(h.X, h.Z) != AimIsland(aim, a)) { a.End("no harbour"); return true; }
        var L0 = LandingFor(aim, a);
        var cls = TransportClass();
        if (L0 == null || cls == null) return false;
        a.FerryState = new Ferry { Cls = cls, Land = L0.Value, Aim = aim, T0 = W.Time, Phase = "ships" };
        a.Set("ferry");
        FerryThink(S, a, us);
        return true;
    }
    /// <summary>the crossings' think (FerryWait: the water crossing goal thinks every 10 + r ticks, WC:80 - 2.2-3.6 s at
    /// every difficulty; it ran with the attack goals' think, every 5 s from Medium down, and its boarding rounds of
    /// seven thinks took 35 s)</summary>
    void FerryThinks()
    {
        var S = Census();
        foreach (var a in Attacks.ToList())
        {
            if (a.State != "ferry" || a.FerryState == null) continue;
            if (W.Time - a.T0 > TIME_LIMIT) { FerryDone(a); a.End("timeout"); continue; }
            FerryThink(S, a, a.Alive());
        }
    }
    /// <summary>a transport of the crossing's class this player has, free: not claimed, not locked, empty, outside</summary>
    bool FreeTransport(Unit u, string cls) => u.Alive && u.Name == cls && !u.Inside && !Claims.Has(u) && !Locked.Has(u) && !(Crew != null && Crew.Has(u)) && (u.Passengers?.Count ?? 0) == 0;
    /// <summary>places per transport: groups of ten (WC:170-230), fewer when it has fewer</summary>
    int Places(Unit s) => (int)Math.Max(1, Math.Min(10, W.Capacity(s) > 0 ? W.Capacity(s) : 10));
    void FerryThink(Census S, Attack a, List<Unit> us)
    {
        var F = a.FerryState!;
        F.N++;
        F.Ships = F.Ships.Where(s => s.Alive).ToList();
        var aboard = us.Where(u => u.Inside && F.Ships.Any(s => s.Passengers != null && s.Passengers.Contains(u))).ToList();
        switch (F.Phase)
        {
            case "ships":
            {
                // ceil(n / 10) transports, idle ones first (WC:634-721): the whole census, not only the fleet - the SEAS
                // hovercraft is a vehicle (it never was in the fleet: every SEAS crossing waited two minutes and gave up)
                int Need() { int n = 0, left = us.Count; foreach (var s in F.Ships) left -= Places(s); if (left <= 0) return F.Ships.Count; n = F.Ships.Count + (int)Math.Ceiling(left / 10.0); return n; }
                var c = us.Count > 0 ? Attack.Centre(us) : S.Home!.Value;
                foreach (var u in S.Units.Where(u => FreeTransport(u, F.Cls)).OrderBy(u => u.Task.Type == "idle" ? 0 : 1).ThenBy(u => c.Dist(u)).ToList())
                {
                    if (F.Ships.Count >= Need()) break;
                    // (a transport does not stop to fight on the way: the Aje's turtle bites)
                    F.Ships.Add(u); Claims.Set(u, a); u.FerryStance = u.Stance; u.Stance = 3;
                }
                int need = Need();
                if (F.Ships.Count >= need || (F.Ships.Count > 0 && W.Time - F.T0 > 120)) { F.Phase = "board"; F.Round = 0; F.BoardN = 0; F.BoardT = W.Time; F.Hp = F.Ships.ToDictionary(s => s, s => s.Hp); goto case "board"; }
                // the missing ones are trained (again each think until they are queued: one that could not be paid the
                // first time was never asked for again); none and none to be had (no producer, not allowed): the transfer
                // fails at once (WC:714-751) instead of the squad standing two minutes for nothing - the SEAS get a
                // hovercraft for free then (WC:737-751, /AI_Help), for their next crossing
                string? why = null;
                for (int i = F.Ships.Count + F.Queued; i < need; i++)
                {
                    var T = TrainableOf(S, F.Cls);
                    if (T == null || T.Producer == null) { why = T?.Why ?? "none"; break; }
                    if (!ArmyCanPay(S, T.Action.Cost)) { why = "cost"; break; }
                    if (Hands.QueueAction(P, T.Producer, T.Action) != null) { why = "busy"; break; }
                    F.Queued++;
                }
                if (F.Ships.Count == 0 && F.Queued == 0 && why is "none" or "req")
                {
                    if (P.Tribe == "SEAS" && F.Cls == "seas_hovercraft" && S.CountOf("seas_hovercraft") == 0)
                    {
                        var at = (Entity?)HarbourOf(S) ?? S.Base;
                        var g = at != null ? Pt.Of(at) : S.Home!.Value;
                        W.SpawnUnit(F.Cls, P, g.X + (R.Next() - 0.5) * 8, g.Z + (R.Next() - 0.5) * 8, null, 0);
                    }
                    FerryDone(a); a.End("no transports"); return;
                }
                if (W.Time - F.T0 > 120) { FerryDone(a); a.End("no transports"); }
                return;
            }
            case "board":
            {
                // a transport lost or attacked while boarding: everybody out at home (WC:138 OnShipAttacked, 444-447) - hit
                // since the boarding began (lastHit: healed at home, a small hit is gone by the next think), or with
                // fewer hit points than then
                F.Hp ??= F.Ships.ToDictionary(s => s, s => s.Hp);          // (the hit points when the boarding began)
                bool hit = F.Ships.Any(s => W.LastHitOf(s) >= F.BoardT || s.Hp < (F.Hp.TryGetValue(s, out var h0) ? h0 : s.Hp));
                if (F.Lost || hit) { FerryUnload(a); FerryDone(a); a.End(F.Lost ? "transport lost" : "transport attacked"); return; }
                var outp = us.Where(u => !u.Inside).ToList();
                if (outp.Count == 0) { FerrySail(F); return; }
                // Board() at once, then again every 7th think while somebody is still out; after 7 rounds the transfer
                // fails, or sails with those aboard (WC:437-469)
                if (F.BoardN++ % 7 == 0)
                {
                    if (++F.Round > 7)
                    {
                        if (aboard.Count == 0) { FerryDone(a); a.End("boarding"); return; }
                        // who is still out is not left standing at the shore: back to the pool
                        foreach (var u in outp) a.Drop(u);
                        Release(outp); SendHome(outp);
                        FerrySail(F); return;
                    }
                    // an amphibious transport (hovercraft, turtle) drives to the squad: it may wait in the water or on
                    // another island, where nobody walks to it (a ship comes to the waterline by itself: BoardShipUpdate)
                    var sc = Attack.Centre(outp);
                    foreach (var s in F.Ships) if (s.Amphib && s.Task.Type is "idle" or "hold" && sc.Dist(s) > 20) Hands.Order(P, new[] { s }, new Order("move") { X = sc.X, Z = sc.Z, Auto = true });
                    // groups of ten per transport (WC:170-230), as many as it has places
                    var room = F.Ships.Select(s => Places(s) - (s.Passengers?.Count ?? 0)).ToArray();
                    var going = F.Ships.Select(s => 0).ToArray();
                    foreach (var u in outp)
                    {
                        // still on its way to a transport with room: let it walk on
                        if (u.Task.Type == "board" && u.Task.Target is Unit bt && F.Ships.IndexOf(bt) is var bi and >= 0 && going[bi] < room[bi] && W.CanBoard(u, bt)) { going[bi]++; continue; }
                        int k = -1;
                        for (int i = 0; i < F.Ships.Count; i++) if (going[i] < room[i] && W.CanBoard(u, F.Ships[i])) { k = i; break; }
                        if (k < 0) continue;
                        going[k]++;
                        Hands.Order(P, new[] { u }, new Order("board") { Target = F.Ships[k], Auto = true });
                    }
                }
                return;
            }
            case "sail":
            {
                // every transport lost on the way: the crossing is over (FerryDone clears it), who got out fights on
                if (F.Ships.Count == 0) { var left = a.Alive().Where(u => !u.Inside).ToList(); FerryDone(a); if (left.Count == 0) { a.End("dead"); return; } a.Crossed = true; AttackGo(S, a, left); return; }
                int to = AimIsland(F.Aim, a);
                foreach (var s in F.Ships)
                {
                    if ((s.Passengers?.Count ?? 0) == 0) continue;
                    double d = DistP(s, F.Land);
                    var shore = W.ShorePoint(s, 30);
                    // there: within 32 m of the landing (WC:478-487), or stopped (the original takes a ship that has not moved
                    // since its last think as arrived, SP "swim") with the target's land at hand - a ship stopped short of it
                    // sails on instead of waiting out the five minutes
                    bool stopped = s.Task.Type == "idle";
                    bool there = d < 32 || (stopped && (d < 70 || (shore is { } sp && (to < 0 || W.LandIsland(sp.X, sp.Z) == to)))) || W.Time - F.SailT > 300;
                    if (there && shore != null) Hands.Order(P, new[] { s }, new Order("unload"));
                    else if (stopped) { double ox = there ? (R.Next() - 0.5) * 24 : 0, oz = there ? (R.Next() - 0.5) * 24 : 0; Hands.Order(P, new[] { s }, new Order("move") { X = F.Land.X + ox, Z = F.Land.Z + oz, Auto = true }); }
                }
                if (F.Ships.Any(s => (s.Passengers?.Count ?? 0) > 0)) return;
                var landed = a.Alive().Where(u => !u.Inside).ToList();
                FerryDone(a);
                if (landed.Count == 0) { a.End("dead"); return; }
                a.Crossed = true;
                AttackGo(S, a, landed);
                return;
            }
        }
    }
    void FerrySail(Ferry F)
    {
        F.Phase = "sail";
        Hands.Order(P, F.Ships.Where(s => (s.Passengers?.Count ?? 0) > 0).ToList(), new Order("move") { X = F.Land.X, Z = F.Land.Z, Auto = true });
        F.SailT = W.Time;
    }
    /// <summary>everybody out at home (EmergencyUnboard, WC:347-377)</summary>
    void FerryUnload(Attack a) { foreach (var s in a.FerryState!.Ships) if (s.Alive && (s.Passengers?.Count ?? 0) > 0) Hands.Order(P, new[] { s }, new Order("unload")); }
    /// <summary>the transports go back to the harbour, free again; the attack's crossing is over (FerryState cleared: a
    /// crossing ended by a lost or attacked transport, a timeout or the attack's end kept it - and its transports
    /// claimed - before)</summary>
    internal void FerryDone(Attack a)
    {
        var F = a.FerryState;
        if (F == null) return;
        a.FerryState = null;
        var S = this.S ?? Census();
        var h = HarbourOf(S);
        foreach (var s in F.Ships)
        {
            Claims.Delete(s);
            if (s.FerryStance != null) { s.Stance = s.FerryStance.Value; s.FerryStance = null; }
            if (s.Alive && h != null) Hands.Order(P, new[] { s }, new Order("move") { X = h.Pos.X, Z = h.Pos.Z, Auto = true });
        }
    }
    /// <summary>a transport of a crossing died (ReadEvents)</summary>
    void FerryLost(Unit s) { foreach (var a in Attacks) if (a.FerryState != null && a.FerryState.Ships.Contains(s) && a.FerryState.Phase == "board") a.FerryState.Lost = true; }

    // ------------------------------------------------------------------ units left on another land (AM:140-210)
    void StrandedThink(Census S)
    {
        if (W.WaterNav == null || S.Home == null) return;
        var h = S.Home.Value;
        int home = W.LandIsland(h.X, h.Z);
        var st = _stranded ??= new OrderedMap<Unit, int>();
        var far = new List<Unit>();
        foreach (var u in S.Pool)
        {
            if (u.Task.Type != "idle" || u.Inside || W.LandIsland(u.Pos.X, u.Pos.Z) == home) { st.Delete(u); continue; }
            int n = st.Get(u) + 1;
            st.Set(u, n);
            if (n > 10) far.Add(u);
        }
        if (far.Count == 0 || Attacks.Any(a => a.Type == "home")) return;
        var group = far.Where(u => DistE(u, far[0]) < 128).ToList();
        var at = new Attack(this, "home", "quick", pos: S.Home, hunt: true);
        Attacks.Add(at);
        Launched.Add(new LaunchLog { Id = at.Id, T = Math.Floor(W.Time + 0.5), Type = "home", Goal = "quick", Scripted = false, Hunt = true, N = group.Count });
        at.Add(group);
        foreach (var u in group) st.Delete(u);
        AttackGo(S, at, group);
    }
}
