// Shared helpers of the sys2 tests (economy, construction, production, buildings, wall map, wall walk, siege):
// the rules and model manifest of a data folder, a flat test world with players.
//
// Game data: PW_DATA / PW_DATA_MIRAGE (default /opt/data/base, /opt/data/mirage); model files (.glb) for clip lengths
// and links: PW_MODELS (default the staged export of the user's PC) - without them the world runs on the manifest
// alone (no clips: the systems' fallbacks).
using System;
using System.Collections.Generic;
using System.IO;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

public static class Sys2
{
    static readonly Dictionary<string, (Rules R, ModelManifest M)> s_data = new();

    public static string Base => Environment.GetEnvironmentVariable("PW_DATA") is { Length: > 0 } s ? s : "/opt/data/base";
    public static string Mirage => Environment.GetEnvironmentVariable("PW_DATA_MIRAGE") is { Length: > 0 } s ? s : "/opt/data/mirage";
    static string ModelsDir() => Environment.GetEnvironmentVariable("PW_MODELS") is { Length: > 0 } s ? s : "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/models";

    public static (Rules R, ModelManifest M) Load(TestContext t, string data)
    {
        if (!File.Exists(Path.Combine(data, "assets", "manifest.json"))) t.Skip($"no game data in {data}");
        lock (s_data)
        {
            if (s_data.TryGetValue(data, out var d)) return d;
            var R = Rules.Load(data);
            var man = Json.ParseFile(Path.Combine(data, "assets", "manifest.json"))!;
            string md = ModelsDir();
            var M = new ModelManifest(man, null, n => { var p = Path.Combine(md, n + ".glb"); return File.Exists(p) ? p : null; });
            return s_data[data] = (R, M);
        }
    }

    /// <summary>a flat world (or with a height function) with players 1 (tribe) and 2 (enemy) on teams 1 and 2</summary>
    public static (W_ W, Player P, Player E) World(TestContext t, string data, string tribe = "Hu", string enemy = "Aje", Func<double, double, double>? height = null, double size = 256)
    {
        var (R, M) = Load(t, data);
        var w = new W_(R, M, new FlatMap(size) { HeightFn = height }, new WorldOptions { Seed = 7 });
        var p = new Player(1, tribe, new PlayerOpts { Rules = R, Team = 1 });
        var e = new Player(2, enemy, new PlayerOpts { Rules = R, Team = 2, Ai = true });
        w.Players.Add(p); w.Players.Add(e);
        return (w, p, e);
    }

    public static void Run(W_ w, double seconds) { int n = (int)Math.Round(seconds / W_.STEP); for (int i = 0; i < n; i++) w.Step(); }

    /// <summary>the action of a player by id (Build / Upgrades / Moves)</summary>
    public static ActionDef? Action(W_ w, Player p, string id, string? kind = null)
    {
        foreach (var a in w.Data.Actions(p)) if (a.Id == id && (kind == null || a.Kind == kind)) return a;
        return null;
    }

    /// <summary>FNV-1a of a string (UTF-16 code units, as the golden dump's fnvStr)</summary>
    public static uint Fnv(string s) { uint h = 0x811c9dc5; foreach (char c in s) { h ^= c; h *= 0x01000193; } return h; }
}
