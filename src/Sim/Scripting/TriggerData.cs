// The definitions the trigger engine is built from: triggers with their conditions and actions (nodes) and their
// parameters, as they come from a mission's triggers.lua (LuaMission.cs) or straight from the toolkit's
// pw-campaign/1 JSON (docs/CAMPAIGN_FORMAT.md §10.6). The parameters stay the strings of the data; the defaults of
// the node type (doc.defaults, the original's *_attrib_def.txt) are applied when the engine builds its objects.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Scripting;

/// <summary>The parameters of one condition or action: key -> string value (the data's strings, defaults applied).
/// The accessors follow the JS idioms of conditions.js / actions.js.</summary>
public sealed class TriggerParams
{
    readonly Dictionary<string, string> d;
    public TriggerParams() { d = new(StringComparer.Ordinal); }
    public TriggerParams(IEnumerable<KeyValuePair<string, string>> kv) { d = new(StringComparer.Ordinal); foreach (var (k, v) in kv) d[k] = v; }

    /// <summary>The raw value or null (JS `p.x` / `p.x == null`).</summary>
    public string? this[string key] { get => d.TryGetValue(key, out var v) ? v : null; set { if (value == null) d.Remove(key); else d[key] = value; } }
    public bool Has(string key) => d.ContainsKey(key);
    public IReadOnlyDictionary<string, string> All => d;
    public int Count => d.Count;

    /// <summary>null -> def ("" stays "").</summary>
    public string S(string key, string def = "") => d.TryGetValue(key, out var v) ? v : def;
    /// <summary>null or "" -> def.</summary>
    public string SE(string key, string def) => d.TryGetValue(key, out var v) && v.Length > 0 ? v : def;
    /// <summary>toInt(p.x) (missing = 0).</summary>
    public long I(string key) => TrigUtil.ToInt(this[key]);
    /// <summary>num(p.x, def) = parseFloat or def.</summary>
    public double N(string key, double def = 0) => TrigUtil.Num(this[key], def);
    /// <summary>on(p.x, def).</summary>
    public bool On(string key, bool def = false) => TrigUtil.On(this[key], def);
    /// <summary>slotOf(p.x, def): a player slot, missing / empty = def.</summary>
    public int Slot(string key, int def = 0) { var v = this[key]; return string.IsNullOrEmpty(v) ? def : (int)TrigUtil.ToInt(v); }

    /// <summary>A copy with other values for some keys (the REGN group test, the ACDO invulnerability region).</summary>
    public TriggerParams With(string key, string value) { var p = new TriggerParams(d); p[key] = value; return p; }

    /// <summary>The node's own parameters on top of the type's defaults (engine.js params()).</summary>
    public static TriggerParams WithDefaults(IReadOnlyDictionary<string, string>? defaults, IReadOnlyDictionary<string, string> own)
    {
        var p = new TriggerParams();
        if (defaults != null) foreach (var (k, v) in defaults) p.d[k] = v;
        foreach (var (k, v) in own) p.d[k] = v;
        return p;
    }
}

/// <summary>One condition or action of a trigger definition.</summary>
public sealed class NodeDef
{
    /// <summary>The original's 4-letter type ("TIME", "SPGR") or SCRP / CHCK for Lua code.</summary>
    public string Code = "";
    public string Note = "", Name = "";
    /// <summary>Actions: 0 easy, 1 medium, 2 hard (default 1, §1.4).</summary>
    public int Difficulty = 1;
    /// <summary>Only the parameters stored (defaults are applied by the engine).</summary>
    public Dictionary<string, string> P = new(StringComparer.Ordinal);
    /// <summary>Script / Check: the Lua function (MoonSharp Closure) and the Lua host that runs it.</summary>
    public object? Function;
    public LuaHost? Lua;
    /// <summary>Check: poll interval in seconds.</summary>
    public double Every = 0.5;
    /// <summary>Where it was defined (Lua file:line) for messages.</summary>
    public string Source = "";
}

/// <summary>One trigger definition (docs/CAMPAIGN_FORMAT.md §10.6).</summary>
public sealed class TriggerDef
{
    public string Guid = "", Name = "", Description = "", Folder = "Root", Expression = "";
    public bool Once, Enabled, Random, ByDifficulty, NodeOff;
    public List<NodeDef> Conditions = new(), Actions = new();
    public string Source = "";

