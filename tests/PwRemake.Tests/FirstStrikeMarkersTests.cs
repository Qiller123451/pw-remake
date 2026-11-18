// First strike (the engine's FrstStrk -> CFightingObj.OnDoFirstStrike; src/Sim/Systems/FirstStrike.cs), the range
// markers' merge (game/src/View/World/RangeMarkers*: the outline maths are in src/Sim/World/RangeOutline.cs) and the
// MIRAGE master 2.6.8 ports of this round (nests, minimap hiding, AI). Game data: PW_DATA / PW_DATA_MIRAGE; models:
// PW_MODELS (the clips).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class FirstStrikeMarkersTests
{
    sealed class Fs
    {
        public double At = -1, Delay; public Entity? Target; public int N; int _seen;
        public Unit U = null!; public World W = null!;
        /// <summary>one step; the unit's first strikes counted from the world's events</summary>
        public void Step()
        {
            W.Step();
            for (; _seen < W.Events.Count; _seen++)
            {
                var e = W.Events[_seen];
                if (e.Type != "firstStrike" || e.U("attacker") != U) continue;
                N++;
                if (At < 0) { At = e.Time; Delay = e.Num("delay"); Target = e.E("target"); }
            }
        }
        public void Run(double seconds) { for (int i = 0; i < (int)Math.Round(seconds / World.STEP); i++) Step(); }
    }

    /// <summary>u ordered (or not) to attack a foe `dist` m away; the first strikes and the time of the first real blow</summary>
    static (World W, Unit U, Unit E, Fs F) Arena(TestContext t, string data, string name, string foe, double dist, bool user = true, int level = 1)
    {
        var (R, M) = Sys1.Load(t, data);
        if (!R.Index.ContainsKey(name) || !R.Index.ContainsKey(foe)) t.Skip($"no {name} / {foe}");
        var W = Sys1.NewWorld(R, M, 256, 7);
        string tribe = R.Index[name].Tribe;
        if (tribe is not ("Hu" or "Aje" or "Ninigi" or "SEAS")) tribe = "Hu";
        // (the foe's player has the foe's tribe: a unit of another tribe has no weapon stats)
        var p = Sys1.AddPlayer(W, R, 0, tribe); var q = Sys1.AddPlayer(W, R, 1, R.Index[foe].Tribe);
        var u = W.SpawnUnit(name, p, 0, 0, level)!;
        if (u.Anim == null || u.Anim.Clips.Count == 0) t.Skip($"no clips of {u.Gfx} (PW_MODELS)");
        var e = W.SpawnUnit(foe, q, 0, -dist, 1)!;
        e.Hp = e.MaxHp = 1e6; W.SetAggro(new[] { e }, -1);
        var f = new Fs { U = u, W = W };
        if (user) W.Order(new[] { u }, new Order("attack") { Target = e });
        else W.EngageTarget(u, e);
        return (W, u, e, f);
    }

    /// <summary>base: a Hu warrior ordered to attack strikes once while it runs up (first_strike_0, factor 1, the hit
    /// after the clip's 0.48 s); the blow lands, before its first normal blow; its normal blows follow</summary>
    public static void FirstStrikeBaseWarrior(TestContext t)
    {
        var (W, u, e, f) = Arena(t, RulesTestData.Base(t), "hu_warrior", "aje_warrior", 20);
        double hp0 = e.Hp, firstHpDrop = -1, firstBlow = -1, last = u.LastHitDone;
        for (int i = 0; i < 20 * 12; i++)
        {
            f.Step();
            if (firstHpDrop < 0 && e.Hp < hp0) firstHpDrop = W.Time;
            if (firstBlow < 0 && u.LastHitDone != last) firstBlow = W.Time;
        }
        t.Log($"first strike at {f.At:0.00} (hit in {f.Delay:0.00}), hp first lost at {firstHpDrop:0.00}, first blow at {firstBlow:0.00}");
        t.Equal(1, f.N, "one first strike in the run-up");
        t.True(f.Target == e, "at the ordered target");
        t.Near(0.48, f.Delay, 1e-9, "the hit time of first_strike_0 (its weapon sound)");
        t.True(firstHpDrop > 0 && firstHpDrop <= f.At + f.Delay + 2 * World.STEP + 1e-9, "the first strike's damage lands at its hit time");
        t.True(firstBlow < 0 || firstBlow >= f.At + f.Delay - 1e-9, "the first normal blow comes after the first strike's hit");
        t.True(firstBlow > 0, "normal blows follow");
    }

    /// <summary>the damage is TakeDmg's with factor 1 (base) / 1.5 (MIRAGE)</summary>
    public static void FirstStrikeDamageFactor(TestContext t)
    {
        foreach (var (data, factor) in new[] { (RulesTestData.Base(t), 1.0), (RulesTestData.Mirage(t), 1.5) })
        {
            var (W, u, e, f) = Arena(t, data, "hu_warrior", "aje_warrior", 20);
            double hp0 = e.Hp, lost = -1;
            for (int i = 0; i < 20 * 8 && lost < 0; i++) { f.Step(); if (e.Hp < hp0) lost = hp0 - e.Hp; }
            t.True(f.N == 1, $"{data}: a first strike");
            t.True(W.FirstStrikeFactor == factor, $"{data}: factor {factor}");
            // the normal formula once with factor 1 (a fresh victim of the same class)
            var e2 = W.SpawnUnit("aje_warrior", e.Owner, 30, 30, 1)!;
            double one = W.TakeDmg(u, e2, 1, 0, u.Weapons.Long);
            t.Log($"{data}: first strike took {lost} hp, a factor-1 hit is {one:0.00}");
            t.Equal(Math.Max(Math.Ceiling(one * factor), 1), lost, $"{data}: damage = TakeDmg x {factor}");
        }
    }

    /// <summary>no first strike in an automatic fight (FollowEnemy sets m_sFirstStrike only for an order of the player)</summary>
    public static void FirstStrikeOnlyOnOrders(TestContext t)
    {
        var (W, u, _, f) = Arena(t, RulesTestData.Base(t), "hu_warrior", "aje_warrior", 20, user: false);
        f.Run(8);
        t.True(u.Task.Type == "attack" || u.LastHitDone > 0, "it fights");
        t.Equal(0, f.N, "no first strike");
    }

    /// <summary>a unit whose victim is next to it does not first-strike (MinDist); a building is no target (character,
    /// animal or vehicle only)</summary>
    public static void FirstStrikeNotWhenClose(TestContext t)
    {
        var (_, _, _, f) = Arena(t, RulesTestData.Base(t), "hu_warrior", "aje_warrior", 1.2);
        f.Run(4);
        t.Equal(0, f.N, "no first strike from next to the victim");
    }

    /// <summary>MIRAGE: who strikes follows the techtree's fsa (the warriors yes, hu_spearman - first_strike_0 in its
    /// model but no fsa - no); base: every character with first_strike_0 (the Aje warrior too; a spearman or archer stops
    /// farther than MaxDist from its victim: no strike); MIRAGE animals with the clip first_strike (the allosaurus)</summary>
    public static void FirstStrikeWho(TestContext t)
    {
        string b = RulesTestData.Base(t), m = RulesTestData.Mirage(t);
        int N(string data, string name)
        {
            var (W, u, e, f) = Arena(t, data, name, "aje_warrior", 22, level: 2);     // (level 2: the archers and the Ninigi warrior start there)
            double minGap = 99;
            for (int i = 0; i < 200; i++) { f.Step(); if (u.Task.Chasing) minGap = Math.Min(minGap, W.Reach(u, e) - u.Radius); }
            t.Log($"{name} ({data}): clip {u.Task.FsClip}, approach {W.ApproachDist(u, u.Weapons.Long):0.00}, nearest gap while chasing {minGap:0.00}, run {u.RunSpeed:0.0}");
            return f.N;
        }
        int bs = N(b, "ninigi_warrior"), ba = N(b, "hu_archer"), ms = N(m, "hu_spearman"), mw = N(m, "hu_warrior");
        t.Log($"base Ninigi warrior {bs}, base archer {ba}, MIRAGE spearman {ms}, MIRAGE warrior {mw}");
        t.Equal(1, bs, "base: the Ninigi warrior (first_strike_0)");
        t.Equal(0, ba, "base: the archer stops far from its victim");
        t.Equal(0, ms, "MIRAGE: the spearman has no fsa");
        t.Equal(1, mw, "MIRAGE: the warrior (fsa first_strike_0|first_strike_1)");
        var (R, _) = Sys1.Load(t, m);
        string? allo = new[] { "hu_allosaurus", "aje_allosaurus", "ninigi_allosaurus" }.FirstOrDefault(R.Index.ContainsKey);
        if (allo != null)
        {
            var (_, u, _, f) = Arena(t, m, allo, "hu_warrior", 30);
            if (u.Anim!.Has("first_strike")) { f.Run(10); t.Log($"{allo}: {f.N} (hit in {f.Delay:0.00})"); t.Equal(1, f.N, $"MIRAGE: {allo} (first_strike)"); }
        }
        // base: animals don't (FightingObj's GetFirstStrikeAnim is "")
        var (Rb, _) = Sys1.Load(t, b);
        string? ab = new[] { "aje_allosaurus", "hu_allosaurus" }.FirstOrDefault(Rb.Index.ContainsKey);
        if (ab != null) { var (_, _, _, f) = Arena(t, b, ab, "hu_warrior", 30); f.Run(10); t.Equal(0, f.N, $"base: {ab} has none"); }
    }

    // ------------------------------------------------------------------ range markers

    /// <summary>the merged outline (RangeOutline: MIRAGE's merge patch): two overlapping rings lose the segments inside
    /// each other; a ring inside a bigger one disappears; identical and touching rings stay; separated rings are whole</summary>
    public static void RangeOutlineMerge(TestContext t)
    {
        var arcs = new List<PwRemake.View.Entities.MarkerArc>();
        int Drawn(params PwRemake.View.Entities.MarkerCircle[] c) { PwRemake.View.Entities.RangeOutline.Build(c, arcs); return PwRemake.View.Entities.RangeOutline.Drawn(arcs); }
        int N(double r) => PwRemake.View.Entities.RangeOutline.Segments(r);
        t.Equal(64 + 15, N(10), "64 + 1.5 x range segments");
        t.Equal(2 * N(10), Drawn(new(0, 0, 10), new(30, 0, 10)), "apart: both whole");
        t.Equal(2 * N(10), Drawn(new(0, 0, 10), new(20, 0, 10)), "touching: both whole");
        t.Equal(2 * N(10), Drawn(new(0, 0, 10), new(0, 0, 10)), "identical: both stay");
        t.Equal(N(20), Drawn(new(0, 0, 20), new(3, 0, 5)), "inside a bigger one: gone");
        int two = Drawn(new(0, 0, 10), new(10, 0, 10));
        // the circles at distance r: each loses the third of its ring inside the other (120 degrees)
        t.Near(2 * N(10) * 2.0 / 3, two, 4, $"overlapping: {two} of {2 * N(10)} segments");
        t.True(arcs.Count == 2 && arcs.All(a => a.Count < a.N), "one arc per ring (the run through segment 0 joined)");
        // the kept segments' middles are outside the other ring, the left-out ones inside
        var c = new[] { new PwRemake.View.Entities.MarkerCircle(0, 0, 10), new PwRemake.View.Entities.MarkerCircle(10, 0, 10) };
        PwRemake.View.Entities.RangeOutline.Build(c, arcs);
        foreach (var a in arcs)
            for (int k = 0; k < a.Count; k++)
            {
                double ang = ((a.First + k) % a.N + 0.5) * Math.Tau / a.N;
                var me = c[a.Circle]; var o = c[1 - a.Circle];
                double x = me.X + me.R * Math.Cos(ang), z = me.Z + me.R * Math.Sin(ang);
                t.True(Math.Sqrt((x - o.X) * (x - o.X) + (z - o.Z) * (z - o.Z)) >= o.R - PwRemake.View.Entities.RangeOutline.MARGIN, "a kept segment lies outside the other ring");
            }
    }

    /// <summary>100 rings of a group (the FOW markers of 100 selected units): the outline is the group's border only,
    /// and working it out is cheap (well under a millisecond per type and rebuild)</summary>
    public static void RangeOutlineHundred(TestContext t)
    {
        var rings = new List<PwRemake.View.Entities.MarkerCircle>();
        for (int i = 0; i < 100; i++) rings.Add(new(i % 10 * 2.0, i / 10 * 2.0, 35));
        var arcs = new List<PwRemake.View.Entities.MarkerArc>(); var scratch = new List<(double, double)>();
        PwRemake.View.Entities.RangeOutline.Build(rings, arcs, scratch);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) PwRemake.View.Entities.RangeOutline.Build(rings, arcs, scratch);
        double ms = sw.Elapsed.TotalMilliseconds / 20;
        int drawn = PwRemake.View.Entities.RangeOutline.Drawn(arcs), all = 100 * PwRemake.View.Entities.RangeOutline.Segments(35);
        t.Log($"100 rings of 35 m: {drawn} of {all} segments drawn, {arcs.Count} arcs, {ms:0.000} ms per build");
        t.True(drawn < all / 10, "only the border is drawn");
        t.True(drawn > PwRemake.View.Entities.RangeOutline.Segments(35) / 2, "the border is there");
        // (a few hundred microseconds on a free core; the bound is loose: the test machines are shared)
        t.True(ms < 50, "cheap");
    }

    /// <summary>the five ranges of a selected object (World.MarkerRangesOf, RangeMarkerMgr.usl): attack = the attack range
    /// (range + 2 for characters, + 0 for projectile weapons), alarm = clamp(attack + 8, 32, FOW), fog = FOW, healing =
    /// the heal ability's radius, aura = the aura's radius; a shooting building's attack = its tower reach</summary>
    public static void RangeMarkerValues(TestContext t)
    {
        foreach (var data in new[] { RulesTestData.Base(t), RulesTestData.Mirage(t) })
        {
            var (R, M) = Sys1.Load(t, data);
            var W = Sys1.NewWorld(R, M, 256, 3);
            var p = Sys1.AddPlayer(W, R, 0, "Hu");
            var war = W.SpawnUnit("hu_warrior", p, 0, 0, 1)!;
            var arc = W.SpawnUnit("hu_archer", p, 10, 0, 2)!;
            var mw = W.MarkerRangesOf(war); var ma = W.MarkerRangesOf(arc);
            var wcs = war.Cs(war.Weapons.Long)!; var acs = arc.Cs(arc.Weapons.Long)!;
            t.Log($"{data}: warrior {mw}, archer {ma}");
            t.Near(wcs.Range + 2, mw.Attack, 1e-9, "warrior: range + 2 (a character)");
            t.Near(acs.Range, ma.Attack, 1e-9, "archer: range (projectile)");
            t.Near(Math.Clamp(mw.Attack + 8, 32, Math.Max(32, war.Fow)), mw.Alarm, 1e-9, "alarm range");
            t.Near(war.Fow, mw.Fow, 1e-9, "fog of war range");
            t.Equal(0.0, mw.Healing, "no healer");
            // a healer
            string? healer = R.IndexOrder.FirstOrDefault(n => R.Index[n].Tribe == "Hu" && R.Index[n].Type == "CHTR" && W.SpawnUnit(n, p, 50, 50, 3) is { } h && h.Stats?.Heal is { Radius: > 0 });
            if (healer != null)
            {
                var h = W.Units.Last(u => u.Name == healer);
                t.Near(h.Stats!.Heal!.Radius, W.MarkerRangesOf(h).Healing, 1e-9, $"{healer}: the heal radius");
            }
            // a shooting building: its tower reach
            var tower = R.IndexOrder.FirstOrDefault(n => R.Index[n].Tribe == "Hu" && R.Index[n].Type == "BLDG" && n.Contains("tower"));
            if (tower != null && W.PlaceBuilding(tower, p, -40, -40, 0, true) is { } b && W.TowerRange(b) is var (tr, _))
                t.Near(tr, W.MarkerRangesOf(b).Attack, 1e-9, $"{tower}: the tower's reach");
            // an unbuilt site has none
            if (tower != null && W.PlaceBuilding(tower, p, 40, -40, 0, false) is { } site) t.Equal(0.0, W.MarkerRangesOf(site).Attack, "a site: no markers");
        }
    }

    // ------------------------------------------------------------------ MIRAGE master 2.6.8

    /// <summary>the holy city's hc_ rally buildings (aje_rodeo with the model hc_rodeo ...) and its tavern (aje_cook_house
    /// with hc_taverne) are left out of every minimap but their owner's; the same classes with their own models are not</summary>
    public static void HideOnMinimapHcBuildings(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Mirage(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var p = Sys1.AddPlayer(W, R, 0, "Aje"); var q = Sys1.AddPlayer(W, R, 1, "Hu");
        foreach (var (cls, hc) in new[] { ("aje_rodeo", "hc_rodeo"), ("aje_huge_farm", "hc_huge_farm"), ("aje_cook_house", "hc_taverne") })
        {
            if (!R.Exists(cls)) continue;
            var b = W.PlaceBuilding(cls, p, -60 + 40 * Array.IndexOf(new[] { "aje_rodeo", "aje_huge_farm", "aje_cook_house" }, cls), 40, 0, true);
            if (b == null) { t.Log($"{cls}: not placed"); continue; }
            t.True(!(W.HiddenOnMinimap(b, q)), $"{cls} with its own model ({b.Gfx}): shown");
            b.Gfx = hc;
            t.True(W.HiddenOnMinimap(b, q), $"{cls} with {hc}: hidden from another player");
            t.True(W.HiddenOnMinimap(b, null), $"{cls} with {hc}: hidden from a spectator");
            t.True(!(W.HiddenOnMinimap(b, p)), $"{cls} with {hc}: shown to its owner");
        }
        var hq = W.PlaceBuilding("aje_resource_collector", p, 80, -60, 0, true);
        if (hq != null) { hq.Gfx = "hc_resource_collector"; t.True(!(W.HiddenOnMinimap(hq, q)), "another class (no CRallyBuilding / CNPCSeller): shown"); }
    }

    /// <summary>MIRAGE CSwimmingNest: a nest of aggressive water animals without a limit (spawn_amount -1) lays three
    /// broods in all (3 x spawn_max, the animals born before the start among them); not aggressive or limited: as the
    /// map says; the base game: no limit</summary>
    public static void SwimmingNestBroods(TestContext t)
    {
        foreach (var data in new[] { RulesTestData.Mirage(t), RulesTestData.Base(t) })
        {
            var (R, M) = Sys1.Load(t, data);
            bool mirage = R.Extra.Mod == "MIRAGE";
            var W = Sys1.NewWorld(R, M, 256, 3);
            Sys1.AddPlayer(W, R, 0, "Hu");
            WildNest Nest(string sp, double max, double amount, int start)
            {
                var n = new PwRemake.Sim.Maps.MapNest { Name = "n_" + sp, Species = sp, X = 0, Z = 0, Max = max, Rate = 30, Amount = amount, Swim = true };
                for (int i = 0; i < start; i++) n.Start.Add(new PwRemake.Sim.Maps.NestStart { Species = sp, Swim = true, Born = 0 });
                return W.AddWildNest(n);
            }
            string aggr = R.Exists("Kronosaurus") ? "Kronosaurus" : "Mosasaurus", calm = R.Exists("Henodus") ? "Henodus" : "Placohelys";
            t.Log($"{data}: {aggr} aggressive {R.Def(aggr, null)?.Aggressive}, {calm} {R.Def(calm, null)?.Aggressive}");
            t.Equal(mirage ? 12.0 : -1.0, Nest(aggr, 4, -1, 0).Amount, $"{data}: {aggr}, spawn_max 4, no limit");
            t.Equal(mirage ? 10.0 : -1.0, Nest(aggr, 4, -1, 2).Amount, $"{data}: two born before the start count");
            t.Equal(5.0, Nest(aggr, 4, 5, 0).Amount, $"{data}: a limit of the map stays");
            t.Equal(-1.0, Nest(calm, 4, -1, 0).Amount, $"{data}: {calm} (not aggressive) keeps no limit");
        }
    }

    /// <summary>the computer player's ninja picks the lock of the nearest closed enemy gate within sqrt(50) m (MIRAGE
    /// 2.6.8 "Fixed a bug that prevented Dragon Clan AI from lock picking gates"): not an open gate, not its own, not
    /// one farther</summary>
    public static void AiLockpickGate(TestContext t)
    {
        string data = RulesTestData.Mirage(t);
        var (R, M) = Sys1.Load(t, data);
        var W = Sys1.NewWorld(R, M, 256, 3);
        var p = new Player(0, "Ninigi", new PlayerOpts { Rules = R, Ai = true }); var q = new Player(1, "Hu", new PlayerOpts { Rules = R });
        W.Players.Add(p); W.Players.Add(q);
        string? gateCls = R.IndexOrder.FirstOrDefault(n => R.Index[n].Tribe == "Hu" && R.Def(n, q)?.Gate == true);
        if (gateCls == null) t.Skip("no Hu gate");
        var ninja = W.SpawnUnit("ninigi_ninja", p, 0, 0, 2)!;
        var ai = new PwRemake.Sim.Ai.TribeAI(W, p, PwRemake.Sim.Ai.AiData.Load(data), new PwRemake.Sim.Ai.TribeAIOptions { Difficulty = 8 }, new FakeHands(W));
        var pick = typeof(PwRemake.Sim.Ai.TribeAI).GetMethod("LockpickGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Building? Pick() => (Building?)pick.Invoke(ai, new object[] { ninja });
        var far = W.PlaceBuilding(gateCls!, q, 0, 30, 0, true)!;
        t.True(Pick() == null, "a gate 30 m away: none");
        var near = W.PlaceBuilding(gateCls!, q, 6, 0, 0, true)!;
        near.GateState = PwRemake.Sim.World.Moves.GATE_CLOSED;
        t.True(Pick() == near, $"a closed enemy gate 6 m away ({gateCls})");
        near.GateState = PwRemake.Sim.World.Moves.GATE_OPEN;
        t.True(Pick() == null, "an open gate: none");
        near.GateState = PwRemake.Sim.World.Moves.GATE_AUTO;
        t.True(Pick() == near, "a gate that opens for its own side: picked");
        var own = W.PlaceBuilding(R.IndexOrder.FirstOrDefault(n => R.Index[n].Tribe == "Ninigi" && R.Def(n, p)?.Gate == true) ?? gateCls!, p, -5, 0, 0, true);
        if (own != null && own.Owner == p) t.True(Pick() == near, "its own gate nearer: not picked");
    }

    /// <summary>MIRAGE 2.6.8 chat: a line to one computer player is its request; the computer players read the lines to
    /// the allies / the enemies; a line to all only with "AI&lt;n&gt;:"</summary>
    public static void ChatAiReaders(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var me = new Player(0, "Hu", new PlayerOpts { Rules = R, Team = 0 });
        var ally = new Player(1, "Aje", new PlayerOpts { Rules = R, Team = 0, Ai = true });
        var foe = new Player(2, "Ninigi", new PlayerOpts { Rules = R, Team = 1, Ai = true });
        var human = new Player(3, "Hu", new PlayerOpts { Rules = R, Team = 0 });
        foreach (var p in new[] { me, ally, foe, human }) W.Players.Add(p);
        var A = PwRemake.Sim.Ai.AiData.Load(RulesTestData.Base(t));
        var AP = new PwRemake.Sim.Ai.AiPlayers(W, A) { Passive = true };
        foreach (var p in new[] { ally, foe }) AP.Add(new PwRemake.Sim.Ai.TribeAI(W, p, A, new PwRemake.Sim.Ai.TribeAIOptions { Difficulty = 4 }, new FakeHands(W)));
        AP.Attach();
        t.True(me.IsFriend(ally) && me.IsEnemy(foe), "the teams");
        string S(string to, string text) => string.Join(",", PwRemake.UI.Hud.Logic.ChatRouting.AiReaders(W, me, to, text).Select(r => $"{r.Slot}:{r.Word}"));
        t.Equal("1:help", S("allies", "help"), "allies: the allied computer player reads it");
        t.Equal("2:help me", S("enemies", "help me"), "enemies: the enemy computer player");
        t.Equal("2:food", S("p2", "food"), "a direct line to a computer player");
        t.Equal("", S("p3", "help"), "a direct line to a human: no request");
        t.Equal("", S("all", "help"), "to all without the prefix: nobody");
        t.Equal("1:wood", S("all", "AI1: wood"), "to all with AI1:");
        t.Equal("1,2", string.Join(",", PwRemake.UI.Hud.Logic.ChatRouting.DirectTargets(W, me).Select(p => p.Id)), "the computer players are listed by name");
    }
}

