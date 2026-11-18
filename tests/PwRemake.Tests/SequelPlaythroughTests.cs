// Every mission of the remake's own campaigns played through by script (docs/SEQUEL.md "Tests"): the player's side
// cannot be hurt, the fights are won by killing what stands in the way, the heroes and townsfolk are put where the story
// needs them - and each mission must end in a win. The scenarios also as several tribes, and the start checked: the
// tribe's main building (or the Dustriders' collector), the hero, the enemies of other tribes.
// PW_SEQUEL_ONLY=1,5,12 runs only those missions; PW_SCENARIO_TRIBES=Hu,Aje the tribes of the scenarios (default all).
// Ported from remake/tests/sequel_flow.js and scenario_flow.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using PwRemake.Sim.Sequel;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class SequelPlaythroughTests
{
    sealed class Flow
    {
        public readonly SequelSession S;
        public SequelRuntime R => S.Runtime;
        public World W => S.World;
        public Player Me => R.Me!;
        public readonly List<string> Out = new();
        public bool Ok = true;
        public Flow(SequelSession s) { S = s; }
        public void Sec(double s) { int n = Math.Max(1, (int)Math.Round(s * 20)); for (int i = 0; i < n; i++) W.Step(); }
        public Player? P(string role) => R.PlayerOf(role);
        public List<Unit> Mine(Func<Unit, bool>? f = null) => W.Units.Where(u => u.Alive && u.Owner == Me && (f == null || f(u))).ToList();
        public List<Unit> Foes(string role, Func<Unit, bool>? f = null) { var p = P(role); return W.Units.Where(u => u.Alive && u.Owner == p && (f == null || f(u))).ToList(); }
        public void Guard()
        {
            foreach (var u in Mine()) if (!u.Invulnerable()) W.SetInvulnerable(u, true);
            if (R.Home(Me) is { } h && !h.Invulnerable()) W.SetInvulnerable(h, true);
        }
        public void Kill(IEnumerable<Entity?> list, Player? by = null) { foreach (var e in list.ToList()) if (e != null && e.Alive) { if (by != null) e.LastDamager = by; W.Kill(e, null); } }
        public Unit? Hero(string n) => Mine(u => u.Name == n).FirstOrDefault();
        public void Tp(Entity? u, double x, double z) { if (u != null) W.Teleport(u, x, z); }
        /// <summary>puts a unit within r of (x, z): the point itself, else around it (a building's footprint pushes a
        /// teleported unit out - its size depends on the model data the test has)</summary>
        public void Near(Entity? u, double x, double z, double r)
        {
            if (u == null) return;
            for (int k = 0; k < 24; k++)
            {
                double a = k * 0.9, d = k == 0 ? 0 : 4 + k * 0.5;
                W.Teleport(u, x + Math.Cos(a) * d, z + Math.Sin(a) * d);
                if (Math.Sqrt((u.Pos.X - x) * (u.Pos.X - x) + (u.Pos.Z - z) * (u.Pos.Z - z)) <= r - 1) return;
            }
        }
        public void ClearOut() => Kill(Foes("outbreak"));
        public DynValue St(string key) => S.Lua.M.Get("state").Table.Get(key);
        public Entity? E(DynValue v) => v.Type == DataType.Number ? W.ById((int)v.Number) : null;
        public Entity? StE(string key) => E(St(key));
        public double StN(string key) => St(key).Type == DataType.Number ? St(key).Number : 0;
        public void Check(string what, bool c) { Out.Add($"{what}: {(c ? "ok" : "FAIL")}"); if (!c) Ok = false; }
        public string Objs() => string.Join(",", R.Objectives.Select(o => o.Id + (o.Done ? "+" : o.Failed ? "x" : "")));
        public void Evac(string zone, int n = 99)
        {
            var Z = R.Zone(zone)!;
            foreach (var u in Foes("civilians").Take(n)) { var m = R.Mark(u); m.Rescued = true; m.Hiding = false; u.Untargetable = false; W.Teleport(u, Z.X + W.Rng.Next() * 4, Z.Z + W.Rng.Next() * 4); }
            Sec(2);
        }
        public Building? Seal(SqRift r) { var b = W.PlaceBuilding("sq_stabilizer", Me, r.X + 12, r.Z + 6, 0, true); if (b != null) W.SetInvulnerable(b, true); return b; }
        public bool Capture(Unit? V, SqZone pen)
        {
            if (V == null || !V.Alive) return false;
            R.Sleep(V);
            var t = R.Spawn("sq_capture_truck", "me", V.Pos.X + 4, V.Pos.Z, 1).FirstOrDefault();
            Guard(); Sec(1);
            if (t != null) W.Teleport(t, pen.X, pen.Z);
            Sec(1.5);
            return true;
        }
    }

    static bool Invulnerable(this Entity e) => e is Unit u ? u.InvulnT > 0 : e is Building b && b.InvulnT > 0;

    static Flow Begin(TestContext t, string kind, int id, string tribe = "")
    {
        var s = SequelTests.Session(t, kind, id, 1, tribe, kind == "scenario" && tribe == "SEAS" ? "mirage" : null);
        var f = new Flow(s);
        s.World.Enqueue(new SequelCommand { Player = s.Human!.Id, What = "begin" });
        f.Sec(0.05);
        return f;
    }

    static IEnumerable<int> Only(IEnumerable<int> all)
    {
        var o = Environment.GetEnvironmentVariable("PW_SEQUEL_ONLY");
        return string.IsNullOrEmpty(o) ? all : all.Where(i => o.Split(',').Contains(i.ToString()));
    }

    public static void ContainmentPlaythroughs(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_PLAY") != "1" && Environment.GetEnvironmentVariable("PW_SEQUEL_ONLY") == null) t.Skip("PW_PLAY=1 (or PW_SEQUEL_ONLY) to run the playthroughs");
        var failed = new List<string>();
        foreach (var id in Only(Enumerable.Range(1, 16)))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var f = Begin(t, "sequel", id);
            try { Containment(f, id); }
            catch (Exception e) { f.Check("no exception: " + e.Message, false); }
            f.Check("won", f.R.Ended is { Won: true });
            f.Check("no Lua errors", f.S.Lua.Errors.Count == 0);
            f.Out.Add(f.Objs());
            var line = $"{(f.Ok ? "ok  " : "FAIL")} sequel {id} ({f.R.Time:0} s game, {sw.ElapsedMilliseconds} ms): {string.Join(" | ", f.Out)}";
            t.Log(line);
            if (!f.Ok) failed.Add(line);
        }
        t.True(failed.Count == 0, $"{failed.Count} missions failed:\n" + string.Join("\n", failed));
    }

    public static void ScenarioPlaythroughs(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_PLAY") != "1" && Environment.GetEnvironmentVariable("PW_SEQUEL_ONLY") == null) t.Skip("PW_PLAY=1 (or PW_SEQUEL_ONLY) to run the playthroughs");
        var tribes = (Environment.GetEnvironmentVariable("PW_SCENARIO_TRIBES") ?? "Hu,Aje,Ninigi,SEAS").Split(',');
        var failed = new List<string>();
        foreach (var id in Only(Enumerable.Range(1, 5)))
            foreach (var tribe in tribes)
                foreach (var path in id == 4 ? new[] { "choose", "silence" } : new[] { "" })
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var f = Begin(t, "scenario", id, tribe);
                    try { Scenario(f, id, path); }
                    catch (Exception e) { f.Check("no exception: " + e.Message, false); }
                    f.Check("won", f.R.Ended is { Won: true });
                    f.Check("no Lua errors", f.S.Lua.Errors.Count == 0);
                    f.Out.Add(f.Objs());
                    var line = $"{(f.Ok ? "ok  " : "FAIL")} scenario {id} as {tribe}{(path != "" ? " (" + path + ")" : "")} ({f.R.Time:0} s game, {sw.ElapsedMilliseconds} ms): {string.Join(" | ", f.Out)}";
                    t.Log(line);
                    if (!f.Ok) failed.Add(line);
                }
        t.True(failed.Count == 0, $"{failed.Count} scenarios failed:\n" + string.Join("\n", failed));
    }

    // ------------------------------------------------------------------ Containment (sequel_flow.js)
    static void Containment(Flow f, int id)
    {
        var R = f.R; var W = f.W;
        f.Guard();
        switch (id)
        {
            case 1:
                f.Evac("buses", (int)f.StN("need") + 2);
                f.Sec(155);
                f.Check("rift and Taslov", R.Rifts.Count == 1 && f.Hero("sq_taslov") != null);
                f.Seal(R.Rifts[0]); for (int k = 0; k < 16 && R.Rifts[0].Open; k++) { f.ClearOut(); f.Sec(5); }
                f.Check("rift sealed", !R.Rifts[0].Open);
                f.Sec(15);
                break;
            case 2:
                W.SetInvulnerable(f.StE("station")!, true);
                for (int k = 0; k < 75 && R.Ended == null; k++) { f.ClearOut(); f.Sec(10); }
                break;
            case 3:
                {
                    var pen = R.Zone("pen")!;
                    bool Horned(Unit u) => System.Text.RegularExpressions.Regex.IsMatch(u.Name, "ceratops|Styraco");
                    for (int k = 0; k < 40 && !R.IsDone("capture"); k++)
                    {
                        var V = f.Foes("outbreak", u => Horned(u) && !u.Parked).FirstOrDefault();
                        if (V != null) f.Capture(V, pen); else f.Sec(10);
                        f.Kill(f.Foes("outbreak", q => !q.Parked && !R.Asleep(q) && !Horned(q)));
                    }
                    f.Check("captured", R.IsDone("capture"));
                    f.Sec(12);
                    break;
                }
            case 4:
                foreach (var r in R.Rifts.ToList()) f.Seal(r);
                for (int k = 0; k < 20 && R.Rifts.Any(r => r.Open); k++) { f.ClearOut(); f.Sec(5); }
                f.Check("rifts sealed", R.Rifts.All(r => !r.Open));
                f.Sec(12);
                break;
            case 5:
                W.SetInvulnerable(f.StE("pad")!, true);
                for (int k = 0; k < 95 && R.Ended == null; k++) { f.ClearOut(); f.Sec(10); }
                break;
            case 6:
                f.Sec(3);
                f.Check("caught", f.Capture(f.StE("boss") as Unit, R.Zone("pen")!));
                f.Sec(12);
                break;
            case 7:
                f.Evac("bridge");
                f.Seal(R.Rifts[0]); for (int k = 0; k < 18 && R.Rifts[0].Open; k++) { f.ClearOut(); f.Sec(5); }
                f.Check("rift sealed", !R.Rifts[0].Open);
                f.Tp(f.StE("taslov"), 100, 13); f.Sec(2);
                f.Check("Taslov in the archive", f.St("inside").CastToBool());
                for (int k = 0; k < 50 && R.Ended == null; k++) { f.Kill(f.Foes("crane")); f.ClearOut(); f.Sec(5); }
                f.Check("Taslov taken (the story)", f.St("taken").CastToBool());
                break;
            case 8:
                f.Near(f.Hero("sq_halloran"), 190, -180, Math.Max(18, (f.StE("jail")?.Radius ?? 0) + 6)); f.Sec(2);
                f.Check("Taslov freed", R.IsDone("taslov"));
                f.Kill(new[] { f.StE("hq") }); f.Sec(15);
                break;
            case 9:
                f.Tp(f.Hero("sq_halloran"), 150, -150); f.Sec(2);
                f.Check("met Tala", R.IsDone("meet"));
                W.SetInvulnerable(f.StE("ajeHq")!, true);
                f.Kill(new[] { f.StE("craneHq") }); f.Sec(15);
                break;
            case 10:
                {
                    var tas = f.Hero("sq_taslov");
                    f.Tp(tas, 158, -150); f.Sec(2);
                    f.Check("at the temple", R.IsDone("temple"));
                    for (int k = 0; k < 40 && !R.IsDone("core"); k++) { f.ClearOut(); f.Kill(f.Foes("crane")); f.Tp(tas, 158, -150); f.Sec(5); }
                    f.Check("core", R.IsDone("core"));
                    f.Tp(tas, -205, 205); f.Sec(12);
                    break;
                }
            case 11:
                W.SetInvulnerable(f.StE("machine")!, true);
                for (int k = 0; k < 125 && R.Ended == null; k++) { f.ClearOut(); f.Kill(f.Foes("crane")); f.Sec(10); }
                break;
            case 12:
                {
                    int n0 = f.Mine().Count;
                    var ds = f.St("districts").Table;
                    foreach (var dv in ds.Values)
                    {
                        var d = dv.Table;
                        f.Kill(d.Get("us").Table.Values.Select(f.E));
                        double x = d.Get("x").Number, z = d.Get("z").Number;
                        foreach (var h in new[] { "sq_halloran", "sq_booker", "sq_tala" }) f.Tp(f.Hero(h), x + W.Rng.Next() * 6, z + W.Rng.Next() * 6);
                        if (d.Get("tame").CastToBool()) { f.Sec(2); f.Tp(f.Hero("sq_booker"), 160, -118); }
                        f.Sec(3); f.Guard();
                    }
                    f.Check("districts", R.IsDone("districts"));
                    f.Check("holdouts joined", f.Mine().Count >= n0 + 5 + 3 + 3 + 2);
                    f.Check("triceratops tamed", f.Mine(u => u.Name == "Triceratops").Count == 2);
                    f.Check("Old Scar objective", R.Objectives.Any(o => o.Id == "scar"));
                    f.Kill(new[] { f.StE("boss") }); f.Sec(15);
                    break;
                }
            case 13:
                {
                    for (int k = 0; k < 120 && R.Ended == null; k++)
                    {
                        f.Kill(f.Foes("outbreak")); f.Kill(f.Foes("crane")); f.Kill(W.Buildings.Where(b => b.Alive && b.Owner == f.P("crane")));
                        var lead = f.St("trucks").Table.Values.Select(f.E).FirstOrDefault(e => e is { Alive: true });
                        if (lead != null) foreach (var u in f.Mine(q => q.Name != "sq_cargo_truck")) if (Math.Sqrt((u.Pos.X - lead.Pos.X) * (u.Pos.X - lead.Pos.X) + (u.Pos.Z - lead.Pos.Z) * (u.Pos.Z - lead.Pos.Z)) > 30) f.Tp(u, lead.Pos.X + 8, lead.Pos.Z + 8);
                        f.Guard(); f.Sec(5);
                    }
                    f.Check("trucks delivered", f.StN("delivered") >= f.StN("need"));
                    f.Out.Add($"delivered {f.StN("delivered")}, legs to {f.StN("lead")}");
                    break;
                }
            case 14:
                {
                    f.Sec(5);
                    var sites = f.St("sites").Table.Values.Select(v => v.Table).ToList();
                    f.Check("three engines", sites.All(s => f.E(s.Get("engine")) is { Alive: true }));
                    var w = sites[0];
                    var wid = w.Get("id").String;
                    f.Kill(new[] { f.E(w.Get("engine")) }); f.Sec(3);
                    f.Check("rift shut with its engine", !(R.Rifts.LastOrDefault(r => r.Id == wid)?.Open ?? false));
                    f.Sec(130);
                    f.Check("engine raised again while the station runs", w.Get("raised").Number == 2 && (R.Rifts.LastOrDefault(r => r.Id == wid)?.Open ?? false));
                    f.Kill(new[] { f.StE("station") }); f.Sec(3);
                    f.Check("station down: all engines dark", R.IsDone("engines") && sites.All(s => !(R.Rifts.LastOrDefault(r => r.Id == s.Get("id").String)?.Open ?? false) && !(f.E(s.Get("engine")) is { Alive: true })));
                    f.Kill(new[] { f.StE("hq") }); f.Sec(15);
                    break;
                }
            case 15:
                {
                    foreach (var kv in f.St("cages").Table.Values)
                    {
                        var k = kv.Table; double x = k.Get("x").Number, z = k.Get("z").Number;
                        f.Kill(R.Near(x, z, 40, "crane"));
                        f.Near(f.Hero("sq_tala"), x, z, Math.Max(16, (f.E(k.Get("b"))?.Radius ?? 0) + 4));
                        f.Sec(2);
                    }
                    f.Check("cages opened", R.IsDone("cages"));
                    f.Check("riders joined", f.Mine(u => u.Name.StartsWith("aje_")).Count >= 15);
                    f.Sec(4 * 60 - R.Time + 5);
                    f.Check("a wave came through the engine", f.StN("waves") >= 1);
                    f.Kill(new[] { f.StE("engine") }); f.Sec(2);
                    f.Check("engine objective", R.IsDone("engine"));
                    f.Kill(new[] { f.StE("hq") }); f.Sec(15);
                    break;
                }
            case 16:
                {
                    W.SetInvulnerable(f.StE("door")!, true);
                    f.Sec(160);
                    f.Check("the Door hijacked", f.St("wild").CastToBool() && (R.Rifts.LastOrDefault(r => r.Id == "door")?.Open ?? false));
                    f.Sec(8 * 60 - R.Time + 3);
                    var boss = f.StE("boss");
                    f.Check("Behemoth shielded", boss != null && boss.Invulnerable());
                    f.Out.Add($"caravans {f.StN("caravans")}, arrived {f.StN("arrived")}");
                    f.Kill(f.St("masts").Table.Values.Select(f.E)); f.Sec(3);
                    f.Check("masts down", R.IsDone("masts") && !(R.Rifts.LastOrDefault(r => r.Id == "door")?.Open ?? false) && !(boss?.Invulnerable() ?? false));
                    f.Kill(new[] { boss }); f.Sec(2);
                    var crane = f.StE("crane");
                    f.Check("Crane out of the wreck", crane is { Alive: true });
                    if (crane != null) { W.SetInvulnerable(crane, true); f.Tp(f.Hero("sq_halloran"), crane.Pos.X + 3, crane.Pos.Z); }
                    f.Sec(2);
                    f.Check("Crane taken", R.IsDone("crane"));
                    f.Sec(15);
                    break;
                }
        }
    }

    // ------------------------------------------------------------------ the scenarios (scenario_flow.js)
    static void Scenario(Flow f, int id, string path)
    {
        var R = f.R; var W = f.W; var me = f.Me;
        Unit? Hero() => f.Mine(u => u.Name == f.St("heroCls").String).FirstOrDefault();
        f.Check($"plays as {R.Tribe}", me.Tribe == R.Tribe && R.Kind == "scenario");
        f.Check("main building", R.Home(me) != null);
        f.Check("hero", Hero() != null);
        f.Check("enemies of other tribes", new[] { "foe", "foe2" }.All(r => f.P(r) == null || f.P(r)!.Tribe != me.Tribe));
        f.Guard();
        switch (id)
        {
            case 1:
                {
                    f.Sec(2);
                    var beasts = f.St("beasts").Table.Values.Select(v => v.Table).ToList();
                    f.Kill(new[] { f.E(beasts.First(b => b.Get("id").String == "king").Get("e")) }, f.P("foe"));
                    f.Sec(1);
                    f.Check("a rival kill counts for them", f.StN("theirs") == 1);
                    foreach (var b in beasts.Where(x => x.Get("id").String != "king").Take(3)) { f.Kill(new[] { f.E(b.Get("e")) }, me); f.Sec(1); }
                    f.Check("three trophies", f.StN("mine") == 3);
                    f.Sec(10);
                    break;
                }
            case 2:
                for (int k = 0; k < 400 && R.Ended == null; k++) { f.Kill(f.Foes("outbreak")); f.Guard(); f.Sec(5); }
                f.Check("ten waves", f.StN("wave") == 10);
                f.Check("the Barrow King came", f.StE("king") != null);
                break;
            case 3:
                {
                    for (int k = 0; k < 120 && R.Ended == null; k++)
                    {
                        foreach (var r in new[] { "foe", "foe2" }) f.Kill(f.Foes(r, u => Math.Sqrt(u.Pos.X * u.Pos.X + u.Pos.Z * u.Pos.Z) < 60));
                        foreach (var u in f.Mine(x => !x.IsWorker)) if (Math.Sqrt(u.Pos.X * u.Pos.X + u.Pos.Z * u.Pos.Z) > 15) f.Tp(u, (W.Rng.Next() - 0.5) * 12, (W.Rng.Next() - 0.5) * 12);
                        f.Guard(); f.Sec(5);
                    }
                    var held = f.St("held").Table;
                    f.Check("held the summit", held.Get("me").Number >= f.StN("need"));
                    f.Check("the mountain erupted", f.StN("rumbles") >= 3);
                    break;
                }
            case 4:
                {
                    for (int k = 0; k < 60 && f.StN("met") != 2; k++) f.Sec(5);
                    f.Check("both envoys came", f.StN("met") == 2);
                    var homes = f.St("homes").Table;
                    if (path == "silence")
                    {
                        f.Sec(f.StN("deadline") - R.Time + 2);
                        f.Check("silence: both enemies", f.St("chosen").String == "none" && me.IsEnemy(f.P("foe")) && me.IsEnemy(f.P("foe2")));
                        f.Kill(new[] { f.E(homes.Get("foe")), f.E(homes.Get("foe2")) });
                    }
                    else
                    {
                        var e = f.E(f.St("envoys").Table.Get("foe2"));
                        if (e != null) f.Tp(Hero(), e.Pos.X + 3, e.Pos.Z);
                        f.Sec(2);
                        f.Check("sided with the second", f.St("chosen").String == "foe2" && me.IsFriend(f.P("foe2")) && me.IsEnemy(f.P("foe")));
                        f.Sec(70);
                        f.Kill(new[] { f.E(homes.Get("foe")) });
                    }
                    f.Sec(12);
                    break;
                }
            case 5:
                {
                    double t0 = f.StN("titanAt");
                    foreach (var fv in f.St("forges").Table.Values) { f.Kill(new[] { f.E(fv.Table.Get("b")) }); f.Sec(1); }
                    f.Check("forges set the Titan back", f.StN("titanAt") == t0 + 9 * 60 && R.IsDone("forges"));
                    f.S.Lua.M.Get("state").Table["titanAt"] = R.Time + 1; f.Sec(3);
                    var titan = f.StE("titan");
                    f.Check("the Titan walks", titan is { Alive: true } && titan.MaxHp > 0);
                    f.Kill(new[] { titan }); f.Sec(2);
                    f.Kill(new[] { f.StE("home") }); f.Sec(12);
                    break;
                }
        }
    }
}
