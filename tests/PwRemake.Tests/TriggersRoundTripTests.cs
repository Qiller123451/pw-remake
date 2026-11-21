// The converter and the Lua map format on the real campaign: every mission of both configurations (the official
// game and MIRAGE) is converted with tools/prepare/campaign_to_lua.py, every triggers.lua is loaded in the sandbox,
// and the triggers, conditions, actions and every parameter (defaults applied) are compared with the mission's
// pw-campaign/1 JSON. Then each mission runs two game minutes on the FakeMissionHost with its objects, groups and
// regions: no unknown type, no error (the JS remake's campaign_engine.mjs part 2).
// Data: PW_CAMPAIGN_JSON (a folder with base/ and mirage/, each index.json + single_NN.json), default
// /home/claude/testdata/campaign; skipped without it.
using System;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Content;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;

namespace PwRemake.Tests;

[TestClass]
public static class TriggersRoundTripTests
{
    static readonly Dictionary<string, string> converted = new();
    static readonly string[] EditorOnly = { "renderable", "renderable_type" };

    static string JsonDir(TestContext t, string cfg)
    {
        var root = Environment.GetEnvironmentVariable("PW_CAMPAIGN_JSON") is { Length: > 0 } e ? e : "/home/claude/testdata/campaign";
        var d = Path.Combine(root, cfg);
        if (!File.Exists(Path.Combine(d, "index.json"))) t.Skip($"no campaign JSON in {d} (PW_CAMPAIGN_JSON)");
        return d;
    }

