// Golden tests of the rules: the JS remake's rules (remake/src/game/rules.js, run in node by tests/golden/rules_dump.mjs)
// and the C# port (src/Sim/Rules) answer the same broad sample of queries on the same data - stats, costs, build
// times, weapons, requirements, action lists per tribe, with and without upgrades - and every value must agree.
//
// Needs node and the JS sources (PW_REMAKE_SRC, default /home/claude/pw-exporter/remake/src) and the data folders of
// both configurations (PW_DATA / PW_DATA_MIRAGE, default /opt/data/base and /opt/data/mirage); skipped without them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Tests;

[TestClass]
public static class RulesGoldenTests
{
    public static void GoldenBase(TestContext t) => Run(t, RulesTestData.Base(t));
    public static void GoldenMirage(TestContext t) => Run(t, RulesTestData.Mirage(t));

    static void Run(TestContext t, string data)
    {
        var dump = RulesTestData.JsDump(t, data);
        var R = Rules.Load(data);
        var diffs = new List<string>();
        int compared = 0;
        foreach (var sc in dump["scenarios"]!.AsArray())
        {
            string label = sc!["label"].Str();
            string? tribe = sc["tribe"] is JsonValue ? sc["tribe"].Str() : null;
            var owner = tribe == null ? null : new TestOwner(R.NewTree(tribe), tribe);
            foreach (var f in sc["filters"]!.AsArray()) owner!.Tt!.Enable(f.Str());
            if (owner != null) t.Equal(sc["version"]!.GetValue<int>(), owner.Tt!.Version, $"{label}: tree version after the filters");
            foreach (var (name, jsc) in sc["classes"]!.AsObject())
            {
                var mine = RulesGolden.ClassNode(R, name, owner);
                Compare(jsc, mine, $"{label}/{name}", diffs);
                compared++;
            }
        }
        var misc = dump["misc"]!;
        Compare(misc, RulesGolden.Misc(R, misc), "misc", diffs);
        foreach (var d in diffs.Take(40)) t.Log(d);
        t.Log($"{data}: {compared} class queries in {dump["scenarios"]!.AsArray().Count} scenarios, {diffs.Count} differences");
        t.True(diffs.Count == 0, $"the C# rules answer as the JS rules ({diffs.Count} differences, first: {diffs.FirstOrDefault()})");
    }

    sealed class TestOwner : IRulesOwner
    {
        public TestOwner(TechTree tt, string tribe) { Tt = tt; Tribe = tribe; }
        public TechTree? Tt { get; }
        public string Tribe { get; }
        public bool Debug => false;
        public int MaxEpoch => 0;
        public bool ForeignActions => false;
    }

    /// <summary>structural comparison; numbers with a relative tolerance of 1e-9</summary>
    public static void Compare(JsonNode? js, JsonNode? cs, string path, List<string> diffs)
    {
        if (diffs.Count > 500) return;
        if (js == null || cs == null)
        {
            if (js != null || cs != null) diffs.Add($"{path}: JS {Show(js)} / C# {Show(cs)}");
            return;
        }
        switch (js)
        {
            case JsonObject jo:
                if (cs is not JsonObject co) { diffs.Add($"{path}: JS object / C# {Show(cs)}"); return; }
                foreach (var (k, v) in jo) Compare(v, co.ContainsKey(k) ? co[k] : null, path + "." + k, diffs);
                foreach (var (k, v) in co) if (!jo.ContainsKey(k) && v != null) diffs.Add($"{path}.{k}: only in C# ({Show(v)})");
                return;
            case JsonArray ja:
                if (cs is not JsonArray ca) { diffs.Add($"{path}: JS array / C# {Show(cs)}"); return; }
                if (ja.Count != ca.Count) { diffs.Add($"{path}: JS {ja.Count} items {Show(ja)} / C# {ca.Count} {Show(ca)}"); return; }
                for (int i = 0; i < ja.Count; i++) Compare(ja[i], ca[i], $"{path}[{i}]", diffs);
                return;
            case JsonValue jv:
                if (cs is not JsonValue cv) { diffs.Add($"{path}: JS {Show(js)} / C# {Show(cs)}"); return; }
                var jk = jv.GetValueKind(); var ck = cv.GetValueKind();
                if (jk == JsonValueKind.Number && ck == JsonValueKind.Number)
                {
                    double a = jv.GetValue<double>(), b = cv.GetValue<double>();
                    if (Math.Abs(a - b) > 1e-9 * Math.Max(1, Math.Abs(a))) diffs.Add($"{path}: JS {a} / C# {b}");
                    return;
                }
                if (jk != ck || jv.ToJsonString() != cv.ToJsonString()) diffs.Add($"{path}: JS {Show(js)} / C# {Show(cs)}");
                return;
        }
    }

    static string Show(JsonNode? n) { var s = n?.ToJsonString() ?? "null"; return s.Length > 160 ? s.Substring(0, 160) + "…" : s; }
}

/// <summary>The data folders and the JS dump of the golden tests.</summary>
public static class RulesTestData
{
    public static string Base(TestContext t) => Dir(t, "PW_DATA", "/opt/data/base");
    public static string Mirage(TestContext t) => Dir(t, "PW_DATA_MIRAGE", "/opt/data/mirage");

    static string Dir(TestContext t, string env, string def)
    {
        var d = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrEmpty(d)) d = def;
        if (!File.Exists(Path.Combine(d, "techtree.json"))) t.Skip($"no game data ({env} / {def})");
        return d;
    }

    public static string RemakeSrc() => Environment.GetEnvironmentVariable("PW_REMAKE_SRC") is { Length: > 0 } s ? s : "/home/claude/pw-exporter/remake/src";

    /// <summary>runs tests/golden/rules_dump.mjs in node (once per data folder and test run)</summary>
    public static JsonNode JsDump(TestContext t, string data)
    {
        string src = RemakeSrc();
        if (!File.Exists(Path.Combine(src, "game", "rules.js"))) t.Skip($"JS remake sources not found ({src}; PW_REMAKE_SRC)");
        string script = Path.Combine(RepoRoot(), "tests", "golden", "rules_dump.mjs");
        string outDir = Path.Combine(Path.GetTempPath(), "pwremake-golden");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, "rules_" + Math.Abs(data.GetHashCode()).ToString(CultureInfo.InvariantCulture) + ".json");
        if (!s_done.Contains(outFile))
        {
            var psi = new ProcessStartInfo("node", $"\"{script}\" \"{src}\" \"{data}\" \"{outFile}\"") { RedirectStandardError = true, RedirectStandardOutput = true };
            Process? p;
            try { p = Process.Start(psi); } catch (Exception e) { t.Skip("node not available: " + e.Message); return null!; }
            p!.WaitForExit(300000);
            string err = p.StandardError.ReadToEnd();
            if (p.ExitCode != 0) throw new Exception($"rules_dump.mjs failed ({p.ExitCode}): {err}");
            s_done.Add(outFile);
        }
        return Json.ParseFile(outFile)!;
    }
    static readonly HashSet<string> s_done = new();

    public static string RepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (d != null && !Directory.Exists(Path.Combine(d, "tests", "golden"))) d = Path.GetDirectoryName(d);
        return d ?? throw new Exception("repository root not found");
    }
}
