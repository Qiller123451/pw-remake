// The standing army of the computer player (CAiGoalMinistryOfDefense: one unit request at a time, chosen by the
// tribe's unit mix per epoch and pyramid level), level-ups (CAiGoalKindergarten) and special moves in a fight
// (CAiTaskAttackObject). docs/spec/ai.md §4.6, §8.5. TribeAI methods; S = the census.
//
// Ported from remake/src/game/ai/army.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Ai;

public sealed partial class TribeAI
{
    static readonly Regex NOT_ARMY_MIX = new("worker|resource_collector|_cart$|trade_dino|hovercraft|transport|tracker_dino|kennel|fishing_boat|scout$|smokebomb|drums$|siegetower", RegexOptions.Compiled);
    /// <summary>pyramid slots per level the standing army leaves free (MOD:129)</summary>
    static readonly int[] RESERVED = { 6, 5, 3, 1, 0 };

    Dictionary<string, List<MixGroup>>? _mix;
    string? _mixKey;
    /// <summary>the unit in production for the pool (MOD), the extra unit (FM:569)</summary>
    (Entity Producer, QueueItem? Item, double T)? _modJob;
    (Entity Producer, QueueItem? Item)? _xtraJob;
    int _modLevel = -1;
    /// <summary>the level / variant (ObjFlag) the mix wants a class at</summary>
    readonly Dictionary<string, int> _wantLevel = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _wantFlag = new(StringComparer.Ordinal);