    /// <summary>Runs the converter once per configuration into a temporary folder.</summary>
    static string Convert(TestContext t, string cfg)
    {
        if (converted.TryGetValue(cfg, out var o)) return o;
        var src = JsonDir(t, cfg);
        var outDir = Path.Combine(Path.GetTempPath(), $"pw_missions_{cfg}_{Environment.ProcessId}");
        var psi = new ProcessStartInfo("python3", new[] { Path.Combine(Repo.Root(), "tools", "prepare", "campaign_to_lua.py"), src, outDir, "--force" })
        { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd(); var se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        t.True(p.ExitCode == 0, $"converter ran ({se})");
        t.Log(so.Split('\n').LastOrDefault(l => l.Length > 0) ?? "");
        // the game reads only the map format (docs/MAP_FORMAT.md): each mission folder of the intermediate layout is
        // converted in place with its original map, as Prepare's tools/mapconvert does
        var data = Environment.GetEnvironmentVariable("PW_INSTALL_DATA") is { Length: > 0 } dd ? dd : "/mnt/user-data/uploads/Paraworld/Data";
        string[] folders = cfg == "mirage" ? new[] { "Base", "BoosterPack1", "BoosterPack3", "MIRAGE" } : new[] { "Base", "BoosterPack1" };
        foreach (var mdir in Directory.GetDirectories(outDir).OrderBy(x => x, StringComparer.Ordinal))
        {
            var mjf = Path.Combine(mdir, "mission.json");
            if (!File.Exists(mjf)) continue;
            var map = JsonNode.Parse(File.ReadAllText(mjf))?["map"].Str() ?? "";
            string? ula = PathUtil.FindCaseInsensitive(data, map);
            var rest = System.Text.RegularExpressions.Regex.Replace(PathUtil.Norm(map), "^[^/]+/", "");
            for (int i = folders.Length - 1; ula == null && i >= 0; i--) ula = PathUtil.FindCaseInsensitive(Path.Combine(data, folders[i]), rest);
            t.True(ula != null, $"{Path.GetFileName(mdir)}: its map {map} is in the installation");
            if (ula == null) continue;
            var m = PwMapConvert.FromUla(ula, map);
            PwMapConvert.AddMission(m, mdir);
            PwMapWriter.WriteFolder(m, mdir, removeOldLayout: true);
        }
        converted[cfg] = outDir;
        return outDir;
    }

    static Dictionary<string, string> Norm(NodeDef n, MissionScript m, bool action)
    {
        var defs = (action ? m.ActionDefaults : m.ConditionDefaults).GetValueOrDefault(n.Code);
        var p = TriggerParams.WithDefaults(defs, n.P).All.Where(kv => !EditorOnly.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return p;
    }

    static void RoundTrip(TestContext t, string cfg)
    {
        var dir = Convert(t, cfg);
        var src = JsonDir(t, cfg);
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "index.json")))!.AsArray();
        t.True(index.Count >= 17, $"index.json lists the missions ({index.Count})");
        int missions = 0, trig = 0, cond = 0, act = 0;
        foreach (var e in index)
        {
            var folder = e!["folder"].Str();
            t.True(e["title"].Str().Length > 0 && e["map"].Str().EndsWith(".ula") && e["tribe"].Str().Length > 0, $"{folder}: index entry has title, map, tribe");
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(src, $"single_{e["id"].Int():00}.json")))!;
            var want = MissionScript.FromCampaignJson(doc);
            var sw = Stopwatch.StartNew();
            var lm = LuaMission.Load(Path.Combine(dir, folder), Repo.Std);
            long ms = sw.ElapsedMilliseconds;
            t.True(lm.Ok && lm.Lua.Errors.Count == 0, $"{folder}: triggers.lua loads ({string.Join(" | ", lm.Lua.Errors.Take(3))})");
            var got = lm.Script;
            t.Equal(want.Triggers.Count, got.Triggers.Count, $"{folder}: triggers");
            t.Equal(want.CountConditions, got.CountConditions, $"{folder}: conditions");
            t.Equal(want.CountActions, got.CountActions, $"{folder}: actions");
            t.Equal(want.Quests.Count, got.Quests.Count, $"{folder}: quests");
            t.Equal(want.Variables.Count, got.Variables.Count, $"{folder}: variables");
            for (int i = 0; i < want.Triggers.Count; i++)
            {
                var a = want.Triggers[i]; var b = got.Triggers[i];
                string w = $"{folder} trigger {i} \"{a.Name}\"";
                t.True(a.Name == b.Name && a.Guid == b.Guid && a.Folder == b.Folder && a.Expression.Trim() == b.Expression, $"{w}: name, guid, folder, expression");
                t.True(a.Once == b.Once && a.Enabled == b.Enabled && a.Random == b.Random && a.ByDifficulty == b.ByDifficulty && a.NodeOff == b.NodeOff, $"{w}: flags");
                foreach (var (list, isAct) in new[] { (a.Conditions.Zip(b.Conditions), false), (a.Actions.Zip(b.Actions), true) })
                    foreach (var (x, y) in list)
                    {
                        t.True(x.Code == y.Code && (!isAct || x.Difficulty == y.Difficulty), $"{w}: {x.Code} type and difficulty");
                        var px = Norm(x, want, isAct); var py = Norm(y, got, isAct);
                        bool same = px.Count == py.Count && px.All(kv => py.TryGetValue(kv.Key, out var v) && v == kv.Value);
                        if (!same) t.True(false, $"{w}: {x.Code} parameters differ: {string.Join(", ", px.Where(kv => py.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => $"{kv.Key}={kv.Value} vs {py.GetValueOrDefault(kv.Key)}"))}");
                        t.Checks++;
                    }
            }
            missions++; trig += got.Triggers.Count; cond += got.CountConditions; act += got.CountActions;
            if (ms > 3000) t.Log($"{folder}: loading took {ms} ms");
        }
        t.Log($"{cfg}: {missions} missions, {trig} triggers, {cond} conditions, {act} actions: all equal to the JSON");
    }

    public static void RoundTripBase(TestContext t) => RoundTrip(t, "base");
    public static void RoundTripMirage(TestContext t) => RoundTrip(t, "mirage");

    /// <summary>Every mission from its Lua folder, two game minutes on the fake world.</summary>
    static void Run(TestContext t, string cfg)
    {
        var dir = Convert(t, cfg);
        var src = JsonDir(t, cfg);
        foreach (var e in JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "index.json")))!.AsArray())
        {
            var folder = e!["folder"].Str();
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(src, $"single_{e["id"].Int():00}.json")))!;
            var lm = LuaMission.Load(Path.Combine(dir, folder), Repo.Std);
            var h = new FakeMissionHost();
            h.LoadWorld(doc);
            var eng = new TriggerEngine(h, lm.Script);
            h.E = eng;
            lm.Bind(eng);
            h.Run(120);
            var unk = eng.UnknownConditions.Concat(eng.UnknownActions).ToList();
            t.Log($"{folder,-22} {eng.Triggers.Count,4} triggers, {eng.Triggers.Count(x => x.Fired > 0),4} fired, {eng.StatFired,5} firings, {eng.StatActions,5} actions, ended: {h.Ended?.Won.ToString() ?? "-"}");
            if (Environment.GetEnvironmentVariable("PW_TRIGGER_LOG") == folder) t.Log(eng.DebugPrint(60));
            t.True(unk.Count == 0 && eng.Errors.Count == 0, $"{folder}: {eng.Triggers.Count} triggers, {eng.Triggers.Count(x => x.Fired > 0)} fired in 2 min ({eng.StatFired} firings, {eng.StatActions} actions); unknown: {string.Join(" ", unk)}; errors: {string.Join(" | ", eng.Errors.Take(3))}");
        }
    }

    public static void RunAllBase(TestContext t) => Run(t, "base");
    public static void RunAllMirage(TestContext t) => Run(t, "mirage");
}
