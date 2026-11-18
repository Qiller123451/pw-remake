// Scenario tests of combat, effects, healing, finishing, items, game over and statistics (src/Sim/Systems): worlds on a
// flat map with units of every tribe stepped as the game steps them - fights to the end (deaths, the killer's skulls
// and kills, the statistics), ranged against melee, splash, towers and bunkers, auras, healers and temples, items,
// finishing moves, knockback, the debug options' factors, the end of a skirmish.
// Game data: PW_DATA / PW_DATA_MIRAGE (default /opt/data/base, /opt/data/mirage); model files: PW_MODELS (the staged
// export) - the tests that need clips or links skip without them.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys1CombatTests
{
    static readonly Dictionary<string, string> WARRIOR = new() { ["Hu"] = "hu_warrior", ["Aje"] = "aje_warrior", ["Ninigi"] = "ninigi_warrior", ["SEAS"] = "seas_warrior" };
    static readonly Dictionary<string, string> ARCHER = new() { ["Hu"] = "hu_archer", ["Aje"] = "aje_archer", ["Ninigi"] = "ninigi_archer", ["SEAS"] = "seas_warrior" };

    static List<string> TribesOf(Rules R) => new[] { "Hu", "Aje", "Ninigi", "SEAS" }.Where(t => R.Exists(WARRIOR[t])).ToList();
    static double Skulls(double scalps) => Math.Floor(TT.JsRound(scalps) * 1 + 0.5);

    // ------------------------------------------------------------------ fights between the tribes
    public static void FightEveryTribePairBase(TestContext t) => FightEveryTribePair(t, RulesTestData.Base(t));
    public static void FightEveryTribePairMirage(TestContext t) => FightEveryTribePair(t, RulesTestData.Mirage(t));

    /// <summary>4 warriors against 4 warriors of every other tribe (and 3 archers each behind them): the fight ends with one
    /// side gone; every death has its event and its killer; each side's skulls are the scalps of the enemies it killed;
    /// kills, losses and the statistics count the same</summary>
    static void FightEveryTribePair(TestContext t, string data)
    {
        var (R, M) = Sys1.Load(t, data);
        var tribes = TribesOf(R);
        int fights = 0;
        foreach (var a in tribes)
            foreach (var b in tribes)
            {
                if (string.CompareOrdinal(a, b) >= 0) continue;
                var W = Sys1.NewWorld(R, M, 256, (ulong)(fights + 7));
                var pa = Sys1.AddPlayer(W, R, 0, a); var pb = Sys1.AddPlayer(W, R, 1, b);
                var ua = new List<Unit>(); var ub = new List<Unit>();
                for (int i = 0; i < 4; i++) { ua.Add(W.SpawnUnit(WARRIOR[a], pa, -14, -6 + i * 4)!); ub.Add(W.SpawnUnit(WARRIOR[b], pb, 14, -6 + i * 4)!); }
                for (int i = 0; i < 3; i++) { ua.Add(W.SpawnUnit(ARCHER[a], pa, -24, -4 + i * 4)!); ub.Add(W.SpawnUnit(ARCHER[b], pb, 24, -4 + i * 4)!); }
                t.True(ua.All(u => u != null) && ub.All(u => u != null), $"{a} / {b}: units spawned");
                // their scalps (the stats at their level: what OnKill gives)
                var worth = ua.Concat(ub).ToDictionary(u => u, u => u.Stats!.Scalps);
                int ev = 0; var died = new List<(Unit U, Player? K)>(); int melee = 0, shots = 0;
                double s0a = pa["skulls"], s0b = pb["skulls"];
                int lastDeath = 0;
                for (int step = 0; step < 20 * 300; step++)
                {
                    // the survivors stand at their posts out of each other's alarm range: an attack move, as a player orders it
                    if (step - lastDeath >= 20 * 30)
                    {
                        lastDeath = step;
                        foreach (var (mine, theirs) in new[] { (ua, ub), (ub, ua) })
                        {
                            var e = theirs.FirstOrDefault(u => u.Alive);
                            if (e != null) W.Order(mine.Where(u => u.Alive), new Order("attackmove") { X = e.Pos.X, Z = e.Pos.Z });
                        }
                    }
                    int nd = died.Count;
                    W.Step();
                    foreach (var e in W.EventsSince(ev)) { if (e.Type == "died") died.Add((e.U("entity")!, e.E("killer")?.Owner)); if (e.Type == "hit" && e.Data.Bool("melee")) melee++; if (e.Type == "shoot") shots++; }
                    ev = W.Events.Count;
                    if (died.Count > nd) lastDeath = step;
                    if (ua.All(u => !u.Alive) || ub.All(u => !u.Alive)) break;
                }
                bool aWon = ub.All(u => !u.Alive), bWon = ua.All(u => !u.Alive);
                if (!aWon && !bWon) foreach (var u in ua.Concat(ub).Where(u => u.Alive)) t.Log($"  left: {u} {u.Owner!.Tribe} at {u.Pos} task {u.Task} target {u.Task.Target} hp {u.Hp:0}/{u.MaxHp} stance {u.Stance} anchor ({u.AnchorX:0},{u.AnchorZ:0})");
                t.True(aWon || bWon, $"{a} / {b}: one side is gone after 300 s ({ua.Count(u => u.Alive)} / {ub.Count(u => u.Alive)} alive)");
                t.True(melee > 0 && shots > 0, $"{a} / {b}: blows ({melee}) and shots ({shots})");
                // every dead unit: one died event; its killer is of the other side
                foreach (var u in ua.Concat(ub).Where(u => !u.Alive)) t.Equal(1, died.Count(d => d.U == u), $"{a} / {b}: one died event for {u}");
                foreach (var (u, k) in died) t.True(k == null || k != u.Owner, $"{a} / {b}: {u} killed by the other side");
                // skulls: every enemy killed (the last damager's player gets them)
                double ea = ub.Where(u => !u.Alive).Sum(u => Skulls(worth[u])), eb = ua.Where(u => !u.Alive).Sum(u => Skulls(worth[u]));
                t.Near(ea, pa["skulls"] - s0a, 1e-9, $"{a} / {b}: {a}'s skulls");
                t.Near(eb, pb["skulls"] - s0b, 1e-9, $"{a} / {b}: {b}'s skulls");
                t.Near(ea, pa.Gained["skulls"], 1e-9, $"{a} / {b}: {a}'s gained skulls");
                t.Equal(ub.Count(u => !u.Alive), pa.Kills, $"{a} / {b}: {a}'s kills");
                t.Equal(ua.Count(u => !u.Alive), pa.Lost, $"{a} / {b}: {a}'s losses");
                t.Equal(ua.Count(u => u.Alive), pa.Units, $"{a} / {b}: {a}'s unit count");
                t.Equal(pa.Kills, W.GameStats.Of(pa)!.Killed, $"{a} / {b}: statistics: killed");
                t.Equal(pa.Lost, W.GameStats.Of(pa)!.UnitsLost, $"{a} / {b}: statistics: units lost");
                // the dead leave spirits (owned characters)
                t.True(W.Spirits.Count > 0 || W.Time > 60, $"{a} / {b}: spirits of the fallen");
                t.Log($"{a} / {b}: {(aWon ? a : b)} won after {W.Time:0} s, {died.Count} dead, {melee} blows, {shots} shots, skulls {pa["skulls"]} / {pb["skulls"]}");
                fights++;
            }
        t.True(fights >= 3, "fights between the tribes");
    }

    // ------------------------------------------------------------------ one blow, one shot
    public static void MeleeAndRangedHits(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        var war = W.SpawnUnit("hu_warrior", p0, 0, 0)!;
        var arc = W.SpawnUnit("hu_archer", p0, 0, 40)!;
        var v1 = W.SpawnUnit("aje_spearman", p1, 12, 0)!; var v2 = W.SpawnUnit("aje_spearman", p1, 25, 40)!;
        W.SetAggro(new[] { v1, v2 }, -1);              // passive: they take it
        W.Order(new[] { war }, Order.Attack(v1));
        W.Order(new[] { arc }, Order.Attack(v2));
        int ev = 0; double hp1 = v1.Hp, hp2 = v2.Hp; bool blow = false, shot = false, arrow = false;
        double shotDist = -1;
        for (int i = 0; i < 20 * 20 && !(blow && arrow); i++)
        {
            W.Step();
            foreach (var e in W.EventsSince(ev))
            {
                if (e.Type == "hit" && e.E("attacker") == war && e.Data.Bool("melee"))
                {
                    blow = true;
                    t.True(W.Reach(war, v1) <= W.AttackRangeOf(war, war.Cs()) + war.Radius + 3.5, $"blow in reach ({W.Reach(war, v1):0.0} m)");
                    t.Equal(Math.Max(Math.Ceiling(v1.DmgCache), 1), hp1 - v1.Hp, "a blow takes max(ceil(damage), 1) hit points");
                }
                if (e.Type == "shoot" && e.E("attacker") == arc && !shot) { shot = true; shotDist = W.Reach(arc, v2); }
                if (e.Type == "hit" && e.E("attacker") == arc) { arrow = true; t.Equal(Math.Max(Math.Ceiling(v2.DmgCache), 1), hp2 - v2.Hp, "an arrow takes max(ceil(damage), 1)"); }
            }
            ev = W.Events.Count; hp1 = v1.Hp; hp2 = v2.Hp;
        }
        t.True(blow, "the warrior strikes");
        t.True(shot && arrow, "the archer shoots and hits");
        t.True(shotDist > 8, $"the archer shoots from afar ({shotDist:0.0} m)");
        t.True(W.Reach(war, v1) < 4, "the warrior walked up to its victim");
        // the attack's timing: blows every weapon duration
        var cs = war.Cs()!;
        t.True(W.AttackDuration(war, cs) == cs.Dur, "attack duration = the weapon's (no aura, no AI factor)");
        // retaliation: a hit unit that is not passive fights back
        var v3 = W.SpawnUnit("aje_warrior", p1, -15, 0)!;
        W.TakeDmg(war, v3, 1, 0);
        t.True(v3.Task.Type == "attack" && v3.Task.Target == war, $"the victim fights back ({v3.Task})");
        t.True(v3.Attackers.Has(war) && v3.LastDamager == p0 && v3.LastAttacker == war, "who hit it is known");
    }

    // ------------------------------------------------------------------ splash
    public static void SplashHitsAGroup(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Aje"); var p1 = Sys1.AddPlayer(W, R, 1, "Hu");
        // a unit with a splash weapon (catapults, mortars, throwers ...)
        Unit? A = null; Weapon? sw = null;
        foreach (var n in R.IndexOrder.Where(n => R.Info(n)?.Tribe == "Aje" && (R.Info(n)!.Type == "VHCL" || R.Info(n)!.Type == "ANML" || R.Info(n)!.Type == "CHTR")).OrderBy(n => n, StringComparer.Ordinal))
        {
            var u = W.SpawnUnit(n, p0, 0, 0);
            if (u == null) continue;
            var w = u.Weapons.All.FirstOrDefault(q => u.Cs(q)!.Splash > 0);
            if (w != null && u.Weapons.Long == w && w.Projectile != null) { A = u; sw = w; break; }
            u.Alive = false; W.Units.Remove(u); W.UHash.Remove(u);
        }
        if (A == null) { t.Skip("no Aje unit with a splash weapon"); return; }
        var cs = A.Cs(sw)!;
        // the area hit itself: full damage in the middle, endDmg at the edge
        var vs = new List<Unit>();
        for (int k = 0; k < 4; k++) { var v = W.SpawnUnit("hu_spearman", p1, 0.5 + k * cs.Splash / 4, 30)!; v.Radius = 0.5; v.Hp = 1e6; vs.Add(v); }
        int nv = W.AreaDamage(A, new V3(0, 0, 30), cs.Splash, cs.Dmg, cs.EndDmg);
        t.Equal(4, nv, $"four victims in {cs.Splash} m");
        var loss = vs.Select(v => 1e6 - v.Hp).ToList();
        t.True(loss.All(l => l >= 1), "each loses hit points");
        // a friend of the attacker is not hit, nor an object of a neutral player
        var friend = W.SpawnUnit("aje_spearman", p0, 1, 30)!; double fh = friend.Hp;
        W.AreaDamage(A, new V3(0, 0, 30), cs.Splash, cs.Dmg, cs.EndDmg);
        t.Equal(fh, friend.Hp, "no damage to friends");
        // MIRAGE "all": everyone
        W.AreaDamage(A, new V3(0, 0, 30), cs.Splash, cs.Dmg, cs.EndDmg, new AreaOpts { All = true });
        t.True(friend.Hp < fh, "AreaOpts.All hits friends too");
        // in the fight: the shot's impact hits the group
        foreach (var v in vs) v.Hp = v.MaxHp = 1e6;
        W.SetAggro(vs, -1);
        W.Order(new[] { A }, Order.Attack(vs[1]));
        int ev = 0; bool impact = false;
        for (int i = 0; i < 20 * 30 && !impact; i++)
        {
            W.Step();
            foreach (var e in W.EventsSince(ev)) if (e.Type == "impact" && e.Data.Bool("splash")) impact = true;
            ev = W.Events.Count;
        }
        t.True(impact, $"{A.Name} hits with {sw!.Id} (a splash impact)");
        t.True(vs.Count(v => v.Hp < 1e6) >= 2, $"the impact hurts several ({vs.Count(v => v.Hp < 1e6)})");
    }

    // ------------------------------------------------------------------ towers and bunkers
    public static void TowersShoot(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        var tower = W.PlaceBuilding("hu_small_tower", p0, 0, 0, 0, true)!;
        t.True(tower.Weapons.Long?.Projectile != null, "the tower has a ranged weapon");
        var cs = tower.Cs(tower.Weapons.Long)!;
        var near = W.SpawnUnit("aje_spearman", p1, cs.Range * 0.6, 0)!;
        var far = W.SpawnUnit("aje_spearman", p1, -100, 0)!;
        W.SetAggro(new[] { near, far }, -1);
        near.Hp = near.MaxHp = 1e5;
        int ev = 0, shots = 0, hits = 0;
        Sys1.Step(W, 0);
        for (int i = 0; i < 20 * 10; i++)
        {
            W.Step();
            foreach (var e in W.EventsSince(ev)) { if (e.Type == "shoot" && e.E("attacker") == tower) shots++; if (e.Type == "hit" && e.E("attacker") == tower) { hits++; t.True(e.E("target") == near, "the tower shoots the near one"); } }
            ev = W.Events.Count;
        }
        t.True(shots >= 2 && hits >= 2, $"the tower shoots ({shots} shots, {hits} hits in 10 s)");
        double rate = 10 / W.AttackDuration(tower, cs);
        t.True(shots <= rate + 1, $"no faster than its weapon ({shots} <= {rate + 1:0.0})");
        t.True(near.Hp < near.MaxHp && far.Hp == far.MaxHp, "only the one in range is hurt");
        // an ordered target (AimTowers) is kept and shot when it comes into range
        t.True(W.AimTowers(new List<Entity> { tower }, far), "the tower takes the order");
        t.Equal<Entity?>(far, tower.ForcedTarget, "its forced target");
        far.Pos.X = cs.Range * 0.5; far.Pos.Z = 10; W.UHash.Move(far);
        int fs = 0;
        for (int i = 0; i < 20 * 6; i++) { W.Step(); foreach (var e in W.EventsSince(ev)) if (e.Type == "shoot" && e.E("attacker") == tower) fs++; ev = W.Events.Count; }
        t.True(far.Hp < far.MaxHp, "the ordered target is shot");
        // a bunker shoots only with a garrison (one arrow each): empty, it stays quiet
        if (R.Exists("hu_bunker"))
        {
            var bk = W.PlaceBuilding("hu_bunker", p0, 60, 60, 0, true)!;
            var v = W.SpawnUnit("aje_spearman", p1, 66, 60)!; W.SetAggro(new[] { v }, -1);
            double hv = v.Hp;
            int bs = 0;
            for (int i = 0; i < 20 * 5; i++) { W.Step(); foreach (var e in W.EventsSince(ev)) if (e.Type == "shoot" && e.E("attacker") == bk) bs++; ev = W.Events.Count; }
            t.Equal(0, bs, "an empty bunker does not shoot");
        }
    }

    // ------------------------------------------------------------------ auras
    public static void AurasChangeDamageAndDefence(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        var A = W.SpawnUnit("hu_spearman", p0, 0, 0)!;
        var V = W.SpawnUnit("hu_warrior", p1, 2, 0)!;        // (a Hu warrior of the enemy: warpaint source below is Aje)
        double Hit(Entity a, Entity v) { double h = v.Hp; v.Hp = 1e9; double d = W.TakeDmg(a, v, 1, 0); v.Hp = h; return d; }
        double d0 = Hit(A, V);
        // warcry: a level 5 warrior of A's side, warcry invented: x1.2
        var cry = W.SpawnUnit("hu_warrior", p0, -5, 0, 5)!;
        W.AuraUpdate();
        t.Equal(0, A.St.Aura.Warcry, "no warcry before the invention");
        t.True(Sys1.Invent(R, p0, "warcry"), "warcry invented");
        W.AuraUpdate();
        t.Equal(5, A.St.Aura.Warcry, "a friend in the warcry");
        t.Near(d0 * 1.2, Hit(A, V), 1e-9, "warcry x1.2 at level 5");
        t.True(W.AuraOn(cry), "the warrior's aura is on (buffs)");
        t.True(Buffs.BuffsOf(W, A).Contains("more_damage"), "the buff shows");
        // warpaint of the enemy: x0.8 at level 5 - both apply
        var paint = W.SpawnUnit("aje_warrior", p1, 5, 5, 5)!;
        Sys1.Invent(R, p1, "warpaint");
        W.AuraUpdate();
        t.Equal(5, A.St.Aura.Warpaint, "an enemy in the warpaint");
        t.Near(d0 * 1.2 * 0.8, Hit(A, V), 1e-9, "warcry and warpaint");
        // out of the radius (20 m at level 5): nothing
        A.Pos.X = 80; W.UHash.Move(A); W.AuraUpdate();
        t.Near(d0, Hit(A, V), 1e-9, "outside the auras");
        // pennant: +20 % protection for the rhino's friends (not Aje)
        if (R.Exists("hu_rhino"))
        {
            var p2 = Sys1.AddPlayer(W, R, 2, "Hu");
            var rh = W.SpawnUnit("hu_rhino", p2, 0, 60)!; var fr = W.SpawnUnit("hu_spearman", p2, 4, 60)!;
            Sys1.Invent(R, p2, "pennant");
            double before = W.TempDef(fr, 10);
            W.AuraUpdate();
            t.True(fr.St.Aura.Pennant, "the pennant aura");
            t.Equal(before + 20, W.TempDef(fr, 10), "+20 protection");
        }
        // the debug options: your damage x2, the enemy's defence x4
        var me = A; me.Pos.X = 0; W.UHash.Move(me); W.AuraUpdate();
        double plain = Hit(A, V);
        W.Cheats = new WorldCheats { Human = 0, Me = new PwRemake.Sim.Game.DebugSide { Dmg = 2 }, Foe = new PwRemake.Sim.Game.DebugSide { Def = 4 } };
        double h0 = V.Hp; V.Hp = 1e6; W.TakeDmg(A, V, 1, 0);
        t.Equal(Math.Max(Math.Ceiling(plain * 2 / 4), 1), 1e6 - V.Hp, "debug options: x your damage / their defence");
        W.Cheats = null; V.Hp = h0;
    }

    // ------------------------------------------------------------------ healing
    public static void HealersAndTemplesHeal(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu");
        // a healer: a unit class with a heal ability at some level
        Unit? healer = null;
        foreach (var n in new[] { "hu_druid", "ninigi_monk", "seas_medic" })
            for (int lv = 1; lv <= 5 && healer == null; lv++)
            {
                if (!R.Exists(n) || !R.HasLevel(n, lv) || R.StatsAt(n, lv, p0)?.Heal == null) continue;
                healer = W.SpawnUnit(n, p0, 0, 0, lv);
            }
        if (healer == null) { t.Skip("no healer"); return; }
        var h = healer.Stats!.Heal!;
        var hurt = W.SpawnUnit("hu_warrior", p0, 6, 0)!;
        hurt.Hp = hurt.MaxHp * 0.3;
        double hp0 = hurt.Hp;
        t.Equal<Entity?>(hurt, W.HealScan(healer), "the healer finds the hurt friend");
        Sys1.Step(W, 3);
        t.Equal("heal", healer.Task.Type, "it heals");
        t.True(healer.Healing && healer.VisFlags.TryGetValue(16, out var on) && on, "its kit glows");
        double per = W.HealPerSecond(healer, h, hurt);
        t.True(hurt.Hp > hp0 && hurt.Hp - hp0 <= per * 3 + 1e-6, $"healed {hurt.Hp - hp0:0.0} (<= {per * 3:0.0} in 3 s)");
        Sys1.Step(W, 60);
        t.Equal(hurt.MaxHp, hurt.Hp, "healed fully");
        Sys1.Step(W, 2);
        t.True(healer.Task.Type == "idle" && !healer.Healing, "back to idle, the kit dark");
        // blood infection: no healing
        hurt.Hp = 10; hurt.St.T["noHeal"] = W.Time + 100;
        t.True(W.HealScan(healer) == null, "an infected unit is not healed");
        hurt.St.T.Remove("noHeal");
        // a temple heals friends in its radius once a second
        foreach (var tn in new[] { "hu_temple", "ninigi_temple" })
        {
            if (!R.Exists(tn)) continue;
            var tp = W.PlaceBuilding(tn, p0, 100, 100, 0, true)!;
            if (tp.Stats!.Heal == null) continue;
            var f = W.SpawnUnit("hu_spearman", p0, 100 + tp.Radius + 3, 100)!;
            f.Hp = 10;
            Sys1.Step(W, 3.05);
            double ps = W.HealPerSecond(tp, tp.Stats.Heal, f);
            t.True(f.Hp > 10 && f.Hp <= 10 + ps * 4 + 1e-6, $"{tn} heals ({f.Hp - 10:0.0} in 3 s, {ps:0.0}/s)");
            break;
        }
    }

    // ------------------------------------------------------------------ items
    public static void ItemsArePickedUpAndDropped(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        var it = W.PlaceItem("item_flowerpot_of_insanity", 20, 0);
        if (it == null) { t.Skip("no item model (item_treasure_chest)"); return; }
        t.True(W.Items.Contains(it) && it.Prop != null && it.Prop.Visible, "the item lies on the map");
        var u = W.SpawnUnit("hu_warrior", p0, 0, 0)!;
        t.True(W.CanCarry(u) && W.HasItem(it), "a character can carry it");
        W.Order(new[] { u }, new Order("pickup") { Target = it });
        t.Equal("pickup", u.Task.Type, "the pickup order");
        int ev = 0; bool taken = false;
        for (int i = 0; i < 20 * 30 && !taken; i++) { W.Step(); foreach (var e in W.EventsSince(ev)) if (e.Type == "itemTaken") taken = true; ev = W.Events.Count; }
        t.True(taken && u.Item == it && it.Carrier == u && !it.Prop!.Visible, "it is carried");
        t.Near(1.2, p0.ScalpMod, 1e-9, "MoreScalps: x1.2 skulls");
        // a second item: the inventory is full
        var it2 = W.PlaceItem("item_toolkit", u.Pos.X + 2, u.Pos.Z)!;
        W.Order(new[] { u }, new Order("pickup") { Target = it2 });
        Sys1.Step(W, 3);
        t.True(u.Item == it && it2.Carrier == null, "one item at most");
        // killed: the item falls where it died, the bonus ends; the killer's skulls x1.2 while it carries one
        var k = W.SpawnUnit("aje_warrior", p1, 40, 40)!;
        u.LastDamager = p1;
        W.Kill(u, k);
        t.True(u.Item == null && it.Carrier == null && it.Prop!.Visible, "the item falls");
        t.True(Math.Abs(it.Pos.X - u.Pos.X) < 1e-9 && Math.Abs(it.Pos.Z - u.Pos.Z) < 1e-9, "where its carrier died");
        t.Near(1, p0.ScalpMod, 1e-9, "the bonus ends");
        // the killer's player picks it up: its skulls are worth 1.2
        k.Pos = it.Pos; W.UHash.Move(k);
        W.TakeItem(k, it);
        var v = W.SpawnUnit("hu_spearman", p0, 60, 60)!;
        double s0 = p1["skulls"]; v.LastDamager = p1; W.Kill(v, k);
        t.Near(Math.Floor(TT.JsRound(v.Stats!.Scalps) * 1.2 + 0.5), p1["skulls"] - s0, 1e-9, "skulls x1.2 (Player.AddScalps)");
        // RepairNoCost: the carrier's player repairs for free
        var w2 = W.SpawnUnit("hu_worker", p0, it2.Pos.X, it2.Pos.Z)!;
        W.TakeItem(w2, it2);
        t.Equal<Player?>(p0, W.RepairNoCost, "item_toolkit: repairs cost nothing");
        W.DropItem(w2);
        t.True(W.RepairNoCost == null, "dropped: they cost again");
        // into deep water an item is lost
        var Ww = new W_(R, M, new FlatMap(256) { Water = 0, HeightFn = (x, z) => x > 50 ? -5 : 1 }, new WorldOptions { Seed = 3 });
        var q = Sys1.AddPlayer(Ww, R, 0, "Hu");
        var it3 = Ww.PlaceItem("item_compass", 0, 0)!;
        var c = Ww.SpawnUnit("hu_warrior", q, 0, 0)!;
        Ww.TakeItem(c, it3);
        c.Pos = new V3(80, -5, 0);
        t.True(Ww.DropItem(c) == null && !Ww.Items.Contains(it3) && !it3.Alive, "lost in deep water");
        // GenerateItems: the always-spawns first, no class twice, at most max
        var W3 = Sys1.NewWorld(R, M);
        var sp = new List<ItemSpawn>
        {
            new() { X = 0, Z = 0, Items = { "item_ring" }, Always = true },
            new() { X = 10, Z = 0, Items = { "item_ring" } },
            new() { X = 20, Z = 0, Random = true },
            new() { X = 30, Z = 0, Random = true },
        };
        W3.GenerateItems(sp, new List<string> { "item_ring", "item_talisman", "item_compass" }, 3);
        t.True(W3.Items.Count == 3 && W3.Items.Select(i => i.ItemCls).Distinct().Count() == 3, $"three different items ({string.Join(", ", W3.Items.Select(i => i.ItemCls))})");
        t.True(W3.Items[0].ItemCls == "item_ring" && W3.Items[0].Pos.X == 0, "the always-spawn first");
    }

    // ------------------------------------------------------------------ finishing moves
    public static void FinishingMove(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        var u = W.SpawnUnit("hu_spearman", p0, 0, 0)!;
        var mv = W.FinishingMoveOf(u, W.SpawnUnit("aje_warrior", p1, 100, 100)!);
        if (mv == null || !u.FindLink(mv.Link) || !mv.A.Any(p => u.Anim!.Has(p.Fighter))) { t.Skip($"no clips / links for {u.Gfx} (PW_MODELS)"); return; }
        var V = W.SpawnUnit("aje_warrior", p1, 2.5, 0)!;
        W.Order(new[] { u }, Order.Attack(V));
        W.Order(new[] { V }, Order.Attack(u));
        V.Hp = 1;
        int ev = 0; bool fm = false;
        for (int i = 0; i < 20 * 10 && !fm; i++) { W.Step(); foreach (var e in W.EventsSince(ev)) if (e.Type == "finishingMove") fm = true; ev = W.Events.Count; }
        t.True(fm, "a finishing move instead of the last blow");
        t.True(!V.Alive && V.FinishMove != null, "the victim is dead and plays its part");
        t.True(u.Finishing && u.Task.Type == "finish" && W.Invulnerable(u), "the fighter is invulnerable during the move");
        double hp = u.Hp; W.ApplyDamage(u, 50, null);
        t.Equal(hp, u.Hp, "no damage during the move");
        Sys1.Step(W, 8);
        t.True(!u.Finishing && u.Task.Type != "finish" && V.FinishMove == null, "the move is over");
        t.True(p0["skulls"] > 0, "skulls for the kill");
    }

    // ------------------------------------------------------------------ knockback, poison, death
    public static void KnockbackTrapAndPoison(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        var u = W.SpawnUnit("hu_spearman", p0, 0, 0)!;
        W.Knockback(u, new V3(-1, 0, 0), 3, 0);
        t.True(u.Knock != null && u.Active("trapped") && u.St.TrapWhy == "knock", "thrown and stunned");
        Sys1.Step(W, 1);
        t.Near(3, u.Pos.X, 0.35, "about 3 m away");
        Sys1.Step(W, 1);
        t.True(!u.Active("trapped"), "on its feet again after 1.2 s");
        // poison: 7 a tick, 4 ticks, every 3 s
        var a = W.SpawnUnit("aje_spearman", p1, 120, 120)!;
        double hp = u.Hp = u.MaxHp;
        W.ApplyPoison(u, 7, 4, a);
        Sys1.Step(W, 13);
        t.Equal(hp - 28, u.Hp, "four ticks of 7");
        t.True(u.St.Poison == null && u.LastDamager == p1, "the poison is gone; the poisoner's player hurt it");
        // death: the unit's player loses it, a spirit stays, the event
        int ev = W.Events.Count;
        u.LastDamager = p1;
        W.Kill(u, a);
        t.True(!u.Alive && u.Task.Type == "dead" && p0.Units == 0 && p0.Lost == 1, "dead and counted");
        t.True(W.EventsSince(ev).Any(e => e.Type == "died" && e.E("entity") == u && e.E("killer") == a), "the died event");
        t.True(W.Spirits.Any(s => s.Owner == p0 && s.Name == "hu_spearman"), "its spirit");
        Sys1.Step(W, 20);
        t.True(!W.Units.Contains(u), "the corpse is gone after a while");
        // a wild animal leaves a carcass
        var beast0 = R.IndexOrder.Where(n => R.Info(n)?.Tribe == "World" && R.Info(n)!.Type == "ANML").OrderBy(n => n, StringComparer.Ordinal).Select(n => W.SpawnUnit(n, null, -40, -40)).FirstOrDefault(x => x != null);
        if (beast0 is Unit beast)
        {
            int k0 = p1.Kills;
            W.Kill(beast, a);
            t.True(beast.CorpseNode != null && beast.CorpseNode.Res == "food" && beast.CorpseNode.Amount > 0, "a carcass of food");
            t.Equal(k0 + 1, p1.Kills, "a wild animal counts as a kill");
        }
        // skull protector: no skulls for its friends
        if (R.Exists("aje_skull_protector"))
        {
            var sp = W.PlaceBuilding("aje_skull_protector", p1, 80, 80, 0, true)!;
            var x = W.SpawnUnit("aje_spearman", p1, 85, 80)!;
            W.AuraUpdate();
            double s0 = p0["skulls"]; x.LastDamager = p0; W.Kill(x, null);
            t.Equal(s0, p0["skulls"], "no skulls under the skull protector");
        }
    }

    // ------------------------------------------------------------------ the end of a skirmish
    public static void SkirmishGameOver(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 5, multiplayer: true);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        var f0 = W.PlaceBuilding("hu_fireplace", p0, -60, 0, 0, true)!;
        var f1 = W.PlaceBuilding("aje_resource_collector", p1, 60, 0, 0, true);
        var u0 = W.SpawnUnit("hu_warrior", p0, -40, 0)!;
        var u1 = W.SpawnUnit("aje_warrior", p1, 40, 0)!;
        var wk = W.SpawnUnit("aje_worker", p1, 40, 10)!;
        W.GameOverInit();
        t.True(W.Go != null && W.Go.State.Get(p0) == "alive" && W.Go.State.Get(p1) == "alive", "both in the game");
        Sys1.Step(W, 2);
        t.True(p1.HadHQ, "it has a head quarter");
        // the head quarter goes (the collector is a unit for Aje): the timer of 5 minutes
        foreach (var e in W.Units.Where(x => x.Owner == p1 && x.Name == "aje_resource_collector").ToList()) W.Kill(e, null);
        if (f1 != null) W.Kill(f1, null);
        Sys1.Step(W, 2);
        t.True(W.Go!.Hq.Has(p1), "the head quarter timer runs");
        t.True(W.GoTimers().Any(x => x.Icon == "headquater" && x.Owner == p1), "the HUD shows it");
        // the last units die: out at once
        W.Kill(u1, u0); W.Kill(wk, u0);
        int ev = W.Events.Count;
        Sys1.Step(W, 2);
        t.True(p1.Defeated && p0.Won, "Aje is out, Hu wins");
        t.True(W.EventsSince(ev).Count(e => e.Type == "gameOver") >= 2, "game over events (lost, won)");
        t.True(!W.Go.On, "decided");
        t.True(u0.Alive && f0.Alive, "the winner's objects stay");
        // the head quarter timer runs out: out
        var W2 = Sys1.NewWorld(R, M, 256, 6, multiplayer: true);
        var a = Sys1.AddPlayer(W2, R, 0, "Hu"); var b = Sys1.AddPlayer(W2, R, 1, "Ninigi");
        W2.PlaceBuilding("hu_fireplace", a, -60, 0, 0, true);
        var nf = W2.PlaceBuilding("ninigi_fireplace", b, 60, 0, 0, true)!;
        W2.SpawnUnit("ninigi_warrior", b, 40, 0); W2.SpawnUnit("hu_warrior", a, -40, 0);
        W2.GameOverInit();
        Sys1.Step(W2, 2);
        W2.Kill(nf, null);
        Sys1.Step(W2, 60);
        t.True(!b.Defeated, "a minute later still in");
        Sys1.Step(W2, 245);
        t.True(b.Defeated && a.Won, "five minutes without a head quarter: out");
        // last man standing: no head quarter rule
        var W3 = Sys1.NewWorld(R, M, 256, 7, multiplayer: true);
        var c = Sys1.AddPlayer(W3, R, 0, "Hu"); var d = Sys1.AddPlayer(W3, R, 1, "Ninigi");
        W3.PlaceBuilding("hu_fireplace", c, -60, 0, 0, true);
        var nf3 = W3.PlaceBuilding("ninigi_fireplace", d, 60, 0, 0, true)!;
        W3.SpawnUnit("ninigi_warrior", d, 40, 0); W3.SpawnUnit("hu_warrior", c, -40, 0);
        W3.GameOverInit(lastManStanding: true);
        Sys1.Step(W3, 2); W3.Kill(nf3, null); Sys1.Step(W3, 310);
        t.True(!d.Defeated && W3.Go!.On, "last man standing: no timer");
        // not a multiplayer level: no rules
        var W4 = Sys1.NewWorld(R, M);
        W4.GameOverInit();
        t.True(W4.Go == null, "campaign worlds have no game over rules");
    }

    public static void DominationFlags(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = new W_(R, M, new FlatMap(256), new WorldOptions { Seed = 9, Multiplayer = true, Mode = "domination" });
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        W.PlaceBuilding("hu_fireplace", p0, -80, -80, 0, true); W.PlaceBuilding("aje_resource_collector", p1, 80, 80, 0, true);
        W.SpawnUnit("aje_warrior", p1, 90, 60);
        W.GameOverInit(false, new[] { new FlagSpec { Name = "f1", X = 0, Z = 0 }, new FlagSpec { Name = "f2", X = 40, Z = -40 } });
        t.Equal(2, W.Go!.Flags.Count, "two flags");
        var u1 = W.SpawnUnit("hu_warrior", p0, 2, 0)!; var u2 = W.SpawnUnit("hu_warrior", p0, 40, -38)!;
        Sys1.Step(W, 3);
        t.True(W.Go.Flags.All(f => f.Dir == "up" && f.Timer != null), "both flags go up for Hu");
        Sys1.Step(W, 12);
        t.True(W.Go.Flags.All(f => f.Owner == p0), "Hu holds both flags");
        t.True(W.Go.Dom != null && W.Go.Dom.Team == p0.Team && !W.Go.Dom.Paused, "the domination timer runs");
        // an enemy alone at a flag: it goes down, the timer pauses once it changes hands
        u1.Pos = new V3(-60, 0, 60); W.UHash.Move(u1);
        var e1 = W.SpawnUnit("aje_warrior", p1, 0, 3)!; W.SetAggro(new[] { e1 }, -1);
        Sys1.Step(W, 25);
        t.True(W.Go.Flags[0].Owner == p1, "Aje took the first flag");
        t.True(W.Go.Dom != null && W.Go.Dom.Paused, "the timer pauses");
        e1.Pos = new V3(90, 0, 90); W.UHash.Move(e1);
        u1.Pos = new V3(1, 0, 1); W.UHash.Move(u1);
        Sys1.Step(W, 25);
        t.True(W.Go.Flags.All(f => f.Owner == p0) && !W.Go.Dom!.Paused, "Hu again: the timer goes on");
        Sys1.Step(W, 600);
        t.True(p0.Won && p1.Defeated, "ten minutes of all flags: Hu wins");
    }

    // ------------------------------------------------------------------ statistics
    public static void StatisticsCountAndSample(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M);
        var p0 = Sys1.AddPlayer(W, R, 0, "Hu"); var p1 = Sys1.AddPlayer(W, R, 1, "Aje");
        var wk = W.SpawnUnit("hu_worker", p0, 0, 0)!; var war = W.SpawnUnit("hu_warrior", p0, 3, 0)!;
        W.Emit("trained", new Args { ["unit"] = wk }); W.Emit("trained", new Args { ["unit"] = war });
        var s = W.GameStats.Of(p0)!;
        t.True(s.Workers == 1 && s.Infantry == 1, "made: a worker, a fighter");
        var b = W.PlaceBuilding("hu_small_tower", p0, 30, 30, 0, true)!;
        W.Emit("built", new Args { ["building"] = b });
        // (stats.js cat() reads a.loc of data.actions() - those entries have no loc, so the JS never counts a building's
        // row: eco / mil / def stay 0; kept as in the JS)
        t.Equal(0, s.Def + s.Mil + s.Eco, "a building's row (as the JS: not found)");
        var age = R.Actions(p0).Find(a => a.Id == "age_2");
        if (age != null) { W.Emit("upgrade", new Args { ["player"] = p0, ["action"] = age }); t.Equal<double?>(W.Time, s.Epochs[2], "epoch 2 reached"); }
        W.Emit("tribute", new Args { ["from"] = p0, ["to"] = p1, ["n"] = 50.0 });
        t.True(s.TributeSent == 50 && W.GameStats.Of(p1)!.TributeReceived == 50, "tribute");
        Sys1.Step(W, 25);
        t.Equal(3, W.GameStats.Series.Count, "a sample every 10 s (0, 10, 20)");
        t.Equal(2, W.GameStats.Series[^1].V[p0.Id].Units, "the sample counts the units");
        W.Kill(b, war);
        t.Equal(1, s.BuildingsLost, "a building lost");
    }
}
