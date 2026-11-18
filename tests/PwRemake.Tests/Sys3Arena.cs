// A small arena for the sys3 tests (moves, transport, naval, animals): a flat world with a player and an enemy, a
// caster and around it enemies (characters, an animal, a vehicle, a building, a gate), a wild animal, own units
// and spirits - the targets every kind of move needs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

public static class Sys3Data
{
    static readonly Dictionary<string, (Rules R, ModelManifest M)> s_data = new();
    static string ModelsDir() => Environment.GetEnvironmentVariable("PW_MODELS") is { Length: > 0 } s ? s : "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/models";

    /// <summary>rules and manifest of a data folder (model files of PW_MODELS when there)</summary>
    public static (Rules R, ModelManifest M) Load(TestContext t, string data)
    {
        if (s_data.TryGetValue(data, out var d)) return d;
        if (!File.Exists(Path.Combine(data, "assets", "manifest.json"))) t.Skip($"no assets/manifest.json in {data}");
        var R = Rules.Load(data);
        var man = Json.ParseFile(Path.Combine(data, "assets", "manifest.json"))!;
        string md = ModelsDir();
        var M = new ModelManifest(man, null, n => { var p = Path.Combine(md, n + ".glb"); return File.Exists(p) ? p : null; });
        return s_data[data] = (R, M);
    }

    public static readonly string[] TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" };

    /// <summary>the filters of every upgrade of a tribe (as tests/golden/sys3_dump.mjs)</summary>
    public static List<string> AllUpgrades(Rules R, string tribe)
    {
        var res = new List<string>();
        var ups = R.Base.Filters[tribe]?["Upgrades"];
        if (ups == null || !ups.IsObject) return res;
        foreach (var (loc, v) in ups.Entries()) if (v.IsObject) foreach (var (a, f) in v.Entries()) if (f.IsObject) res.Add($"{tribe}/Upgrades/{loc}/{a}");
        return res;
    }

    public static void Upgrade(Player p, Rules R)
    {
        p.Tt!.Suspend();
        foreach (var f in AllUpgrades(R, p.Tribe)) p.Tt.Enable(f);
        p.Tt.Resume();
    }
}

public sealed class Sys3Arena
{
    public W_ W = null!;
    public Rules R = null!;
    public Player P = null!, Q = null!;
    public Unit Caster = null!;
    public Unit? EnemyChar, EnemyChar2, EnemyChar3, EnemyAnimal, EnemyVehicle, WildAnimal, OwnChar, OwnAnimal;
    public Building? EnemyBuilding, EnemyGate;
    public Spirit? OwnSpirit, EnemySpirit;
    public V3 Ground;

    static string? FirstOf(Rules R, ModelManifest M, string tribe, string type, Func<string, bool>? ok = null)
    {
        foreach (var n in R.IndexOrder)
        {
            var ix = R.Info(n)!;
            if (ix.Tribe != tribe || ix.Type != type || (ok != null && !ok(n))) continue;
            var g = R.Stats(n, R.MinLevel(n, null), null)?.Gfx;
            if (g != null && M.Has(g)) return n;
        }
        return null;
    }