    /// <summary>A trigger of the pw-campaign/1 JSON; null for one that was not compiled (it does not exist for the
    /// game, §1.2).</summary>
    public static TriggerDef? FromCampaignJson(JsonNode t)
    {
        var f = t["flags"];
        if (t["compiled"] is JsonValue cv && cv.TryGetValue(out bool c) && !c) return null;
        if (f?["disabled"].Bool() == true) return null;
        var d = new TriggerDef
        {
            Guid = t["guid"].Str(), Name = t["name"].Str(), Description = t["description"].Str(),
            Folder = t["folder"].Str("Root") is { Length: > 0 } fo ? fo : "Root", Expression = t["expression"].Str(),
            Once = f?["once"].Bool() ?? false, Enabled = f?["enabled"].Bool() ?? false, Random = f?["random"].Bool() ?? false,
            ByDifficulty = f?["by_difficulty"].Bool() ?? false, NodeOff = f?["node_off"].Bool() ?? false,
        };
        foreach (var n in t["conditions"]?.AsArray() ?? new JsonArray()) d.Conditions.Add(NodeFromJson(n!, false));
        foreach (var n in t["actions"]?.AsArray() ?? new JsonArray()) d.Actions.Add(NodeFromJson(n!, true));
        return d;
    }

    static NodeDef NodeFromJson(JsonNode n, bool action)
    {
        var nd = new NodeDef { Code = n["type"].Str(), Note = n["note"].Str(), Name = n["name"].Str() };
        if (action) nd.Difficulty = n["difficulty"] is JsonValue ? n["difficulty"].Int(1) : 1;
        if (n["p"] is JsonObject po) foreach (var (k, v) in po) nd.P[k] = v.Str();
        return nd;
    }
}

/// <summary>A quest of the mission and its state (§6.1): the flags the QUES action sets and the condition tests.</summary>
public sealed class QuestState
{
    public string Guid = "", Name = "", Headline = "", Group = "";
    public bool Main;
    /// <summary>Bonus points by difficulty (easy, medium, hard).</summary>
    public double[] Bonus = new double[3];
    public bool Visible, Accomplished, Unaccomplishable;

    public static QuestState FromJson(JsonNode q)
    {
        var b = q["bonus"];
        return new QuestState
        {
            Guid = q["guid"].Str(), Name = q["name"].Str(), Headline = q["headline"].Str(), Group = q["group"].Str(), Main = q["main"].Bool(),
            Bonus = new[] { b?["easy"].Num() ?? 0, b?["medium"].Num() ?? 0, b?["hard"].Num() ?? 0 },
            Visible = q["visible"].Bool(), Accomplished = q["accomplished"].Bool(), Unaccomplishable = q["unaccomplishable"].Bool(),
        };
    }
}

/// <summary>Everything the trigger engine needs of a mission: its triggers, the node defaults, quests, level
/// variables and texts. Made by LuaMission (a mission folder) or FromCampaignJson (a pw-campaign/1 document).</summary>
public sealed class MissionScript
{
    public List<TriggerDef> Triggers = new();
    /// <summary>doc.defaults: conditions / actions -> type -> parameter -> default.</summary>
    public Dictionary<string, Dictionary<string, string>> ConditionDefaults = new(StringComparer.Ordinal), ActionDefaults = new(StringComparer.Ordinal);
    public List<QuestState> Quests = new();
    /// <summary>Level variables at start: name -> value (all of type int in the campaign, kept as strings).</summary>
    public List<KeyValuePair<string, string>> Variables = new();
    public Dictionary<string, string> Texts = new(StringComparer.Ordinal);
    /// <summary>Paths of the sequences / dialogues the mission data has (the host plays them; informative).</summary>
    public string Name = "";

    /// <summary>The engine's part of a pw-campaign/1 document.</summary>
    public static MissionScript FromCampaignJson(JsonNode doc)
    {
        var m = new MissionScript { Name = doc["map"]?["name"].Str() ?? "" };
        ReadCommon(m, doc, doc["defaults"]);
        foreach (var t in doc["triggers"]?.AsArray() ?? new JsonArray()) { var d = TriggerDef.FromCampaignJson(t!); if (d != null) m.Triggers.Add(d); }
        return m;
    }

    /// <summary>Quests, variables, texts and the node defaults of a JSON document (pw-campaign/1 or world.json).</summary>
    public static void ReadCommon(MissionScript m, JsonNode doc, JsonNode? defaults)
    {
        if (defaults is JsonObject dfs)
        {
            ReadDefaults(dfs["conditions"], m.ConditionDefaults);
            ReadDefaults(dfs["actions"], m.ActionDefaults);
        }
        foreach (var q in doc["quests"]?.AsArray() ?? new JsonArray()) m.Quests.Add(QuestState.FromJson(q!));
        if (doc["variables"] is JsonObject vars) foreach (var (k, v) in vars) m.Variables.Add(new(k, v?["value"].Str() ?? "0"));
        if (doc["texts"] is JsonObject tx) foreach (var (k, v) in tx) m.Texts[k] = v.Str();
    }

    static void ReadDefaults(JsonNode? n, Dictionary<string, Dictionary<string, string>> into)
    {
        if (n is not JsonObject o) return;
        foreach (var (type, ps) in o)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (ps is JsonObject po) foreach (var (k, v) in po) d[k] = v.Str();
            into[type] = d;
        }
    }

    public int CountConditions => Triggers.Sum(t => t.Conditions.Count);
    public int CountActions => Triggers.Sum(t => t.Actions.Count);
}
