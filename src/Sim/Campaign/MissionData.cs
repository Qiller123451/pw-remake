// A mission's data as the world setup reads it: the mission folder (docs/LUA_MAPS.md) - mission.json (the menu's
// fields, map_info, players), world.json (objects, groups, regions, question marks, quests, variables, dialogues,
// sequences, refs) and the triggers of triggers.lua / custom.lua (Scripting's LuaMission: TriggerDefs with the
// parameters as written). Together they are what the JS remake had in one pw-campaign/1 document (loadCampaign:
// `data`), so the setup can follow setup.js field by field.
//
// Also the campaign index (index.json: the missions in playing order) for "next mission".
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;

namespace PwRemake.Sim.Campaign;

/// <summary>One entry of the campaign index (index.json): what the menu shows.</summary>
public sealed class MissionEntry
{
    public int Id;
    public string Key = "", Folder = "", Name = "", Title = "", Description = "", Tribe = "", Map = "", MapSource = "", Lang = "uk";
    public bool PointBuy;
    public double MinCredits;

    public static MissionEntry FromJson(JsonNode n) => new()
    {
        Id = n["id"].Int(), Key = n["key"].Str(), Folder = n["folder"].Str(), Name = n["name"].Str(), Title = n["title"].Str(),
        Description = n["description"].Str(), Tribe = n["tribe"].Str(), Map = n["map"].Str(), MapSource = n["map_source"].Str(),
        Lang = n["lang"].Str("uk"), PointBuy = n["point_buy"].Bool(), MinCredits = n["min_credits"].Num(),
    };

    /// <summary>the missions of a configuration's missions folder, in playing order (index.json; without it: the folders)</summary>
    public static List<MissionEntry> LoadIndex(string missionsDir)
    {
        var idx = Json.TryParseFile(Path.Combine(missionsDir, "index.json"));
        if (idx is JsonArray a) return a.Where(x => x != null).Select(x => FromJson(x!)).ToList();
        var res = new List<MissionEntry>();
        if (!Directory.Exists(missionsDir)) return res;
        foreach (var d in Directory.GetDirectories(missionsDir).OrderBy(x => x, StringComparer.Ordinal))
            if (Json.TryParseFile(Path.Combine(d, "mission.json")) is JsonNode m) res.Add(FromJson(m));
        return res;
    }
}

public sealed class MissionData
{
    /// <summary>the mission folder</summary>
    public string Folder = "";
    public MissionEntry Entry = new();
    /// <summary>mission.json and world.json</summary>
    public JsonNode Mission = new JsonObject(), World = new JsonObject();
    /// <summary>the triggers and the engine's data (LuaMission.Script)</summary>
    public MissionScript Script = new();
    /// <summary>the campaign index (for Next), may be empty</summary>
    public List<MissionEntry> Index = new();

    public int Id => Entry.Id;
    /// <summary>mission.json map_info (JS data.map): title, default_camera, black_start, start_time, ai_options ...</summary>
    public JsonNode Map => Mission["map_info"] ?? new JsonObject();
    public JsonArray Players => Mission["players"] as JsonArray ?? new JsonArray();
    public JsonArray Objects => World["objects"] as JsonArray ?? new JsonArray();
    public JsonArray Groups => World["groups"] as JsonArray ?? new JsonArray();
    public JsonArray Regions => World["regions"] as JsonArray ?? new JsonArray();
    public JsonArray QuestionMarks => World["question_marks"] as JsonArray ?? new JsonArray();
    public JsonObject Refs => World["refs"] as JsonObject ?? new JsonObject();
    public JsonObject Sequences => World["sequences"] as JsonObject ?? new JsonObject();
    public JsonObject Dialogs => World["dialogs"] as JsonObject ?? new JsonObject();
    public JsonArray Warnings => World["warnings"] as JsonArray ?? new JsonArray();
    public IReadOnlyList<TriggerDef> Triggers => Script.Triggers;
    /// <summary>a node's parameters with the type's defaults under them (engine.js params; the AI prescan reads them so)</summary>
    public Dictionary<string, string> ActionParams(NodeDef a)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Script.ActionDefaults.TryGetValue(a.Code, out var def)) foreach (var (k, v) in def) d[k] = v;
        foreach (var (k, v) in a.P) d[k] = v;
        return d;
    }

    /// <summary>a mission folder read by Scripting's LuaMission (its triggers already turned into TriggerDefs)</summary>
    public static MissionData FromLua(LuaMission lm, List<MissionEntry>? index = null)
    {
        var m = new MissionData
        {
            Folder = lm.Folder, Mission = lm.Mission ?? new JsonObject(), World = lm.World ?? new JsonObject(), Script = lm.Script,
            Index = index ?? new List<MissionEntry>(),
        };
        m.Entry = MissionEntry.FromJson(m.Mission);
        if (string.IsNullOrEmpty(m.Entry.Folder)) m.Entry.Folder = Path.GetFileName(lm.Folder.TrimEnd('/', '\\'));
        return m;
    }
}