    /// <summary>the caster `name` of `tribe` at `level` (null: the highest it has) at (0, 0) facing -z; enemies of the next
    /// tribe in front of it</summary>
    public static Sys3Arena Make(Rules R, ModelManifest M, string tribe, string name, int? level = null, bool upgraded = true, double size = 192, double? water = null, Func<double, double, double>? height = null)
    {
        var A = new Sys3Arena { R = R };
        var map = new FlatMap(size) { Water = water, HeightFn = height };
        var W = A.W = new W_(R, M, map, new WorldOptions { Seed = 7 });
        string qt = Sys3Data.TRIBES[(Array.IndexOf(Sys3Data.TRIBES, tribe) + 1) % 4];
        if (R.Base.Start["Objects"]?[qt] == null) qt = "Hu";
        A.P = new Player(0, tribe, new PlayerOpts { Rules = R, Team = 0 });
        A.Q = new Player(1, qt, new PlayerOpts { Rules = R, Team = 1 });
        W.Players.Add(A.P); W.Players.Add(A.Q);
        if (upgraded) Sys3Data.Upgrade(A.P, R);
        foreach (var p in W.Players) foreach (var r in new[] { "food", "wood", "stone" }) { p.Caps[r] = 99999; p.Res[r] = 5000; }
        int lv = level ?? Enumerable.Range(1, 5).Reverse().First(l => R.HasLevel(name, l) || l == 1);
        A.Caster = W.SpawnUnit(name, A.P, 0, 0, lv, 0) ?? throw new SkipException("no model for " + name);
        A.Ground = new V3(0, 0, -12);
        // enemies in front (-z), close together
        var ch = FirstOf(R, M, qt, "CHTR", n => n.Contains("warrior") || n.Contains("spearman") || n.Contains("lumberjack")) ?? FirstOf(R, M, qt, "CHTR");
        if (ch != null)
        {
            A.EnemyChar = W.SpawnUnit(ch, A.Q, 0, -6, 1, Math.PI);
            A.EnemyChar2 = W.SpawnUnit(ch, A.Q, 2.5, -7, 1, Math.PI);
            A.EnemyChar3 = W.SpawnUnit(ch, A.Q, -2.5, -7, 1, Math.PI);
        }
        var an = FirstOf(R, M, qt, "ANML", n => !Unit.AMPHIBIOUS.IsMatch(n) && R.Def(n, null)!.MaxPassengers == 0);
        if (an != null) A.EnemyAnimal = W.SpawnUnit(an, A.Q, 6, -9, 1, Math.PI);
        var vh = FirstOf(R, M, qt, "VHCL", n => !Unit.AMPHIBIOUS.IsMatch(n));
        if (vh != null) A.EnemyVehicle = W.SpawnUnit(vh, A.Q, -6, -9, 1, Math.PI);
        var wild = FirstOf(R, M, "World", "ANML", n => !n.Contains("fish") && R.Def(n, null)!.Aggressive != 1 && !Unit.AMPHIBIOUS.IsMatch(n));
        if (wild != null) A.WildAnimal = W.SpawnUnit(wild, null, 0, 8, null, 0);
        A.EnemyBuilding = W.PlaceBuilding("hu_warehouse", A.Q, 20, -30, 0, true);
        var gate = FirstOf(R, M, qt, "BLDG", n => R.Def(n, null)!.Gate) ?? FirstOf(R, M, "Hu", "BLDG", n => R.Def(n, null)!.Gate);
        if (gate != null) A.EnemyGate = W.PlaceBuilding(gate, A.Q, -24, -30, 0, true);
        // own units behind
        var oc = FirstOf(R, M, tribe, "CHTR", n => n.Contains("warrior") || n.Contains("spearman") || n.Contains("lumberjack") || n.Contains("worker")) ?? FirstOf(R, M, tribe, "CHTR");
        if (oc != null) A.OwnChar = W.SpawnUnit(oc, A.P, 3, 4, 1, 0);
        var oa = FirstOf(R, M, tribe, "ANML", n => !Unit.AMPHIBIOUS.IsMatch(n));
        if (oa != null) { A.OwnAnimal = W.SpawnUnit(oa, A.P, -5, 6, 1, 0); if (A.OwnAnimal != null) A.OwnAnimal.Hp = A.OwnAnimal.MaxHp / 3; }
        // spirits of fallen units next to the caster (Resurrect: own; MIRAGE reanimation: anyone's)
        if (ch != null) { A.EnemySpirit = new Spirit { Owner = A.Q, Pos = new V3(-3, 0, 3), Until = 1e9, Name = ch, Level = 1 }; W.Spirits.Add(A.EnemySpirit); }
        if (oc != null) { A.OwnSpirit = new Spirit { Owner = A.P, Pos = new V3(3, 0, 1), Until = 1e9, Name = oc, Level = 1 }; W.Spirits.Add(A.OwnSpirit); }
        return A;
    }

    public void Step(double sec) { int n = (int)Math.Round(sec / W_.STEP); for (int i = 0; i < n; i++) W.Step(); }

    /// <summary>the target a move of this kind is used on in the arena (entity, point)</summary>
    public (Entity? T, (double X, double Z)? Pos) TargetFor(MoveDef M)
    {
        switch (M.Target)
        {
            case null: return (null, null);
            case "enemy": case "unit": return (EnemyChar, null);
            case "own": return (OwnChar, null);
            case "building": return (EnemyBuilding, null);
            case "ground": return (null, (Ground.X, Ground.Z));
            case "animal": return (WildAnimal, null);
            case "vehicle": return (EnemyVehicle, null);
            case "gate": return (EnemyGate, null);
            case "spirit": return (null, M.AnySpirit ? (EnemySpirit!.Pos.X, EnemySpirit.Pos.Z) : (OwnSpirit!.Pos.X, OwnSpirit.Pos.Z));
        }
        return (null, null);
    }
}
