// "Test map" of a map with triggers (docs/MAP_EDITOR.md "Test map"): a skirmish has no trigger engine - only a
// campaign mission runs one (MissionSession: map.lua's mission table, world.lua, triggers.lua, custom.lua) - so a map
// whose triggers or custom.lua have anything is written as a mission folder from the editor's snapshot and started as
// a mission (GameMode.Campaign with MissionFolder = that folder). A mission (a map with map.lua's mission table) plays
// with its own players; a plain map gets a mission table made here: you on the first start location (the tribe of
// map's player slots: the first a human may play, its main building, start units and the tribe's start stock), the
// others computer players as the map's slots set them (teams, diplomacy), awake (Dodo builds and attacks). The map's placed objects become world.lua's objects (with GUIDs
// made from their names, as the region and trigger tools name them) when the map has no world objects of its own.
// No Godot types.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor.Triggers;

public static class TestMission
{
    static readonly Logger L = Log.For("Editor");
    /// <summary>the folder names Test map writes (below the temporary folder): a mission folder of this form is the editor's</summary>
    public const string Prefix = "pw-editor-test-";
    static int s_n;

    /// <summary>is a GameStart's mission folder one of Test map's</summary>
    public static bool IsTestFolder(string? folder) => !string.IsNullOrEmpty(folder) && Path.GetFileName(folder.TrimEnd('/', '\\')).StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>the folder for the next test run (the previous runs' folders of this process are removed)</summary>
    public static string NewFolder(string? root = null)
    {
        root ??= Path.GetTempPath();
        try
        {
            foreach (var d in Directory.GetDirectories(root, Prefix + Environment.ProcessId + "-*")) Directory.Delete(d, true);
        }
        catch (Exception e) { L.Debug("old test folders: " + e.Message); }
        return Path.Combine(root, $"{Prefix}{Environment.ProcessId}-{++s_n}");
    }

    /// <summary>removes the test folders of this process (leaving the editor)</summary>
    public static void Clean(string? root = null)
    {
        root ??= Path.GetTempPath();
        try { foreach (var d in Directory.GetDirectories(root, Prefix + Environment.ProcessId + "-*")) Directory.Delete(d, true); }
        catch (Exception e) { L.Debug("test folders: " + e.Message); }
    }

    /// <summary>removes the test folders earlier runs left behind (at the start): those of processes that no longer run</summary>
    public static void CleanStale(string? root = null)
    {
        root ??= Path.GetTempPath();
        int n = 0;
        try
        {
            foreach (var d in Directory.GetDirectories(root, Prefix + "*"))
            {
                var rest = Path.GetFileName(d)[Prefix.Length..];
                int dash = rest.IndexOf('-');
                if (dash > 0 && int.TryParse(rest[..dash], out var pid) && pid != Environment.ProcessId && Alive(pid)) continue;
                try { Directory.Delete(d, true); n++; } catch (Exception e) { L.Debug($"{d}: {e.Message}"); }
            }
        }
        catch (Exception e) { L.Debug("stale test folders: " + e.Message); }
        if (n > 0) L.Info($"removed {n} test map folders of earlier runs");
    }

    static bool Alive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception) { return false; }
    }

    /// <summary>writes the document (a snapshot) as a mission folder; playable: the configuration's playable tribes (a
    /// plain map's players: the map's player slots, as Test map's skirmish takes them - PlayersSetup); startUnits: a
    /// tribe's start units (Rules.StartOf(tribe).Units; null: three workers)</summary>
    public static string Write(IEditorMapDoc doc, string folder, IReadOnlyCollection<string> playable, Func<string, IReadOnlyList<string>>? startUnits = null,
        int id = 99, string key = "editor_test", string description = "Test map (map editor)")
    {
        var pw = doc.ToMap();
        var m = doc.Mission;
        var world = m.ToWorld();
        // the placed objects: a campaign mission's world.lua list (with the editor's changes); a plain map: all of the
        // map's (with the GUIDs the tools gave them) - world.lua's records first (the objects a trigger or group named),
        // then every other object - the campaign places the units and buildings, the map source the landscape (a
        // mission's map places no players' objects of objects.lua: MapSourceConfig.SkipObjects)
        if (m.World["objects"] == null || m.Mission == null)
        {
            var objs = new JsonArray();
            var have = new HashSet<string>(StringComparer.Ordinal);
            if (m.World["objects"] is JsonArray own)
                foreach (var r in own.OfType<JsonObject>()) { objs.Add(r.DeepClone()); if (r["guid"].Str() is { Length: > 0 } rg) have.Add(rg); }
            int k = objs.Count;
            // each object's GUID: the one the tools gave it (attribute "guid"), else one made from its name - and from its
            // place in the list where several objects share the name (three "hu_worker" placed from the palette: three
            // objects, not one)
            var src = doc.Objects.Where(o => !o.Attr.ContainsKey(MissionDoc.MissionOnlyAttr)).ToList();
            var named = pw.Objects.GroupBy(o => o.Name, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < pw.Objects.Count; i++)
            {
                var o = pw.Objects[i];
                var attr = new JsonObject();
                foreach (var (a, v) in o.Attr) attr[a] = v;
                string g = i < src.Count && src[i].Attr.TryGetValue(MissionDoc.GuidAttr, out var ga) && ga.Length > 0 ? ga
                    : named[o.Name] == 1 ? EdGuid.Of(o.Name) : EdGuid.Of($"{o.Name}#{i}");
                if (have.Contains(g)) continue;                       // (world.lua's own record of it is there)
                if (!used.Add(g)) { g = EdGuid.Of($"{o.Name}#{i}#{g}"); used.Add(g); }
                objs.Add(new JsonObject
                {
                    ["index"] = k++, ["guid"] = g, ["name"] = o.Name, ["class"] = o.Cls, ["type"] = o.Type,
                    ["owner"] = o.Owner is int ow ? JsonValue.Create(ow) : null, ["x"] = o.X, ["y"] = o.Y, ["z"] = o.Z, ["rot"] = o.Rot,
                    ["q"] = new JsonArray(o.Q.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["level"] = o.Level, ["visible"] = true, ["attr"] = attr,
                });
            }
            world["objects"] = objs;
        }
        else if (world["objects"] is JsonArray wobjs)
        {
            // a mission (a campaign mission opened in the editor): world.lua's objects are the mission's own (with the
            // editor's changes: ToWorld). An object placed in the editor that no trigger or group names has no world
            // record, and the mission's map places no players' objects of objects.lua (MapSourceConfig.SkipObjects) -
            // so every such object the map source would leave out gets a world record here, as on a plain map: alive in
            // the mission, the mission's own objects not twice (they are bound to their records by GUID)
            int k = wobjs.OfType<JsonObject>().Select(r => r["index"].Int() + 1).DefaultIfEmpty(0).Max();
            var have = new HashSet<string>(wobjs.OfType<JsonObject>().Select(r => r["guid"].Str()), StringComparer.Ordinal);
            var names = new HashSet<string>(wobjs.OfType<JsonObject>().Select(r => r["name"].Str()), StringComparer.Ordinal);
            var all = doc.Objects.ToList();
            int added = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var o = all[i];
                if (o.Attr.ContainsKey(MissionDoc.GuidAttr) || !DroppedInMission(o)) continue;
                if (names.Contains(o.Name)) continue;                 // (a name world.lua has: the campaign would skip it as that object's)
                string g = EdGuid.Of($"{o.Name}#placed#{i}");
                if (!have.Add(g)) continue;
                var attr = new JsonObject();
                foreach (var (a, v) in o.Attr) attr[a] = v;
                wobjs.Add(new JsonObject
                {
                    ["index"] = k++, ["guid"] = g, ["name"] = o.Name, ["class"] = o.Cls, ["type"] = o.Type,
                    ["owner"] = o.Owner is int ow ? JsonValue.Create(ow) : null, ["x"] = o.X, ["y"] = o.Y, ["z"] = o.Z, ["rot"] = o.Rot,
                    ["q"] = new JsonArray(o.Q.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["level"] = o.Level, ["visible"] = true, ["attr"] = attr,
                });
                added++;
            }
            if (added > 0) L.Info($"{added} objects placed in the editor without a trigger or group naming them: in the mission's world");
        }
        // every object, group, region ... a trigger names by GUID is "addressed" (the campaign keeps such scenery an object of its own)
        if (world["refs"] is not JsonObject refs) world["refs"] = refs = new JsonObject();
        foreach (var r in m.Regions) refs.TryAdd(r.Guid, new JsonArray("region", r.Name));
        foreach (var g in m.Groups) refs.TryAdd(g.Guid, new JsonArray("group", g.Name));
        var byGuid = m.Objects().GroupBy(o => o.Guid).ToDictionary(x => x.Key, x => x.First());
        foreach (var t in m.Triggers.Triggers)
            foreach (var n in t.Conditions.Concat(t.Actions))
                foreach (var p in n.Params)
                    if (p.Value.Value is { Length: 32 } v && byGuid.TryGetValue(v, out var ob)) refs.TryAdd(v, new JsonArray("object", ob.Name));
        pw.World = world;
        pw.Triggers = m.Triggers.Write();
        pw.Custom = m.Custom;
        pw.Mission = m.ToMission() ?? MakeMission(doc, pw, playable, startUnits, id, key, description);
        pw.Texts = m.ToTexts();
        if (pw.Texts.Count == 0) pw.Texts["uk"] = new JsonObject();
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        PwMapWriter.WriteFolder(pw, folder);
        L.Info($"{(key == "editor_test" ? "Test map with triggers" : "a map with triggers")}: {doc.Name} as a mission in {folder} ({m.Triggers.Triggers.Count()} triggers)");
        return folder;
    }

    /// <summary>does a mission's map leave the object out (MapSource.OriginalSource with SkipObjects: the players' buildings,
    /// characters, vehicles, ships and the animals with an owner are the campaign's to place - from world.lua)</summary>
    public static bool DroppedInMission(MapObject o) => o.Type is "BLDG" or "CHTR" or "VHCL" or "SHIP" || (o.Type == "ANML" && o.Owner != null);

    /// <summary>a plain map's mission table: its player slots with a start location (the first a human may play is
    /// yours, the others computer players, awake: Dodo), their tribes (Random: the first the slot allows), teams,
    /// diplomacy, start resources, main building, the tribe's start units</summary>
    static JsonObject MakeMission(IEditorMapDoc doc, PwMap pw, IReadOnlyCollection<string> playable, Func<string, IReadOnlyList<string>>? startUnits, int missionId, string key, string description)
    {
        var P = doc.GetPlayers();
        var act = PlayersSetup.ActiveSlots(doc);
        if (act.Count == 0) act = new List<int> { 0, 1 };
        int human = act.FirstOrDefault(i => P.Slots[i].Control != "computer", act[0]);
        var slocs = pw.Objects.Where(o => o.Type == "SLOC" && o.Owner != null).GroupBy(o => o.Owner!.Value).ToDictionary(g => g.Key, g => g.First());
        string humanTribe = "Hu";
        var players = new JsonArray();
        for (int id = 0; id < MapPlayerSettings.SLOTS; id++)
        {
            var ps = P.Slots[id];
            bool present = act.Contains(id);
            var allowed = (ps.Tribes.Count > 0 ? ps.Tribes : MapPlayerSettings.TRIBES.ToList()).Where(playable.Contains).ToList();
            string tribe = !present ? "" : allowed.Contains(ps.Tribe) ? ps.Tribe : allowed.FirstOrDefault() ?? playable.FirstOrDefault() ?? "Hu";
            if (id == human) humanTribe = tribe;
            var p = new JsonObject
            {
                ["id"] = id, ["type"] = id == human ? "human" : "ai", ["control"] = id == human ? "human" : "ai_Dodo", ["present"] = present, ["tribe"] = tribe,
                ["tribes"] = new JsonArray(tribe), ["name"] = id == human ? "Player" : $"Computer {id + 1}", ["team"] = ps.Team, ["color"] = id,
                ["resources"] = ps.HasResources
                    ? new JsonObject { ["food"] = ps.Food, ["wood"] = ps.Wood, ["stone"] = ps.Stone, ["iron"] = ps.Skulls }
                    : new JsonObject { ["food"] = -1, ["wood"] = -1, ["stone"] = -1, ["iron"] = -1 },
                ["diplomacy"] = new JsonArray(Enumerable.Range(0, 8).Select(o => (JsonNode?)JsonValue.Create(o == id ? 2 : ps.Diplomacy[o])).ToArray()),
                ["tech_filters"] = new JsonArray(), ["include_buildings"] = present && ps.IncludeBuildings,
            };
            if (ps.PopMax > 0) p["population_limit"] = ps.PopMax;
            if (present)
            {
                var sl = slocs.GetValueOrDefault(id);
                double x = sl?.X ?? pw.Hx * (id % 2 == 0 ? 0.6 : 1.4), y = sl?.Y ?? pw.Hy * (id % 2 == 0 ? 0.6 : 1.4);
                p["start_location"] = new JsonObject
                {
                    ["guid"] = sl != null ? EdGuid.Of(sl.Name) : EdGuid.Of("start" + id), ["name"] = sl?.Name ?? $"StartLocation_{id}", ["x"] = x, ["y"] = y,
                    ["z"] = doc.HeightAt(x, y), ["rot"] = sl?.Rot ?? 0, ["ignore_pointbuy"] = false, ["include_building"] = ps.IncludeBuildings, ["is_sequence"] = false, ["seq_filename"] = "",
                };
                var units = startUnits?.Invoke(tribe) ?? Enumerable.Repeat(tribe.ToLowerInvariant() + "_worker", 3).ToList();
                p["start_army"] = new JsonArray(units.Select(u => (JsonNode?)new JsonObject { ["class"] = u, ["level"] = 0 }).ToArray());
            }
            players.Add(p);
        }
        string human2 = humanTribe;
        return new JsonObject
        {
            ["id"] = missionId, ["key"] = key, ["folder"] = "", ["name"] = doc.Name, ["title"] = doc.Name, ["description"] = description,
            ["tribe"] = human2, ["point_buy"] = false, ["min_credits"] = 0, ["lang"] = doc.Mission.Lang,
            ["map_info"] = new JsonObject
            {
                ["name"] = doc.Name, ["title"] = doc.Name, ["default_camera"] = doc.Mission.StartAngle, ["black_start"] = false, ["tutorial"] = false,
                ["ai_options"] = new JsonObject(),
            },
            ["players"] = players,
        };
    }
}