    /// <summary>the unit mix of a tribe without a Units.txt: what its buildings can train in this epoch, grouped by the
    /// level a unit starts at; units of the current epoch weigh double</summary>
    public Dictionary<string, List<MixGroup>> DerivedMix(Census S)
    {
        var p = P; string key = S.Age + "|" + (p.Tt?.Version ?? 0);
        if (_mix != null && _mixKey == key) return _mix;
        var mix = new Dictionary<string, List<MixGroup>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var a in D.Actions(p))
        {
            if (a.Kind != "Build" || a.Cat == "Build/BLDG" || a.Results.Count == 0) continue;
            string cls = a.Results[0].Obj; var def = D.Def(cls, p); var info = D.Info(cls);
            if (def == null || info == null || info.Type == "SHIP" || def.Unique || def.CanHarvest != 0 || NOT_ARMY_MIX.IsMatch(cls)) continue;
            var ws = D.WeaponSetOf(cls, D.MinLevel(cls, p), p);
            if (ws.Long == null) continue;
            int ep = 1; bool ok = true;
            foreach (var r in a.Req) { var m = Regex.Match(r, @"^age_(\d)$"); if (m.Success) ep = Math.Max(ep, m.Groups[1].Value[0] - '0'); else if (!D.Invented(p, r, a.Tribe == "Special" ? p.Tribe : a.Tribe)) ok = false; }
            if (!ok || ep > S.Age) continue;
            int lv = Math.Max(D.StartLevel(a), D.MinLevel(cls, p));
            string k = "Level_" + lv;
            if (!mix.TryGetValue(k, out var l)) { mix[k] = l = new List<MixGroup>(); order.Add(k); }
            l.Add(new MixGroup { Weight = ep == S.Age ? 2 : 1, Units = { new MixUnit { Cls = cls } } });
        }
        _mix = mix; _mixKey = key;
        return mix;
    }
    /// <summary>the standing army's mix of this epoch: ai.json's, else (extraSquads) the derived one</summary>
    Dictionary<string, List<MixGroup>> MixOf(Census S) => Data.UnitMix(P.Tribe, S.Age) ?? DerivedMix(S);
    static List<MixGroup> Groups(Dictionary<string, List<MixGroup>> mix, int lv) => mix.TryGetValue("Level_" + lv, out var g) ? g : new List<MixGroup>();

    /// <summary>may the army spend this now? Requests of the build list that wait for resources come first, except while
    /// the army is a bare home guard (fewer than 3 + epoch fighters), during an alarm, and for a Dodo in its first epoch
    /// (who lives off its raids). The next worker always comes first.</summary>
    public bool ArmyCanPay(Census S, Cost cost)
    {
        var p = P;
        bool free = (Alarm != null && !Alarm.Animal) || S.Fighters.Count < ArmyFloor(S) || (Personality == "Dodo" && S.Age <= 1) || EconomyOff;
        var wk = WorkerCost != null && !DefendMode ? WorkerCost : null;
        foreach (var r in COSTS)
        {
            double c = cost[r];
            if (c > 0 && p[r] - c < Math.Max(free ? 0 : ResV(Reserve, r), wk != null ? wk[r] : 0)) return false;
        }
        return true;
    }
    /// <summary>The standing army below this many fighters may spend what the build list saves up: 3 + epoch (a bare home
    /// guard). On skirmish maps and for co-op allies from the third epoch 3 + 3 × epoch (the remake's own, 2026-11-11: the
    /// requests of epochs 4-5 - the next epoch, the inventory upgrades - reserve 1,500-2,500 of each resource for minutes;
    /// the Hu of a 20-minute AI-vs-AI game stood at 10-12 fighters through its fourth epoch with 1,900 food in store and
    /// lost its attacks one by one). The campaign's enemies keep the original's floor (their waves are the scripts').</summary>
    public int ArmyFloor(Census S) => (Multimap || CoopAlly) && S.Age >= 3 ? 3 + 3 * S.Age : 3 + S.Age;
    /// <summary>the action and a producer for a unit class -> (action, producer) | (action, why) | null</summary>
    public sealed record Trainable(ActionDef Action, Entity? Producer, string? Why);
    public Trainable? TrainableOf(Census S, string cls)
    {
        var p = P;
        var R0 = Resolve("CHTR/" + cls, "");
        if (R0 == null || R0.Kind != "unit") return null;
        string why = "none";
        foreach (var pr in ProducersOf(S, R0.Action).OrderBy(a => a.Queue.Count).ToList())
        {
            if (pr.Queue.Count >= 2) { if (why is "none" or "req") why = "busy"; continue; }
            var la = LocalAction(pr, R0.Action);
            var w = W.CanQueue(p, la, pr);
            if (w == null) return new Trainable(la, pr, null);
            // (the reason of the producer closest to making it: "level" / "cost" at one farm say more than "req" at
            // another in the wrong mode)
            if (why is "none" or "req" or "busy" || w is not "req") why = w;
            if (w == "req") Prerequisites(S, R0.Action, pr);
        }
        return new Trainable(R0.Action, null, why);
    }

    // ------------------------------------------------------------------ MinistryOfDefense.Think (MOD:1048-1253)
    void ArmyThink(Census S)
    {
        var p = P;
        if (Params.NoDefence) return;
        // the unit just trained moves up to the level the mix wants it at, when skulls allow
        Promote(S);
        if (_modJob is { } j) { if (j.Producer.Alive && j.Item != null && j.Producer.Queue.Contains(j.Item) && W.Time - j.T < 120) return; _modJob = null; }
        // a tribe without Units.txt (Hu, Ninigi: no settings folder in the installation): the original's ministry of
        // defence trains nothing (MOD:96, 1060-1137) - their army is the fight module's extra units (XtraThink). The mix
        // derived from the tech tree is the remake's own: skirmish and the campaign's Nightmare (extraSquads)
        var mix = Data.UnitMix(p.Tribe, S.Age) ?? (ExtraSquads ? DerivedMix(S) : null);
        if (mix == null) return;
        var pyr = W.PyramidFor(p);
        var reserved = Params.HighDefense ? new[] { 0, 0, 0, 0, 0 } : RESERVED;
        var lords = new HashSet<string>(S.Heroes.Where(h => h.Level >= 5).Select(h => h.Name), StringComparer.Ordinal);        // GetAvailableNPCs
        double Free(int lv) => pyr[lv - 1] - p.AtLevel[lv - 1] - p.QueuedAtLevel[lv - 1] - reserved[lv - 1];
        UnitNeed = null;
        ActionDef? noPlace = null;
        for (int k = 0; k < 5; k++)
        {
            _modLevel = (_modLevel + 1) % 5;
            int lv = _modLevel + 1;
            var groups = Groups(mix, lv);
            if (groups.Count == 0) continue;
            if (S.Age == 1 && S.Pool.Count(u => u.Level == lv) >= 5) continue;      // MOD:1087
            // the group whose share of the pool is furthest below its weight
            var cand = new List<(string Cls, string? Flag, double N, double W)>();
            double wSum = 0, nSum = 0;
            foreach (var g in groups)
            {
                string? cls = null, flag = null; double n = 0;
                foreach (var u in g.Units)
                {
                    n += S.Pool.Count(x => x.Name == u.Cls && x.Level <= lv);
                    if (u.Npc != null && !SpecialAllowed(u.Npc, lords)) continue;
                    cls = u.Cls; flag = u.ObjFlag;
                }
                if (cls == null) continue;
                double wt = g.Weight != 0 ? g.Weight : 1;
                cand.Add((cls, flag, n, wt));
                wSum += wt; nSum += n;
            }
            double ns = nSum != 0 ? nSum : 1;
            foreach (var c in cand.OrderBy(c => c.N / ns - c.W / wSum).ToList())
            {
                var T = TrainableOf(S, c.Cls);
                if (T != null && T.Producer == null && T.Why == "none" && noPlace == null) noPlace = T.Action;
                if (T == null || T.Producer == null) continue;
                int sl = W.UnitStartLevel(T.Action, p);
                if (Free(sl) <= 0) continue;
                UnitNeed = T.Action.Cost;
                if (!ArmyCanPay(S, T.Action.Cost)) return;                 // wait for the resources (the allocator)
                if (Hands.QueueAction(p, T.Producer, T.Action) != null) continue;
                _modJob = (T.Producer, T.Producer.Queue.LastOrDefault(), W.Time);
                _wantLevel[c.Cls] = Math.Max(lv, _wantLevel.TryGetValue(c.Cls, out var wl) ? wl : 0);
                if (c.Flag != null) _wantFlag[c.Cls] = c.Flag;
                if (T.Producer.Rally == null && S.Home != null) { var gp = GuardPoint(S); T.Producer.Rally = (gp.X, gp.Z); }
                return;
            }
        }
        // nothing of the mix can be trained and a unit of it has no place to come from: that building is asked for
        // (the planner's solution for a unit includes the building it is made at)
        if (noPlace != null && !EconomyOff)
        {
            var at = noPlace.Locs.Select(l => l.At).FirstOrDefault(n => D.Info(n) is { Type: "BLDG" });
            if (at != null && !S.Blds.Any(b => b.Name == at)) AddOnTop("BLDG/" + at);
        }
    }
    /// <summary>SpecialAllowed (MIRAGE AiGoalMinistryOfDefense.usl:1071-1167; the base game: the hero must be there,
    /// MOD:233): a mix row with a value - a hero's class, or MIRAGE's 'flyers_enabled' / 'titans_enabled' - is trained
    /// only when its hero is at level 3 or more (GetAvailableNPCs). MIRAGE first drops 'titans_enabled' rows when the
    /// titans are removed and 'flyers_enabled' rows - meant - when flying is off: its script tests RemoveTitans() there
    /// (:1072, a bug; docs/MODDER_NOTES.md §15); the remake tests FlyingEnabled. Without the option FreeSpecials (the
    /// remake does not have it; MIRAGE's default off) the hero test follows - which neither value ever passes, so with
    /// MIRAGE's defaults neither row is trained (as in MIRAGE: the free specials' age table :1080-1166 is never read).</summary>
    public bool SpecialAllowed(string npc, HashSet<string> lords)
    {
        if (npc == "flyers_enabled" && MirageAi && !OptFlying) return false;
        if (npc == "titans_enabled" && OptRemoveTitans) return false;
        return lords.Contains(npc);
    }

    // RequestXtraUnits (FM:569-607): with resources to spare (more than 200 food, 200 wood and 100 stone that no
    // request waits for) one more unit is trained on every fight think - the best the village can make, wherever the
    // pyramid has room (the reserved slots of the standing army do not apply). Not for the SEAS, not at difficulty 0-1.
    void XtraThink(Census S)
    {
        var p = P;
        if (p.Tribe == "SEAS" || Difficulty <= 1 || Params.NoDefence || EconomyOff) return;
        if (p["stone"] - ResV(Reserve, "stone") <= 100 || p["wood"] - ResV(Reserve, "wood") <= 200 || p["food"] - ResV(Reserve, "food") <= 200) return;
        // one extra unit in production at a time (FM:621: a request is dropped while the last one is still being made)
        if (!ExtraSquads && _xtraJob is { } xj && xj.Producer.Alive && xj.Item != null && xj.Producer.Queue.Contains(xj.Item)) return;
        var mix = MixOf(S);
        for (int lv = 5; lv >= 1; lv--)
        {
            // (a random order of the groups: the JS sorted by Math.random() - 0.5; a shuffle by the world's numbers)
            var groups = Groups(mix, lv).ToList();
            for (int i = groups.Count - 1; i > 0; i--) { int k = Rnd(i + 1); (groups[i], groups[k]) = (groups[k], groups[i]); }
            foreach (var g in groups)
            {
                var u = g.Units.FirstOrDefault(x => x.Npc == null);
                if (u == null) continue;
                var T = TrainableOf(S, u.Cls);
                if (T == null || T.Producer == null || T.Producer.Queue.Count > 0) continue;
                var c = T.Action.Cost;
                if (COSTS.Any(r => c[r] > 0 && p[r] - c[r] < ResV(Reserve, r))) continue;
                if (Hands.QueueAction(p, T.Producer, T.Action) == null) { _xtraJob = (T.Producer, T.Producer.Queue.LastOrDefault()); return; }
            }
        }
    }
    /// <summary>level a pool unit up to the level its mix row names, and give variants their upgrade (the ObjFlag)</summary>
    void Promote(Census S)
    {
        var p = P;
        if (_wantFlag.Count > 0)
        {
            foreach (var u in S.Pool)
            {
                if (!_wantFlag.TryGetValue(u.Name, out var f) || u.AiFlag != null || u.Queue.Count > 0) continue;
                var a = Acts(u).FirstOrDefault(x => x.Kind == "Upgrades" && x.Id == f);
                u.AiFlag = f;
                if (a != null && W.CanQueue(p, a, u) == null && ArmyCanPay(S, a.Cost)) Hands.QueueAction(p, u, a);
            }
        }
        if (Cls == "Easy") return;
        double spare = p["skulls"] - ResV(Reserve, "skulls");
        if (spare < D.LevelupSkulls[0]) return;
        Unit? best = null;
        foreach (var u in S.Pool)
        {
            if (!_wantLevel.TryGetValue(u.Name, out var want) || want == 0 || u.Level >= want || u.Task.Type == "attack") continue;
            if (best == null || u.Level < best.Level) best = u;
        }
        // a full pyramid row: one of its fighters moves up (inside the Kindergarten's ceilings) and makes room below - the
        // remake's own (skirmish, Nightmare); the original's only level-ups are the Kindergarten's
        if (best == null && ExtraSquads)
        {
            var pyr = W.PyramidFor(p);
            for (int lv = 1; lv <= 4 && best == null; lv++)
            {
                if (p.AtLevel[lv - 1] + p.QueuedAtLevel[lv - 1] < pyr[lv - 1] || p.AtLevel[lv] + p.QueuedAtLevel[lv] >= pyr[lv]) continue;
                best = S.Pool.FirstOrDefault(u => u.Level == lv && !u.Def!.Unique && u.Task.Type != "attack" && W.CanHaveLevel(u, lv + 1) && Data.MayLevel(u.Name, lv, R));
            }
        }
        if (best != null && W.SkullCost(best.Level, best.Level + 1) <= spare) Hands.LevelUp(p, best);
    }

    // ------------------------------------------------------------------ Kindergarten (KG:95-155): never on Easy
    // badly hurt fighters (30 % hit points or less) and heroes are levelled up - a level-up heals - within the
    // per-class ceilings, when the skulls are there
    void KindergartenThink(Census S)
    {
        var p = P;
        if (Cls == "Easy" || !Kindergarten) return;
        double skulls = p["skulls"] - ResV(Reserve, "skulls");
        if (skulls < D.LevelupSkulls[0]) return;
        foreach (var u in S.Fighters.OrderBy(a => a.Level).ToList())
        {
            if (u.Level >= 5 || u.Inside || Locked.Has(u)) continue;
            var t = u.Task.Type;
            if (t != "idle" && t != "attack" && t != "hold") continue;
            bool hero = u.Def!.Unique;
            if (!(u.Hp / u.MaxHp <= 0.3 || (hero && u.Hp / u.MaxHp < 0.6))) continue;
            if (W.SkullCost(u.Level, u.Level + 1) > skulls) continue;
            if (!hero && !Data.MayLevel(u.Name, u.Level, R)) continue;
            if (Hands.LevelUp(p, u) == null) return;
        }
    }

    // ------------------------------------------------------------------ special moves (AO:337-560)
    // self-cast ones when the enemies around are worth it (summed hit points > 500 within 60 m), targeted ones on the
    // unit's current target
    void MovesThink(Census S)
    {
        if (Cls == "Easy" && Multimap) return;
        int n = 0;
        foreach (var u in S.Fighters)
        {
            if (u.Task.Type != "attack" || u.Task.Target == null || !u.Task.Target.Alive || Locked.Has(u)) continue;
            if (++n > 40) break;
            var mv = W.MovesOf(u);
            if (mv.Count == 0) continue;
            foreach (var a in mv)
            {
                // the ninja picks the lock of the nearest closed enemy gate within sqrt(50) m of it (AiTaskAttackObject
                // CheckSpecialAttack "lockpicking", AO:621-645; MIRAGE 2.6.8 / commit 090b65c0 "Fixed a bug that prevented
                // Dragon Clan AI from lock picking gates": the gate found was never returned)
                if (a.Id == "lockpicking")
                {
                    if (u.Active("lockpicking") || W.MoveCheck(u, a) != null) continue;
                    if (LockpickGate(u) is { } gate) { Hands.UseMove(P, u, a, gate); break; }
                    continue;
                }
                if (!Moves.MOVES.TryGetValue(a.Id, out var M) || M.Auto != null || M.NoCooldown || W.MoveCheck(u, a) != null) continue;
                if (M.Self)
                {
                    double hp = 0;
                    W.UHash.Query(u.Pos.X, u.Pos.Z, 60, e => { if (e.Alive && IsEnemyEntity(e)) hp += e.Hp; });
                    if (hp > 500 && R.Next() < 0.5) Hands.UseMove(P, u, a);
                }
                else if (M.Target == "enemy" || M.Target == "ground") Hands.UseMove(P, u, a, u.Task.Target, (u.Task.Target.Pos.X, u.Task.Target.Pos.Z));
                break;
            }
        }
    }

    /// <summary>AO:621-645 "lockpicking": the nearest enemy gate that is not open ("_gate" class, GateState != 0) whose
    /// centre is within sqrt(50) m of the unit (the squared distance &lt;= 50), or null</summary>
    Building? LockpickGate(Unit u)
    {
        Building? best = null; double bd = 50;
        W.BHash.Query(u.Pos.X, u.Pos.Z, 8 + 16, b =>
        {
            if (!b.Alive || b.Def?.Gate != true || b.GateState == Moves.GATE_OPEN || !IsEnemyEntity(b)) return;
            double dx = b.Pos.X - u.Pos.X, dz = b.Pos.Z - u.Pos.Z, d2 = dx * dx + dz * dz;
            if (d2 <= bd) { bd = d2; best = b; }
        });
        return best;
    }
}
