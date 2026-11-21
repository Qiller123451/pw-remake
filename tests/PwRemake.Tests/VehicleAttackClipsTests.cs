// The attack clip audit of the vehicles and big units (2026-11-21, Kacper: "The SEAS scorpion, the playable unit from
// MIRAGE, doesn't play its animation when attacking; the same issue might be for the scorpion from the final boss
// battle"; docs/VIEW.md "Attack clips of vehicles and big units"). Every class of the four tribes of type VHCL or SHIP
// and every animal carrying a build-up or turret (titans, siege animals) - base game and MIRAGE - fights a unit and a
// building on a flat map; the clips that start on its own model and on its parts (build-ups, turrets, riders) are
// recorded and compared with the attack-like clips its models have (attack*, shoot*, fire*, sting*, gun_shoot ...).
// A clip a model has that no fight plays is listed; the list must be the explained one below (UNPLAYED: what the
// original does not play either, with the reason) - a new one is a clip the remake forgets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class VehicleAttackClipsTests
{
    static readonly Dictionary<string, (Rules R, ModelManifest M)> s_data = new();
    static string ModelsDir() => Environment.GetEnvironmentVariable("PW_MODELS") is { Length: > 0 } s ? s : "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/models";

    /// <summary>the rules and manifest of a data folder; the models from its own assets first (MIRAGE's carrier, mobile
    /// suit, pirate ship ...), then the base export</summary>
    static (Rules R, ModelManifest M) Load(TestContext t, string data)
    {
        if (!File.Exists(Path.Combine(data, "assets", "manifest.json"))) t.Skip($"no game data in {data}");
        lock (s_data)
        {
            if (s_data.TryGetValue(data, out var d)) return d;
            var R = Rules.Load(data);
            var man = Json.ParseFile(Path.Combine(data, "assets", "manifest.json"))!;
            string own = Path.Combine(data, "assets", "models"), md = ModelsDir();
            var M = new ModelManifest(man, null, n =>
            {
                var p = Path.Combine(own, n + ".glb");
                if (File.Exists(p)) return p;
                p = Path.Combine(md, n + ".glb");
                return File.Exists(p) ? p : null;
            });
            return s_data[data] = (R, M);
        }
    }

    // the clips of a fight: what a model plays to strike or shoot
    static readonly Regex ATTACK = new(@"^(attack|shoot|fire|sting|gun_shoot|strike|spit)|^flame$", RegexOptions.IgnoreCase);

    /// <summary>
    /// What a fight does not play although a model of the unit has it, and why the original does not play it either:
    /// "class|model|clip".
    /// </summary>
    static readonly Dictionary<string, string> UNPLAYED = new(StringComparer.Ordinal)
    {
        ["seas_mgwalker|seas_wehrspinne|attack_front"] = "MIRAGE's MG walker (Vehicle.usl CSeasMGWalker) holds its \"standup\" "
            + "(SetSLEAnim 9999) while it fights; CTransportObj.AttackEnemy lets its build-up's top shoot (attack_front of "
            + "seas_hq_machinegun_nest_top) and returns before FightingObj.AttackEnemy would set the weapon's clip on the body",
    };

    static bool Candidate(Rules R, string n)
    {
        var i = R.Info(n);
        if (i == null || i.Tribe is not ("Hu" or "Aje" or "Ninigi" or "SEAS")) return false;
        if (i.Type is "VHCL" or "SHIP") return true;
        return i.Type == "ANML" && R.Composites(n).Any(s => s.Kind is "buildup" or "turret");
    }

    sealed class Result
    {
        public Unit? U;
        /// <summary>"model|clip" started during the fights</summary>
        public readonly HashSet<string> Played = new(StringComparer.Ordinal);
        /// <summary>the weapons it fought with</summary>
        public readonly HashSet<Weapon> Used = new();
        /// <summary>"model|clip" a fight should play: the clips of the weapons' tables where a model of the unit has them
        /// (its own model, else a part's), its gun add-on's and turrets' attack clips, its riders' attack poses</summary>
        public readonly SortedSet<string> Want = new(StringComparer.Ordinal);
        /// <summary>attack clips of its models no weapon table names (shown, not asked for)</summary>
        public readonly SortedSet<string> Other = new(StringComparer.Ordinal);
    }

    /// <summary>one class: fights a unit, then a building, 40 s each</summary>
    static Result Fight(Rules R, ModelManifest M, string n, ulong seed = 7)
    {
        var res = new Result();
        var w = new W_(R, M, new FlatMap(256), new WorldOptions { Seed = seed });
        var tribe = R.Info(n)!.Tribe!;
        string foe = tribe == "Hu" ? "Aje" : "Hu";
        var p = new Player(1, tribe, new PlayerOpts { Rules = R, Team = 1 });
        var e = new Player(2, foe, new PlayerOpts { Rules = R, Team = 2, Ai = false });
        w.Players.Add(p); w.Players.Add(e);
        var u = res.U = w.SpawnUnit(n, p, 0, 0, 5);
        if (u == null || u.CannotFight) return res;
        var serials = new Dictionary<AnimCtl, int>();
        void Watch(string? model, AnimCtl? a)
        {
            if (model == null || a == null) return;
            if (!serials.TryGetValue(a, out var s) || s != a.Serial) { serials[a] = a.Serial; if (a.CurName != null) res.Played.Add(model + "|" + a.CurName.Split('#')[0]); }
        }
        var cs = u.Cs(u.Weapons.Long);
        double range = cs != null ? Math.Max(2, Math.Min(cs.Range, 40)) : 5;
        foreach (var kind in new[] { "unit", "building" })
        {
            Entity? tgt;
            if (kind == "unit")
            {
                var v = w.SpawnUnit(foe == "Hu" ? "hu_warrior" : "aje_warrior", e, 0, range * 0.6 + u.Radius)!;
                w.SetAggro(new[] { v }, 0);
                v.Hp = v.MaxHp = 1e7;
                tgt = v;
                // (one close by for the turrets that shoot by themselves - the seismosaurus' gatlings reach 20 m)
                if (u.Comp?.Gunners.Count > 0)
                {
                    var near = w.SpawnUnit(foe == "Hu" ? "hu_warrior" : "aje_warrior", e, u.Radius + 6, 0)!;
                    w.SetAggro(new[] { near }, 0);
                    near.Hp = near.MaxHp = 1e7;
                }
            }
            else tgt = w.PlaceBuilding(foe == "Hu" ? "hu_fireplace" : "aje_resource_collector", e, 0, range * 0.6 + u.Radius + 8);
            if (tgt == null) continue;
            w.Order(new[] { u }, Order.Attack(tgt));
            for (int i = 0; i < (int)(40 / W_.STEP); i++)
            {
                w.Step();
                if (!u.Alive) break;
                if (tgt is Unit tu) tu.Hp = tu.MaxHp; else tgt.Hp = Math.Max(tgt.Hp, tgt.MaxHp * 0.5);
                if (u.Task?.Type == "attack" && u.Weapon != null && u.LastHitDone > 0) res.Used.Add(u.Weapon);
                Watch(u.Gfx, u.Anim);
                foreach (var q in u.Comp?.Parts ?? new List<AttachedPart>()) Watch(q.Gfx, q.Anim);
            }
            tgt.Alive = false;
            if (tgt is Unit du) { w.Units.Remove(du); w.UHash.Remove(du); } else w.Buildings.Remove((Building)tgt);
        }
        // what it should have played
        var parts = u.Comp?.Parts ?? new List<AttachedPart>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wp in res.Used)
            foreach (var a in wp.Anims.Count > 0 ? wp.Anims.Select(x => x.Name) : new[] { "attack_front" })
            {
                named.Add(a);
                if (u.Anim?.Has(a) == true)
                {
                    // (an animal carrying a gun add-on stands while the gun shoots: Compose.CarrierStrikes)
                    bool stands = wp.Projectile != null && Compose.IsAnimal(u.Cls, M.Get(u.Gfx)?.Arch) && u.Comp?.Gun != null;
                    if (!stands) res.Want.Add(u.Gfx + "|" + a.ToLowerInvariant());
                }
                else if (parts.FirstOrDefault(q => q.Spec!.Kind != "rider" && q.Anim?.Has(a) == true) is { } q) res.Want.Add(q.Gfx + "|" + a.ToLowerInvariant());
            }
        foreach (var q in parts)
        {
            if (q.Anim == null) continue;
            if (q.Spec!.Kind == "rider") { if (q.Attack != null && q.Anim.Has(q.Attack)) res.Want.Add(q.Gfx + "|" + q.Attack); continue; }
            bool gun = q == u.Comp!.Gun && res.Used.Any(x => x.Projectile != null) || q == u.Comp.Top || q.WClass != null || q.Dep == "gun";
            if (gun && !q.Anim.Clips.Names.Any(c => res.Played.Contains(q.Gfx + "|" + c)) && q.Anim.Clips.Names.FirstOrDefault(c => !c.Contains('#') && ATTACK.IsMatch(c)) is { } gc)
                res.Want.Add(q.Gfx + "|" + gc + " (or another clip of it)");
        }
        void Other(string? model, AnimCtl? a) { if (model != null && a != null) foreach (var c in a.Clips.Names) if (!c.Contains('#') && ATTACK.IsMatch(c) && !named.Contains(c)) res.Other.Add(model + "|" + c); }
        Other(u.Gfx, u.Anim);
        foreach (var q in parts) if (q.Spec!.Kind != "rider") Other(q.Gfx, q.Anim);
        return res;
    }

    /// <summary>every vehicle, ship and carrier animal of the base game and MIRAGE: the clips of its weapons' tables
    /// that a model of it has (its own, else a part's - MIRAGE's Scorpion's robot is its build-up), its gun add-on's,
    /// turrets' and riders' attack clips are played in a fight; the ones that are not are the explained list (UNPLAYED)</summary>
    public static void EveryAttackClipOfVehiclesIsPlayed(TestContext t)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var data in new[] { Sys2.Base, Sys2.Mirage })
        {
            var (R, M) = Load(t, data);
            foreach (var n in R.IndexOrder.Where(n => Candidate(R, n)).OrderBy(n => n, StringComparer.Ordinal))
            {
                var r = Fight(R, M, n);
                // (a weapon with several clips picks one at random each blow - GetNextFightAnim -, so one 40 s fight can
                // miss one of them, and any change to the world's random draws elsewhere - an idle animation's - decides
                // which: a clip still missing is looked for in fights of other seeds, the clips they played added)
                for (ulong seed = 11; seed <= 41 && r.U != null && r.Want.Any(x => !x.Contains(' ') && !r.Played.Contains(x)); seed += 10)
                {
                    var more = Fight(R, M, n, seed);
                    r.Played.UnionWith(more.Played);
                }
                if (r.U == null || r.U.CannotFight) continue;
                string tag = data == Sys2.Base ? "base  " : "MIRAGE";
                foreach (var want in r.Want)
                {
                    var key = n + "|" + want.Split(' ')[0];
                    bool ok = want.Contains(' ') ? false : r.Played.Contains(want);
                    if (!ok && seen.Add(key)) missing.Add(key);
                }
                t.Log($"{tag} {n}: weapons {string.Join(",", r.Used.Select(x => x.Id + "[" + string.Join("/", x.Anims.Select(a => a.Name)) + "]"))}; " +
                      $"wants {string.Join(" ", r.Want)}; played {string.Join(" ", r.Played.Where(x => ATTACK.IsMatch(x.Split('|')[1]) || r.Want.Contains(x)))}" +
                      (r.Other.Count > 0 ? $"; not in a weapon table: {string.Join(" ", r.Other)}" : ""));
            }
        }
        missing = missing.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var m in missing) t.Log((UNPLAYED.TryGetValue(m, out var why) ? "explained " : "UNPLAYED  ") + m + (why != null ? "  - " + why : ""));
        var unexplained = missing.Where(m => !UNPLAYED.ContainsKey(m)).ToList();
        t.True(unexplained.Count == 0, "attack clips no fight plays: " + string.Join(", ", unexplained));
        var stale = UNPLAYED.Keys.Where(k => !missing.Contains(k)).ToList();
        t.True(stale.Count == 0, "explained clips that are played now (take them off the list): " + string.Join(", ", stale));
    }

    /// <summary>
    /// MIRAGE's Scorpion (CSeasScorpion, seas_scorpion): its body pirate_boss has no clips; the robot is its build-up
    /// (model scorpion at psh2), which plays the fight clips - against a unit the claws, sting and legs of
    /// seas_boss_weapon_melee_* (attack_1 / attack_4 / attack_3), against a building the flamethrower of its level
    /// (seas_boss_weapon_flamer_*, attack_2, its 12 s clip not started again before it nearly ran out) - and goes back to
    /// its "standanim" after a blow. It turns to face its target before a blow (penetrating weapons: 11.25 degrees).
    /// </summary>
    public static void MirageScorpionFightsWithItsBuildUp(TestContext t)
    {
        var (R, M) = Load(t, Sys2.Mirage);
        var w = new W_(R, M, new FlatMap(256), new WorldOptions { Seed = 7 });
        var p = new Player(1, "SEAS", new PlayerOpts { Rules = R, Team = 1 });
        var e = new Player(2, "Hu", new PlayerOpts { Rules = R, Team = 2, Ai = false });
        w.Players.Add(p); w.Players.Add(e);
        var u = w.SpawnUnit("seas_scorpion", p, 0, 0, 5)!;
        if (u.Comp?.Fighter?.Anim == null || !u.Comp.Fighter.Anim.Has("attack_1")) t.Skip("no clips of the scorpion model");
        var f = u.Comp!.Fighter!;
        t.Equal("scorpion", f.Gfx, "the Fighter is the robot (seas_eb_buildup's model) on the carrier");
        t.True(u.Anim == null || !u.Anim.Clips.Names.Any(), "the carrier pirate_boss has no clips");
        t.Equal("standanim", f.Anim!.CurName, "it stands");
        // a unit behind it: it turns, then strikes with its claws
        var v = w.SpawnUnit("hu_warrior", e, 0, -12)!;
        w.SetAggro(new[] { v }, 0);
        v.Hp = v.MaxHp = 1e7;
        w.Order(new[] { u }, Order.Attack(v));
        var seen = new List<string>(); int serial = f.Anim.Serial; double firstBlow = -1;
        for (int i = 0; i < (int)(30 / W_.STEP); i++)
        {
            w.Step(); v.Hp = v.MaxHp;
            if (f.Anim.Serial != serial) { serial = f.Anim.Serial; seen.Add(f.Anim.CurName!.Split('#')[0]); if (firstBlow < 0 && f.Anim.CurName!.StartsWith("attack_")) firstBlow = w.Time; }
        }
        var blows = seen.Where(c => c.StartsWith("attack_")).Distinct().OrderBy(c => c).ToList();
        t.True(blows.Count >= 2 && blows.All(c => c is "attack_1" or "attack_3" or "attack_4"), "claws, sting and legs against a unit: " + string.Join(",", seen));
        t.True(seen.Contains("standanim"), "back on its standing clip after a blow");
        t.True(v.Hp == v.MaxHp, "(the target kept alive)");
        double off = Math.Abs(Math.Atan2(Math.Sin(Geo.HeadingTo(v.Pos.X - u.Pos.X, v.Pos.Z - u.Pos.Z) - u.Heading), Math.Cos(Geo.HeadingTo(v.Pos.X - u.Pos.X, v.Pos.Z - u.Pos.Z) - u.Heading)));
        t.True(off < Math.PI / 16 + 0.05, $"it faces its target ({off:0.00} rad)");
        // a building: the flamethrower of its level
        w.Kill(v, null);
        var b = w.PlaceBuilding("hu_fireplace", e, 0, 25)!;
        t.Equal("seas_boss_weapon_flamer_e", w.AgainstBuilding(u, b)?.Id, "level 5: the flamethrower _e against buildings");
        t.Equal<string?>(null, w.AgainstBuilding(u, u)?.Id, "its own weapons against units");
        w.Order(new[] { u }, Order.Attack(b));
        int flames = 0; serial = f.Anim.Serial; double firstFlame = -1, lastFlame = -1;
        for (int i = 0; i < (int)(30 / W_.STEP); i++)
        {
            w.Step(); b.Hp = Math.Max(b.Hp, b.MaxHp * 0.5);
            if (f.Anim.Serial != serial) { serial = f.Anim.Serial; if (f.Anim.CurName == "attack_2") { flames++; if (firstFlame < 0) firstFlame = w.Time; lastFlame = w.Time; } }
        }
        t.True(flames >= 2, $"the flamethrower's attack_2 against the building ({flames})");
        t.Equal("seas_boss_weapon_flamer_e", u.Weapon?.Id, "with the flamethrower in use");
        t.True((lastFlame - firstFlame) / Math.Max(1, flames - 1) >= f.Anim.Duration("attack_2") - 0.6, "its 12 s clip runs before it starts again (SCORPION_FLAMER)");
    }

    /// <summary>
    /// The end boss of mission 16 (Building.usl CScorpionBoss, a building): once woken (JumpOffWall) it stands up and,
    /// with an enemy within 150 m, prepares (prepare_1..3) and strikes with its scissors, flamethrower and legs (attack_1,
    /// attack_2, attack_3) - its own clips, the sim's AnimCtl the view follows. Its model's attack_4 and prepare_4 are
    /// not used by the class (three attacks only), as in the original.
    /// </summary>
    public static void BossScorpionPlaysItsAttacks(TestContext t)
    {
        foreach (var data in new[] { Sys2.Base, Sys2.Mirage })
        {
            var (R, M) = Load(t, data);
            var w = new W_(R, M, new FlatMap(256), new WorldOptions { Seed = 3 });
            var p = new Player(1, "SEAS", new PlayerOpts { Rules = R, Team = 1, Ai = true });
            var e = new Player(2, "Hu", new PlayerOpts { Rules = R, Team = 2 });
            w.Players.Add(p); w.Players.Add(e);
            var s = w.PlaceBuilding("scorpion", p, 0, 0)!;
            if (s.Anim == null || !s.Anim.Has("attack_1")) t.Skip("no clips of the scorpion model");
            t.Equal("CScorpionBoss", s.Def?.Script, "the boss' class");
            var v = w.SpawnUnit("hu_warrior", e, 40, 0)!;
            w.SetAggro(new[] { v }, 0);
            t.True(w.ScorpionActivate(s), "woken");
            var seen = new HashSet<string> { s.Anim.CurName!.Split('#')[0] }; int serial = s.Anim.Serial;
            for (int i = 0; i < (int)(240 / W_.STEP); i++)
            {
                w.Step(); v.Hp = v.MaxHp;
                if (s.Anim.Serial != serial) { serial = s.Anim.Serial; seen.Add(s.Anim.CurName!.Split('#')[0]); }
            }
            foreach (var c in new[] { "stand_up", "standanim", "prepare_1", "prepare_2", "prepare_3", "attack_1", "attack_2", "attack_3" })
                t.True(seen.Contains(c), $"{(data == Sys2.Base ? "base" : "MIRAGE")}: it plays {c} ({string.Join(",", seen)})");
        }
    }
}
