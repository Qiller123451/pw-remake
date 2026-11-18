// Helpers of the sys1 tests (combat, effects, healing, finishing, items, game over, stats): the rules and model data
// of a data folder, a flat world with players, the JS dump of tests/golden/sys1_combat_dump.mjs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

public static class Sys1
{
    static readonly Dictionary<string, (Rules R, ModelManifest M)> s_data = new();

    public static string ModelsDir() => Environment.GetEnvironmentVariable("PW_MODELS") is { Length: > 0 } s ? s : "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/models";

    /// <summary>rules and manifest of a data folder (the models of PW_MODELS when they are there)</summary>
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

    /// <summary>the same rules and manifest, but no model files: every model has no clips and the default size (the JS dump's)</summary>
    public static (Rules R, ModelManifest M) LoadBare(TestContext t, string data)
    {
        string key = "bare|" + data;
        if (s_data.TryGetValue(key, out var d)) return d;
        if (!File.Exists(Path.Combine(data, "assets", "manifest.json"))) t.Skip($"no assets/manifest.json in {data}");
        var R = s_data.TryGetValue(data, out var full) ? full.R : Rules.Load(data);
        var man = Json.ParseFile(Path.Combine(data, "assets", "manifest.json"))!;
        var M = new ModelManifest(man, null, _ => null);
        return s_data[key] = (R, M);
    }

    public static W_ NewWorld(Rules R, ModelManifest M, double size = 256, ulong seed = 1, bool multiplayer = false) =>
        new(R, M, new FlatMap(size), new WorldOptions { Seed = seed, Multiplayer = multiplayer });

    public static Player AddPlayer(W_ W, Rules R, int id, string tribe, int? team = null)
    {
        var p = new Player(id, tribe, new PlayerOpts { Rules = R, Team = team });
        W.Players.Add(p);
        return p;
    }

    /// <summary>an upgrade of a player as production.js completeUpgrade switches it (the filters of its results)</summary>
    public static bool Invent(Rules R, Player p, string id)
    {
        var a = R.Actions(p).Find(x => x.Id == id);
        if (a == null) return false;
        foreach (var r in a.Results)
        {
            var path = System.Text.RegularExpressions.Regex.Replace(r.Path ?? "", "^/?Filters/", "");
            if (path == "" || r.Cls == "local") continue;
            if (r.Type == "deactivate") p.Tt!.Disable(path); else p.Tt!.Enable(path);
        }
        if (a.Results.Count == 0) p.Tt!.Enable(System.Text.RegularExpressions.Regex.Replace(a.Path, "^/Actions/", ""));
        return true;
    }

    public static void Step(W_ W, double seconds) { int n = (int)Math.Round(seconds / W_.STEP); for (int i = 0; i < n; i++) W.Step(); }

    /// <summary>runs tests/golden/sys1_combat_dump.mjs in node (once per data folder and test run)</summary>
    public static JsonNode JsDump(TestContext t, string data)
    {
        string src = RulesTestData.RemakeSrc();
        if (!File.Exists(Path.Combine(src, "game", "systems", "combat.js"))) t.Skip($"JS remake sources not found ({src}; PW_REMAKE_SRC)");
        string dir = Path.Combine(RepoRoot(), "tests", "golden");
        string outDir = Path.Combine(Path.GetTempPath(), "pwremake-golden");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, "sys1_combat_" + Math.Abs(data.GetHashCode()).ToString(CultureInfo.InvariantCulture) + ".json");
        if (!s_done.Contains(outFile))
        {
            var psi = new ProcessStartInfo("node", $"--no-warnings --import \"{Path.Combine(dir, "sys1_hooks.mjs")}\" \"{Path.Combine(dir, "sys1_combat_dump.mjs")}\" \"{src}\" \"{data}\" \"{outFile}\"")
            { RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = dir };
            Process? p;
            try { p = Process.Start(psi); } catch (Exception e) { t.Skip("node not available: " + e.Message); return null!; }
            var so = p!.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
            p.WaitForExit(600000);
            if (p.ExitCode != 0) throw new Exception($"sys1_combat_dump.mjs failed ({p.ExitCode}): {se.Result}");
            t.Log(so.Result.Trim());
            s_done.Add(outFile);
        }
        return Json.ParseFile(outFile)!;
    }
    static readonly HashSet<string> s_done = new();

    /// <summary>the repository (above the test binary, or above the working directory: a build with --artifacts-path)</summary>
    public static string RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            while (d != null && !Directory.Exists(Path.Combine(d, "tests", "golden"))) d = Path.GetDirectoryName(d);
            if (d != null) return d;
        }
        throw new Exception("repository root not found");
    }
}
