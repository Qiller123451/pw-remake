// The remake's own campaigns as folders (docs/SEQUEL.md "Files"): game/campaigns/containment (Containment 1964 and
// its 1966 missions) and game/campaigns/scenarios, one folder per mission (NN_name/mission.json + mission.lua), with
// the campaign's tables beside them (containment: classes.json, texts.json, cast.json; scenarios: tribes.json). They
// are part of the game (the repository), not made from the installation: the sequel needs nothing of the original
// but its rules and models (the MIRAGE configuration for Containment, any for the scenarios).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Sequel;

/// <summary>a mission of the remake's own campaigns, as a menu shows it</summary>
public sealed class SequelEntry
{
    /// <summary>"sequel" (Containment) | "scenario"</summary>
    public string Kind = "";
    public int Id;
    public string Folder = "", Title = "", Place = "", Date = "";
    public List<string> Briefing = new();
    /// <summary>the classes it brings in (Containment)</summary>
    public List<string> Unlocks = new();
    /// <summary>"1964" | "1966" (Containment); the scenario's setting</summary>
    public string Chapter = "";
    public JsonObject Json = new();
}

public static class SequelCatalog
{
    static readonly Logger L = Log.For("Sequel");
    public const string CONTAINMENT = "containment", SCENARIOS = "scenarios";
    /// <summary>Containment is played with the MIRAGE mod's data (its SEAS: more units and buildings, the heroes'
    /// bonuses for the SEAS) - rules.js SEQUEL_MOD</summary>
    public const string SEQUEL_MOD = "MIRAGE";
    /// <summary>the scenarios' tribes</summary>
    public static readonly string[] SCENARIO_TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" };

    /// <summary>game/campaigns next to the running game (or PW_CAMPAIGNS)</summary>
    public static string Root()
    {
        var env = Environment.GetEnvironmentVariable("PW_CAMPAIGNS");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "game", "campaigns");
            if (Directory.Exists(c)) return c;
            c = Path.Combine(d.FullName, "campaigns");
            if (Directory.Exists(c) && Directory.Exists(Path.Combine(c, CONTAINMENT))) return c;
        }
        return Path.Combine("game", "campaigns");
    }

    public static string Dir(string kind) => Path.Combine(Root(), kind == "scenario" ? SCENARIOS : CONTAINMENT);

    static readonly Dictionary<string, List<SequelEntry>> _cache = new();
    /// <summary>the missions of a campaign ("sequel" | "scenario") in their order</summary>
    public static List<SequelEntry> List(string kind)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(kind, out var l)) return l;
            l = new List<SequelEntry>();
            var dir = Dir(kind);
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var mj = Path.Combine(f, "mission.json");
                    if (!File.Exists(mj)) continue;
                    try
                    {
                        var j = (JsonObject)Json.ParseFile(mj)!;
                        var e = new SequelEntry
                        {
                            Kind = kind, Id = j["id"].Int(), Folder = f, Title = j["title"].Str(), Place = j["place"].Str(), Date = j["date"].Str(), Json = j,
                            Chapter = kind == "scenario" ? j["setting"].Str() : j["id"].Int() >= 12 ? "1966" : "1964",
                        };
                        if (j["briefing"] is JsonArray b) foreach (var p in b) e.Briefing.Add(p.Str());
                        if (j["unlocks"] is JsonArray u) foreach (var x in u) e.Unlocks.Add(x is JsonArray xa ? xa[0].Str() : x.Str());
                        l.Add(e);
                    }
                    catch (Exception ex) { L.Error($"{mj}: {ex.Message}"); }
                }
            else L.Warn($"no {kind} missions: {dir} does not exist");
            l.Sort((a, b) => a.Id.CompareTo(b.Id));
            _cache[kind] = l;
            return l;
        }
    }
    public static SequelEntry? Find(string kind, int id) => List(kind).FirstOrDefault(e => e.Id == id);
    public static int LastMission => List("sequel").Count > 0 ? List("sequel").Max(e => e.Id) : 0;

    /// <summary>the classes a mission lets the player build: those of its own and the earlier missions' unlocks</summary>
    public static HashSet<string> UnlockedBy(int id)
    {
        var o = new HashSet<string>();
        foreach (var m in List("sequel")) if (m.Id <= id) foreach (var u in m.Unlocks) o.Add(u);
        return o;
    }
    /// <summary>the classes later missions bring in (their build actions are left out of the rules)</summary>
    public static HashSet<string> LockedBefore(int id)
    {
        var open = UnlockedBy(id);
        var all = new HashSet<string>();
        foreach (var m in List("sequel")) foreach (var u in m.Unlocks) all.Add(u);
        all.ExceptWith(open);
        return all;
    }

    /// <summary>a scenario's definition for the tribe and difficulty played (mission.json: its fields + variants[tribe]
    /// (or variants[tribe][difficulty]) - the browser remake's make(tribe, difficulty)); Containment: the file</summary>
    public static JsonObject Definition(SequelEntry e, string tribe, int difficulty)
    {
        var d = (JsonObject)e.Json.DeepClone();
        if (e.Kind != "scenario") return d;
        var vs = d["variants"] as JsonObject;
        d.Remove("variants");
        if (vs?[tribe] is JsonObject v)
        {
            if (v["0"] is JsonObject && v["1"] is JsonObject) v = (JsonObject)v[Math.Clamp(difficulty, 0, 2).ToString()]!;
            foreach (var (k, x) in v) d[k] = x?.DeepClone();
        }
        return d;
    }

    /// <summary>the campaign's tables (classes.json, texts.json of Containment)</summary>
    public static SequelClasses Classes()
    {
        var dir = Dir("sequel");
        return SequelClasses.FromJson(Json.TryParseFile(Path.Combine(dir, "classes.json")), Json.TryParseFile(Path.Combine(dir, "texts.json")));
    }
    /// <summary>the radio's people: name, colour, the portrait card (cast.json)</summary>
    public static JsonObject Cast() => Json.TryParseFile(Path.Combine(Dir("sequel"), "cast.json")) as JsonObject ?? new JsonObject();
}
