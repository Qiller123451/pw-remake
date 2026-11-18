// Golden tests of combat and effects: the JS remake's world (remake/src/game/systems/combat.js, effects.js, items.js
// addScalps; run in node by tests/golden/sys1_combat_dump.mjs) and the C# port build the same scenes and answer the
// same questions - the damage of every unit class's weapons against a sample of units and buildings (TakeDmg and the
// hit points lost), the aura values after an aura update (warcry, warpaint, drums, pennant, cauldrons, scarecrow, skull
// protector, smoke tower, every hero's RangeEffect) with a hit of each unit on its neighbour, splash damage with its
// falloff, two poisons ticking for 30 s, and the skulls and kills for killing every class.
// Both configurations (/opt/data/base, /opt/data/mirage; PW_DATA / PW_DATA_MIRAGE). Needs node and the JS sources
// (PW_REMAKE_SRC, default /home/claude/pw-exporter/remake/src); skipped without them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys1GoldenTests
{
    public static void CombatGoldenBase(TestContext t) => Run(t, RulesTestData.Base(t));
    public static void CombatGoldenMirage(TestContext t) => Run(t, RulesTestData.Mirage(t));

    static double R6(double x) => Math.Round(x * 1e6, MidpointRounding.AwayFromZero) / 1e6;

    sealed class Diffs
    {
        public int N, Bad;
        public readonly List<string> First = new();
        public void Num(string what, double js, double cs, double tol = 2e-6)
        {
            N++;
            if (Math.Abs(js - cs) <= tol * Math.Max(1, Math.Abs(js))) return;
            Bad++;
            if (First.Count < 25) First.Add($"{what}: JS {js}, C# {cs}");
        }
        public void Str(string what, string? js, string? cs)
        {
            N++;
            if (js == cs) return;
            Bad++;
            if (First.Count < 25) First.Add($"{what}: JS {js}, C# {cs}");
        }
    }

    static void Run(TestContext t, string data)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var js = Sys1.JsDump(t, data);
        t.Log($"JS dump {sw.ElapsedMilliseconds} ms"); sw.Restart();
        var (R, M) = Sys1.LoadBare(t, data);
        var tribes = js["tribes"]!.AsArray().Select(x => x!.Str()).ToList();
        var names = R.IndexOrder.Where(n => R.Info(n) != null).OrderBy(n => n, StringComparer.Ordinal).ToList();
        string? Tribe(string n) => R.Info(n)!.Tribe;
        var unitTypes = new HashSet<string> { "CHTR", "ANML", "VHCL", "SHIP" };
        var unitClasses = names.Where(n => unitTypes.Contains(R.Info(n)!.Type) && (tribes.Contains(Tribe(n)!) || Tribe(n) == "World" || Tribe(n) == "Special")).ToList();
        var bldgClasses = names.Where(n => R.Info(n)!.Type == "BLDG" && tribes.Contains(Tribe(n)!)).ToList();
        string? TribeOf(string n) => tribes.Contains(Tribe(n)!) ? Tribe(n) : Tribe(n) == "World" ? null : "Hu";
        string WildBeast() => unitClasses.First(n => Tribe(n) == "World" && R.Info(n)!.Type == "ANML" && R.Def(n, null)!.Aggressive == 1);
        var D = new Diffs();

        // ------------------------------------------------------------------ damage per hit
        {
            var W = Sys1.NewWorld(R, M, 512);
            Dictionary<string, Player> Set(int b) { var d = new Dictionary<string, Player>(); for (int i = 0; i < tribes.Count; i++) d[tribes[i]] = Sys1.AddPlayer(W, R, b + i, tribes[i]); return d; }
            var atk = Set(0); var vic = Set(10); var vet = Set(20);
            Player? OwnerOf(Dictionary<string, Player> set, string n) => TribeOf(n) is string tr ? set[tr] : null;
            int ui = 0, bi = 0;
            (double, double) Place() { int i = ui++; return (-240 + (i % 60) * 8, -240 + Math.Floor(i / 60.0) * 8); }
            var victims = new List<Entity>();
            for (int i = 0; i < unitClasses.Count; i++) { if (i % 3 != 0) continue; var n = unitClasses[i]; var (px, pz) = Place(); var v = W.SpawnUnit(n, OwnerOf(vic, n), px, pz); if (v != null) victims.Add(v); }
            for (int i = 0; i < bldgClasses.Count; i++) { if (i % 4 != 0) continue; var n = bldgClasses[i]; int k = bi++; var b = W.PlaceBuilding(n, OwnerOf(vic, n), -220 + (k % 12) * 40, 60 + Math.Floor(k / 12.0) * 40, 0, true); if (b != null) victims.Add(b); }
            var jv = js["damage"]!["victims"]!.AsArray();
            t.Equal(jv.Count, victims.Count, "damage: the same victims");
            for (int i = 0; i < victims.Count; i++) { D.Str($"victim {i}", jv[i]![0].Str(), victims[i].Name); D.Num($"victim {i} level", jv[i]![1].Num(), victims[i].Level); }
            var rows = js["damage"]!["rows"]!.AsArray();
            int r = 0;
            for (int i = 0; i < unitClasses.Count; i++)
            {
                var n = unitClasses[i];
                var levels = new List<int> { R.MinLevel(n, OwnerOf(atk, n)) };
                if (i % 4 == 0 && R.HasLevel(n, 5) && levels[0] != 5) levels.Add(5);
                foreach (var lv in levels)
                {
                    var set = lv == levels[0] ? atk : vet;
                    var (px, pz) = Place();
                    var A = W.SpawnUnit(n, OwnerOf(set, n), px, pz, lv);
                    if (A == null || A.Weapons.Long == null) continue;
                    foreach (var w in A.Weapons.All)
                    {
                        if (r >= rows.Count) { D.Str("rows", "end", $"{n} {lv} {w.Id}"); continue; }
                        var row = rows[r++]!;
                        string key = $"{n}|{lv}|{w.Id}";
                        D.Str("row", $"{row["a"].Str()}|{row["lvl"].Num()}|{row["w"].Str()}", key);
                        var dm = row["dmg"]!.AsArray(); var loss = row["loss"]!.AsArray();
                        for (int k = 0; k < victims.Count && k < dm.Count; k++)
                        {
                            var V = victims[k];
                            double hp0 = V.Hp; V.Hp = 1e9;
                            double d = W.TakeDmg(A, V, 1, 0, w);
                            D.Num($"{key} -> {V.Name} damage", dm[k].Num(), R6(d));
                            D.Num($"{key} -> {V.Name} hit points lost", loss[k].Num(), 1e9 - V.Hp, 0);
                            V.Hp = hp0; V.Alive = true;
                        }
                    }
                }
            }
            t.Equal(rows.Count, r, "damage: the same rows");
            t.Log($"damage: {r} attacker weapons x {victims.Count} victims ({sw.ElapsedMilliseconds} ms)"); sw.Restart();
        }

        // ------------------------------------------------------------------ auras
        {
            var W = Sys1.NewWorld(R, M, 512);
            var pl = new Dictionary<string, Player>();
            for (int i = 0; i < tribes.Count; i++) pl[tribes[i]] = Sys1.AddPlayer(W, R, i, tribes[i]);
            foreach (var tr in tribes) foreach (var id in new[] { "warcry", "warpaint", "drums", "pennant" }) Sys1.Invent(R, pl[tr], id);
            var ents = new List<Entity>();
            Unit? Spawn(string n, string? tr, double x, double z, int? lv = null) { var u = W.SpawnUnit(n, tr != null ? pl[tr] : null, x, z, lv); if (u != null) { u.Radius = 0.5; ents.Add(u); } return u; }
            Building? Bld(string n, string tr, double x, double z) { var b = W.PlaceBuilding(n, pl[tr], x, z, 0, true); if (b != null) ents.Add(b); return b; }
            var groups = new (string N, string T, int Lv)[] { ("hu_warrior", "Hu", 3), ("hu_warrior", "Hu", 4), ("hu_warrior", "Hu", 5), ("aje_warrior", "Aje", 3), ("aje_warrior", "Aje", 5),
                ("hu_scout", "Hu", 0), ("hu_rhino", "Hu", 0), ("ninigi_parasaurolophus_drums", "Ninigi", 0), ("ninigi_smokebomb_thrower", "Ninigi", 0) };
            var friendOf = new Dictionary<string, string> { ["Hu"] = "hu_spearman", ["Aje"] = "aje_spearman", ["Ninigi"] = "ninigi_archer", ["SEAS"] = "seas_marine" };
            var enemyOf = new Dictionary<string, string> { ["Hu"] = "aje_spearman", ["Aje"] = "hu_spearman", ["Ninigi"] = "hu_archer", ["SEAS"] = "ninigi_archer" };
            string Other(string tr) => tribes.First(q => q != tr);
            double gx = -200;
            foreach (var (n, tr, lv) in groups)
            {
                if (!R.Exists(n)) continue;
                var s = Spawn(n, tr, gx, -150, lv != 0 ? lv : null);
                if (s == null) continue;
                Spawn(friendOf[tr], tr, gx + 3, -150); Spawn(enemyOf[tr], Other(tr), gx - 3, -150);
                gx += 50;
            }
            gx = -200;
            foreach (var (n, tr) in new[] { ("hu_magic_cauldron", "Hu"), ("aje_scarecrow", "Aje"), ("ninigi_cauldron", "Ninigi"), ("aje_skull_protector", "Aje"), ("ninigi_smoke_tower", "Ninigi") })
            {
                if (!R.Exists(n)) continue;
                var b = Bld(n, tr, gx, -50);
                if (b == null) continue;
                Spawn(friendOf[tr], tr, gx + 8, -50); Spawn(enemyOf[tr], Other(tr), gx - 8, -50);
                if (n == "aje_scarecrow") Spawn(WildBeast(), null, gx, -42);
                if (n == "ninigi_smoke_tower") Bld("ninigi_fireplace", "Ninigi", gx, -30);
                gx += 70;
            }
            gx = -200; double gz = 100;
            foreach (var n in names.Where(n => TribeOf(n) != null && R.IsA(n, "CHero")))
            {
                var tr = TribeOf(n)!;
                int lv = 5; while (lv > 1 && !R.HasLevel(n, lv)) lv--;
                var h = Spawn(n, tr, gx, gz, lv);
                if (h == null) continue;
                Spawn(friendOf[tr], tr, gx + 3, gz); Spawn(enemyOf[tr], Other(tr), gx - 3, gz);
                Spawn(WildBeast(), null, gx, gz + 4);
                gx += 45; if (gx > 220) { gx = -200; gz += 60; }
            }
            W.AuraUpdate();
            var je = js["auras"]!["ents"]!.AsArray();
            t.Equal(je.Count, ents.Count, "auras: the same objects");
            for (int i = 0; i < Math.Min(je.Count, ents.Count); i++)
            {
                var e = ents[i]; var j = je[i]!; var a = e.St.Aura; string k = $"aura {i} {e.Name}";
                D.Str(k, j["name"].Str(), e.Name);
                var jb = j["b"]!.AsArray();
                for (int q = 0; q < BonusBuckets.Keys.Length; q++) D.Num($"{k} {BonusBuckets.Keys[q]}", jb[q].Num(), R6(e.St.Bonus[BonusBuckets.Keys[q]]));
                D.Num($"{k} warcry", j["warcry"].Num(), a.Warcry); D.Num($"{k} warpaint", j["warpaint"].Num(), a.Warpaint);
                D.Str($"{k} drums", j["drums"].Bool().ToString(), a.Drums.ToString()); D.Str($"{k} pennant", j["pennant"].Bool().ToString(), a.Pennant.ToString());
                D.Str($"{k} invisible", j["invisible"].Bool().ToString(), a.Invisible.ToString()); D.Str($"{k} cauldron", j["cauldron"].Bool().ToString(), a.NinigiCauldron.ToString());
                D.Str($"{k} skull", j["skull"].Bool().ToString(), a.SkullProtect.ToString()); D.Str($"{k} noAnimalAggro", j["noAnimalAggro"].Bool().ToString(), a.NoAnimalAggro.ToString());
                D.Num($"{k} slowhand", j["slowhand"].Num(), R6(a.Slowhand)); D.Num($"{k} kleemann", j["kleemann"].Num(), R6(a.Kleemann)); D.Num($"{k} drain", j["drain"].Num(), a.DrainBy?.Count ?? 0);
            }
            var jh = js["auras"]!["hits"]!.AsArray();
            for (int i = 0; i + 1 < ents.Count && i < jh.Count; i++)
            {
                var A = ents[i]; var V = ents[i + 1];
                if (A is not Unit || A.Weapons.Long == null) { D.Str($"hit {i}", jh[i] == null ? "none" : "a hit", "none"); continue; }
                double hp0 = V.Hp; V.Hp = 1e9;
                double d = W.TakeDmg(A, V, 1, 0);
                D.Num($"hit {i} {A.Name} -> {V.Name}", jh[i].Num(), R6(d));
                V.Hp = hp0;
            }
            t.Log($"auras: {ents.Count} objects ({sw.ElapsedMilliseconds} ms)"); sw.Restart();
        }

        // ------------------------------------------------------------------ area damage
        {
            var W = Sys1.NewWorld(R, M, 512);
            var pl = tribes.Select((tr, i) => Sys1.AddPlayer(W, R, i, tr)).ToList();
            var rows = js["area"]!.AsArray(); int r = 0;
            double x = -200;
            foreach (var n in unitClasses)
            {
                int ti = tribes.IndexOf(Tribe(n)!);
                if (ti < 0) continue;
                var p = pl[ti];
                var A = W.SpawnUnit(n, p, x, -200);
                if (A == null) continue;
                var w = A.Weapons.All.FirstOrDefault(q => A.Cs(q)!.Splash > 0);
                if (w == null) { A.Alive = false; continue; }
                var cs = A.Cs(w)!;
                A.Weapon = w;
                var other = pl[(ti + 1) % pl.Count];
                var vs = new List<Unit>();
                double R0 = cs.Splash;
                string vn = other.Tribe == "Hu" ? "hu_spearman" : other.Tribe == "Aje" ? "aje_spearman" : other.Tribe == "Ninigi" ? "ninigi_spearman" : "seas_marine";
                for (int k = 0; k < 6; k++) { var v = W.SpawnUnit(vn, other, x + 0.5 + k * R0 / 5, -170); if (v != null) { v.Hp = 1e9; v.Radius = 0.5; vs.Add(v); } }
                var c = new V3(x, 0, -170);
                int nv = W.AreaDamage(A, c, R0, cs.Dmg, cs.EndDmg, new AreaOpts());
                if (r < rows.Count)
                {
                    var row = rows[r]!; string key = $"area {n} {w.Id}";
                    D.Str(key, row["a"].Str() + " " + row["w"].Str(), n + " " + w.Id);
                    D.Num(key + " victims", row["n"].Num(), nv, 0);
                    var jl = row["loss"]!.AsArray();
                    for (int k = 0; k < vs.Count && k < jl.Count; k++) D.Num($"{key} victim {k}", jl[k].Num(), 1e9 - vs[k].Hp, 0);
                }
                r++;
                foreach (var v in vs) v.Alive = false;
                A.Alive = false;
                x += 25; if (x > 220) break;
            }
            t.Equal(rows.Count, r, "area: the same attackers");
        }

        // ------------------------------------------------------------------ poison
        {
            var W = Sys1.NewWorld(R, M, 256);
            var p0 = Sys1.AddPlayer(W, R, 0, "Aje"); var p1 = Sys1.AddPlayer(W, R, 1, "Hu");
            var A = W.SpawnUnit("aje_spearman", p0, 0, 0)!; var V = W.SpawnUnit("hu_warrior", p1, 5, 0)!;
            V.Hp = V.MaxHp = 1000;
            var jp = js["poison"]!.AsArray();
            W.ApplyPoison(V, 7, 4, A);
            for (int i = 0; i < 600; i++)
            {
                W.Time = Math.Round((W.Time + 0.05) * 1e9) / 1e9;
                if (i == 40) W.ApplyPoison(V, 12, 3, A);
                if (i == 90) W.ApplyPoison(V, 5, 6, A);
                W.PoisonTick(V);
                D.Num($"poison step {i}", jp[i].Num(), V.Hp, 0);
            }
        }

        // ------------------------------------------------------------------ skulls
        {
            var W = Sys1.NewWorld(R, M, 512);
            var pl = tribes.Select((tr, i) => Sys1.AddPlayer(W, R, i, tr)).ToList();
            var killer = Sys1.AddPlayer(W, R, 9, "Hu");
            var rows = js["skulls"]!.AsArray(); int r = 0;
            double x = -240;
            foreach (var n in unitClasses)
            {
                var tr = Tribe(n);
                var u = W.SpawnUnit(n, tribes.IndexOf(tr!) is int ti && ti >= 0 ? pl[ti] : null, x, 0);   // (the JS pl[-1]: heroes without an owner)
                x += 3;
                if (u == null) continue;
                double s0 = killer["skulls"]; int k0 = killer.Kills;
                u.LastDamager = killer;
                W.Kill(u, null);
                if (r < rows.Count) { var row = rows[r]!.AsArray(); D.Str($"skulls {r}", row[0].Str(), n); D.Num($"skulls {n}", row[1].Num(), killer["skulls"] - s0, 0); D.Num($"kills {n}", row[2].Num(), killer.Kills - k0, 0); }
                r++;
            }
            t.Equal(rows.Count, r, "skulls: the same classes");
        }

        t.Log($"{D.N} values compared, {D.Bad} differences");
        foreach (var f in D.First) t.Log("  " + f);
        t.Equal(0, D.Bad, "differences to the JS");
    }
}
