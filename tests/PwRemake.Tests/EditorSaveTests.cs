// Tests of the map editor's saving (docs/MAP_EDITOR.md "Saving and sharing", plan phase 7): a map of every kind - a new
// map with edits of every tool, an original multiplayer map, the remake's own map with its biome layer, campaign
// missions of both configurations, a .pwmap, an original .ula - opened, saved (a folder and a .pwmap), opened again:
// the same document (heights, materials, walk grid with the painted cells, biomes, forest, plants, objects, players,
// settings, regions, groups, variables, quests, triggers, custom.lua, texts, world.lua, the mission table); saved again:
// the same bytes. The user's maps folder: ids "User/<name>", the skirmish list's "My maps", the content hash.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Editor.Triggers;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Net;

namespace PwRemake.Tests;

[TestClass]
public static class EditorSaveTests
{
    static string Content() => Environment.GetEnvironmentVariable("PW_CONTENT") is { Length: > 0 } c ? c : "/home/claude/testcontent";
    static string Tmp(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "pw-edsave-tests", name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        if (File.Exists(d)) File.Delete(d);
        Directory.CreateDirectory(Path.GetDirectoryName(d)!);
        return d;
    }

    static readonly JsonSerializerOptions J = new() { IncludeFields = true, WriteIndented = false };
    /// <summary>as JSON without the records' "Extra" (world.lua's record as read: how the document holds a field, not what
    /// it is - world.lua itself is compared as written, "m.world")</summary>
    static string Js(object? o)
    {
        var n = JsonSerializer.SerializeToNode(o, J);
        void Strip(System.Text.Json.Nodes.JsonNode? x)
        {
            if (x is System.Text.Json.Nodes.JsonObject ob) { ob.Remove("Extra"); foreach (var (_, v) in ob) Strip(v); }
            else if (x is System.Text.Json.Nodes.JsonArray a) foreach (var v in a) Strip(v);
        }
        Strip(n);
        return n?.ToJsonString() ?? "null";
    }
    static string F(double v) => BitConverter.DoubleToInt64Bits(v).ToString("x");

    /// <summary>everything the editor shows and the map file holds, as text parts (name -> text): two documents are the
    /// same map when every part is the same</summary>
    public static SortedDictionary<string, string> State(MapDataDoc d)
    {
        var md = d.Md;
        var s = new SortedDictionary<string, string>(StringComparer.Ordinal);
        s["name"] = $"{d.Name}|{d.Setting}|{md.SettingName}|{md.TerrainSetting}|{d.MaxPlayers}|{F(md.Water)}|{md.W}x{md.H}|{md.File}|{F(md.StartTime)}|{F(md.DefaultCamera)}";
        s["heights"] = Convert.ToBase64String(md.Heights.SelectMany(BitConverter.GetBytes).ToArray());
        s["mats"] = Convert.ToBase64String(md.Mats);
        s["walk"] = md.Walk == null ? "-" : $"{md.Walk.W}x{md.Walk.H}@{md.Walk.OffX},{md.Walk.OffY}:" + Convert.ToBase64String(md.Walk.Cells);
        s["walk.paint"] = string.Concat(Enumerable.Range(0, d.WalkW * d.WalkH).Select(k => (char)('0' + (int)d.GetWalkOverride(k % d.WalkW, k / d.WalkW))));
        s["biomes"] = md.Biomes is { } b ? $"{string.Join(",", b.Biomes)}|{b.Cell}|{b.X0}|{b.Y0}|{b.W}x{b.H}|{Convert.ToBase64String(b.Weights)}|{string.Join(";", b.Remap.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + string.Join(",", kv.Value)))}" : "-";
        s["forest"] = $"{md.Forest.W}x{md.Forest.H}|" + string.Join(";", md.Forest.Blocks.Select(x => $"{x.X},{x.Y},{Convert.ToBase64String(x.B)}"));
        s["plants"] = string.Join("\n", md.Plants.Select(p => $"{p.Name} {F(p.X)} {F(p.Y)} {F(p.Z)} {F(p.Rot)} {string.Join(",", p.Q.Select(F))}"));
        s["objects"] = string.Join("\n", md.Objects.Select(o => $"{o.Type} {o.Name} {o.Cls} {F(o.X)} {F(o.Y)} {F(o.Z)} {F(o.Rot)} {string.Join(",", o.Q.Select(F))} {o.Owner} {o.Level} " +
            string.Join(",", o.Attr.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value))));
        s["regions"] = string.Join("\n", md.Regions.Select(r => r.Name + ":" + string.Join(";", r.Shapes.Select(x => $"{x.Oval},{x.Enabled},{F(x.X)},{F(x.Y)},{F(x.W)},{F(x.H)}"))));
        s["players"] = Js(d.GetPlayers());
        s["settings"] = Js(d.GetSettings());
        s["info"] = string.Join(";", md.Info.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        s["description"] = string.Join(";", md.Description.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        var m = d.Mission;
        s["m.regions"] = Js(m.Regions);
        s["m.groups"] = Js(m.Groups);
        s["m.variables"] = Js(m.Variables);
        s["m.quests"] = Js(m.Quests);
        s["m.qmarks"] = Js(m.QuestionMarks);
        s["m.cameras"] = Js(m.Cameras);
        s["m.start"] = $"{F(m.StartAngle)}|{m.StartX}|{m.StartY}|{m.Lang}";
        s["m.triggers"] = m.Triggers.Write();
        s["m.custom"] = m.Custom ?? "-";
        s["m.texts"] = Js(m.Texts);
        s["m.mission"] = m.ToMission()?.ToJsonString() ?? "-";
        s["m.world"] = m.HasTriggers || m.World.Count > 0 || m.Regions.Count > 0 ? m.ToWorld().ToJsonString() : "-";
        return s;
    }

    static void Same(TestContext t, SortedDictionary<string, string> a, SortedDictionary<string, string> b, string what)
    {
        var diff = a.Keys.Where(k => !b.TryGetValue(k, out var v) || v != a[k]).ToList();
        if (diff.Count > 0)
            foreach (var k in diff.Take(3))
            {
                string x = a[k], y = b.GetValueOrDefault(k, "<none>");
                int i = 0; while (i < x.Length && i < y.Length && x[i] == y[i]) i++;
                t.Log($"  {k} differs at {i}: ...{x.Substring(Math.Max(0, i - 60), Math.Min(140, x.Length - Math.Max(0, i - 60)))}... vs ...{y.Substring(Math.Max(0, i - 60), Math.Min(140, y.Length - Math.Max(0, i - 60)))}...");
            }
        t.True(diff.Count == 0, $"{what}: the same document{(diff.Count > 0 ? " - differs in " + string.Join(", ", diff) : "")}");
    }

    static Dictionary<string, byte[]> FilesOf(string path)
    {
        var src = new PwMapReader.Source(path);
        return src.List().ToDictionary(r => r, r => src.Get(r)!, StringComparer.Ordinal);
    }

    /// <summary>open -> save (folder) -> open: the same; save the second again: the same bytes; as a .pwmap: the same
    /// document again</summary>
    static void RoundTrip(TestContext t, MapDataDoc d, string label)
    {
        var dir = Tmp(label);
        d.Save(dir);
        t.Equal(dir, d.SourcePath, $"{label}: the document's path is the saved folder");
        var s1 = State(d);
        var d2 = MapDataDoc.Open(dir);
        Same(t, s1, State(d2), $"{label}: saved and opened again");
        var dir2 = Tmp(label + "_again");
        d2.Save(dir2);
        var f1 = FilesOf(dir); var f2 = FilesOf(dir2);
        var bad = f1.Keys.Union(f2.Keys).Where(k => !f1.TryGetValue(k, out var x) || !f2.TryGetValue(k, out var y) || !x.AsSpan().SequenceEqual(y)).ToList();
        t.True(bad.Count == 0, $"{label}: saved again, the same bytes{(bad.Count > 0 ? " - not: " + string.Join(", ", bad) : "")} ({f1.Count} files)");
        var zip = Tmp(label + ".pwmap");
        d2.Save(zip);
        t.True(File.Exists(zip) && d2.SourcePath == zip, $"{label}: exported as .pwmap");
        Same(t, s1, State(MapDataDoc.Open(zip)), $"{label}: the .pwmap opened");
        t.Equal(MapFiles.ContentHash(dir), MapFiles.ContentHash(dir2), $"{label}: the content hash of the two saves");
        t.Equal(MapFiles.ContentHash(dir), MapFiles.ContentHash(zip), $"{label}: a folder and its .pwmap: the same content hash");
    }

    /// <summary>a new map with an edit of every kind</summary>
    public static MapDataDoc Edited()
    {
        var d = MapDataDoc.CreateFlat(new NewMapSpec { Size = 384, BaseHeight = 20, Water = 16, Setting = "Jungle", Name = "Save test", Players = 2 });
        var h = new EditHistory(d);
        var b = new HeightBrush { Radius = 30, Strength = 7 };
        for (int k = 0; k < 10; k++) h.Execute(b.Step(d, 150 + k * 0.7, 160, 0.1)!, merge: true);            // a hill (heights off the 1/128 m steps)
        h.EndMerge();
        h.Execute(PaintTools.Materials(d, new HeightBrush { Radius = 20 }, 220, 220, 5)!);
        h.Execute(PaintTools.Walk(d, new HeightBrush { Radius = 6 }, 100, 100, WalkOverride.Blocked)!);
        h.Execute(PaintTools.Walk(d, new HeightBrush { Radius = 3 }, 150, 160, WalkOverride.Walkable)!);
        h.Execute(PaintTools.Biome(d, "Savanna", 280, 100, 40, 1)!);
        var fd = ForestData.LoadFile("/opt/data/base/forest.json");
        if (fd != null) h.Execute(VegetationTools.PaintForest(d, fd, new HeightBrush { Radius = 40 }, 300, 300, new ForestPaint())!);
        h.Execute(VegetationTools.ScatterPlants(d, new HeightBrush { Radius = 15 }, 80, 300, 2, new PlantScatter { Kinds = { "Jungle_Fern_01", "Jungle_Smallrock_07" }, Density = 4 }, new PwRemake.Sim.Core.SimRandom(5))!);
        var objs = new List<MapObject>
        {
            new() { Type = "BLDG", Name = "hu_headquarters_1", Cls = "hu_headquarters", X = 100.123456789, Y = 260.5, Z = 20, Owner = 0 },
            new() { Type = "CHTR", Name = "Hero", Cls = "hu_warrior", X = 110.3, Y = 250.7, Z = 20, Owner = 0, Level = 2 },
            new() { Type = "STON", Name = "resource_stone_jun_1", Cls = "resource_stone_jun", X = 200.1, Y = 100.2, Z = 20, Attr = { ["hitpoints"] = "1234" } },
        };
        foreach (var o in objs) ObjectOps.SetRot(o, 0.3);
        h.Execute(ObjectsAddRemove.Add(d, objs));
        var P0 = d.GetPlayers(); var P = P0.Clone();
        P.Slots[0].Team = 2; P.Slots[1].Food = 500; P.Slots[1].Wood = 300;
        h.Execute(new PlayersEdit(P0, P));
        var S0 = d.GetSettings(); var S = S0.Clone();
        S.Author = "Tester"; S.Descriptions["uk"] = "A test map"; S.Descriptions["de"] = "Eine Testkarte"; S.Weather = 2; S.StartTime = 9 * 3600 + 30 * 60;
        h.Execute(new SettingsEdit(S0, S));
        var m = d.Mission;
        h.Execute(MissionEdits.NewRegion(m, true, 40.3, 50.7, 30.1, 20.9, 20, "Goal"));
        h.Execute(MissionEdits.NewQuest(m, "ReachGoal"));
        h.Execute(MissionEdits.SetQuestText(m, 0, "headline", "Reach the goal"));
        h.Execute(MissionEdits.Add(m, new EdVariable { Name = "waves", Value = "3" }));
        h.Execute(MissionEdits.NewTrigger(m, "Root", "Win at the goal"));
        h.Execute(MissionEdits.ChangeTrigger(m, 0, tr =>
        {
            var c = EdNode.Of("REGN", true);
            c.Set("rgn_guid", ParamValue.Str(m.Regions[0].Guid)); c.Set("obj_owner", ParamValue.Of("0")); c.Set("obj_type", ParamValue.Str("CHTR|")); c.Set("obj_count", ParamValue.Str(">0"));
            tr.Conditions.Add(c);
            tr.Actions.Add(EdNode.Of("QUIT", false));
        }));
        var (g, _) = MissionEdits.EnsureGuid(d.Md.Objects.First(o => o.Name == "Hero"));
        if (g != null) h.Execute(g);
        return d;
    }

    public static void EditorSaveRoundTripNewMap(TestContext t)
    {
        var d = Edited();
        t.True(d.GetWalkOverride(50, 50) == WalkOverride.Blocked && d.Md.Plants.Count > 0 && d.Md.Biomes != null && d.Mission.HasTriggers, "the map has painted cells, plants, biomes, triggers");
        RoundTrip(t, d, "newmap");
        // walk.png keeps the painted cells as 64 / 192, read as walkable from 128
        var wi = Png.Read(File.ReadAllBytes(Path.Combine(Tmp("newmap_w"), "..", "newmap", PwMapPaths.Walk)));
        var v = wi.Grey8();
        t.True(v.Contains((byte)64) && v.Contains((byte)192) && v.Contains((byte)255), "walk.png: 255 walkable, 64 painted blocked, 192 painted walkable");
        var game = MapFiles.Read(Path.Combine(Path.GetTempPath(), "pw-edsave-tests", "newmap"));
        t.True(!game.Walk!.WalkableMap(101, 101) && game.Walk.WalkableMap(151, 161), "the game reads the painted cells right (>= 128 walkable)");
        var again = MapDataDoc.Open(Path.Combine(Path.GetTempPath(), "pw-edsave-tests", "newmap"));
        t.Equal(WalkOverride.Blocked, again.GetWalkOverride(50, 50), "the editor reads them back as overrides");
        t.True(again.Mission.HasTriggers && again.Mission.Regions[0].Name == "Goal", "the trigger and the region");
    }

    public static void EditorSaveRoundTripContent(TestContext t)
    {
        var root = Content();
        var maps = new List<(string Label, string Path)>();
        void Add(string label, string p) { if (Directory.Exists(p) || File.Exists(p)) maps.Add((label, p)); else t.Log($"not there: {p}"); }
        Add("multi_2_jun_001", Path.Combine(root, "configs/base/maps/Base/Maps/Multiplayer/multi_2_jun_001"));
        Add("remake_frost", Path.Combine(root, "configs/base/maps/Remake/frost_to_jungle"));
        foreach (var cfg in new[] { "base", "mirage" })
        {
            var md = Path.Combine(root, "configs", cfg, "missions");
            if (!Directory.Exists(md)) continue;
            var ms = Directory.GetDirectories(md).Where(x => File.Exists(Path.Combine(x, "map.lua"))).OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (var pick in new[] { 0, 1, ms.Count / 2, ms.Count - 1 }.Distinct().Where(i => i >= 0 && i < ms.Count)) Add(cfg + "_" + Path.GetFileName(ms[pick]), ms[pick]);
        }
        var ula = Directory.Exists("/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer")
            ? Directory.GetFiles("/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer", "*.ula").OrderBy(x => x).FirstOrDefault() : null;
        if (ula != null) Add("ula", ula);
        if (maps.Count == 0) t.Skip("no converted content (PW_CONTENT)");
        foreach (var (label, p) in maps)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var d = MapDataDoc.Open(p);
            var s0 = State(d);
            RoundTrip(t, d, label);
            // the original map as opened is the saved one too (nothing changed but the id: a saved map is the user's own)
            var diff = s0.Keys.Where(k => s0[k] != State(d)[k]).ToList();
            t.Log($"{label}: {sw.ElapsedMilliseconds} ms; unedited, the save changed {(diff.Count == 0 ? "nothing" : string.Join(", ", diff))}");
        }
    }

    /// <summary>an unchanged campaign mission saved by the editor is the converted mission byte for byte (every file; map.lua
    /// without the line `source` - a saved map is the user's own): regions.lua with the original's floats (world.lua's
    /// regions are rounded to millimetres - the editor keeps each shape's regions.lua values while it is unchanged), the
    /// converter's JSON numbers written as the reader writes them ("16.0" -> 16). Each mission is converted here as
    /// Prepare converts it now (its mission.json / world.json / texts kept beside in the test content); a moved region is
    /// written from its new box.</summary>
    public static void EditorSaveMissionSameBytes(TestContext t)
    {
        var picks = new[] { "base/11_single_11", "base/00_tutorial", "base/06_single_06", "mirage/15_single_15", "mirage/22_single_b43_seas" };
        int n = 0;
        foreach (var pick in picks)
        {
            var src = Path.Combine(Content(), "configs", pick.Split('/')[0], "missions", pick.Split('/')[1]);
            if (!File.Exists(Path.Combine(src, "map.lua"))) { t.Log($"not there: {src}"); continue; }
            var label = pick.Replace('/', '_');
            // the mission as Prepare's converter writes it now
            var conv = Tmp(label + "_conv");
            var pw = PwMapReader.Read(src);
            if (File.Exists(Path.Combine(src, "mission.json"))) PwMapConvert.AddMission(pw, src);
            PwMapWriter.WriteFolder(pw, conv);
            t.True(File.ReadAllBytes(Path.Combine(conv, PwMapPaths.Regions)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(src, PwMapPaths.Regions))), $"{label}: the converter's regions.lua (the original's floats)");
            // opened in the editor and saved unchanged
            var d = MapDataDoc.Open(conv);
            var saved = Tmp(label + "_saved");
            d.Save(saved);
            var a = FilesOf(conv); var b = FilesOf(saved);
            string NoSource(byte[] x) => string.Join("\n", Encoding.UTF8.GetString(x).Split('\n').Where(l => !l.StartsWith("  source = ", StringComparison.Ordinal)));
            var diff = a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y)
                || (k == PwMapPaths.Map ? NoSource(x) != NoSource(y) : !x.AsSpan().SequenceEqual(y))).ToList();
            foreach (var k in diff.Take(3))
                if (a.TryGetValue(k, out var x) && b.TryGetValue(k, out var y))
                {
                    var la = Encoding.UTF8.GetString(x).Split('\n'); var lb = Encoding.UTF8.GetString(y).Split('\n');
                    int i = 0; while (i < la.Length && i < lb.Length && la[i] == lb[i]) i++;
                    t.Log($"  {k} line {i + 1}: {(i < la.Length ? la[i] : "<end>")}  |  {(i < lb.Length ? lb[i] : "<end>")}");
                }
            t.True(diff.Count == 0, $"{label}: saved unchanged, the same bytes ({a.Count} files){(diff.Count > 0 ? " - not: " + string.Join(", ", diff) : "")}");
            n++;
            if (pick != picks[0]) continue;
            // a region moved: regions.lua and world.lua follow it, the other regions keep their bytes
            var h = new EditHistory(d);
            h.Execute(MissionEdits.MoveShape(d.Mission, 0, 0, 10, 0));
            d.Save(saved);
            var back = PwMapReader.Read(saved);
            t.Near(d.Mission.Regions[0].Shapes[0].X, back.Regions[0].Shapes[0].X, 1e-3, "the moved region's box in regions.lua");
            var lines0 = File.ReadAllLines(Path.Combine(conv, PwMapPaths.Regions)); var lines1 = File.ReadAllLines(Path.Combine(saved, PwMapPaths.Regions));
            t.True(lines0.Length == lines1.Length && Enumerable.Range(0, lines0.Length).Count(i => lines0[i] != lines1[i]) == 1, "regions.lua: only the moved region's line changed");
        }
        if (n == 0) t.Skip("no converted missions (PW_CONTENT)");
    }

    public static void EditorSaveUserMaps(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("configuration base not found");
        var user = Tmp("usermaps");
        Directory.CreateDirectory(user);
        var was = MapFiles.UserDir;
        MapFiles.UserDir = user;
        try
        {
            var d = Edited();
            d.Save(Path.Combine(user, "Save test"));
            d.Save(Path.Combine(user, "shared" + PwMapPaths.Ext));
            var list = SkirmishMaps.List(c!.Config);
            var mine = list.Where(m => m.Folder == SkirmishMaps.UserGroup).ToList();
            t.True(mine.Count == 2, $"the skirmish list's \"My maps\": {string.Join(", ", mine.Select(m => m.Path))}");
            t.True(mine.Any(m => m.Path == "User/Save test" && m.Name == "Save test" && m.Players == 2), "the folder: id User/Save test, its name, 2 players");
            t.True(mine.Any(m => m.Path == "User/shared.pwmap"), "the .pwmap: id User/shared.pwmap");
            t.True(list.IndexOf(mine[0]) > list.FindIndex(m => m.Folder.StartsWith("Base")), "after the installation's maps");
            t.Equal(Path.Combine(user, "Save test"), MapFiles.Find(c.Config, "User/Save test"), "Find: a user map's id");
            t.Equal(Path.Combine(user, "shared.pwmap"), MapFiles.Find(c.Config, "user/SHARED.pwmap"), "Find: case does not matter");
            t.True(MapFiles.Find(c.Config, "User/none") == null, "Find: no such user map");
            var h1 = MapFiles.ContentHash(Path.Combine(user, "Save test"));
            t.True(h1.Length == 16, "a content hash: " + h1);
            // a changed file: another hash
            var objs = Path.Combine(user, "Save test", "objects.lua");
            File.AppendAllText(objs, "-- changed\n");
            t.True(MapFiles.ContentHash(Path.Combine(user, "Save test")) != h1, "a changed file changes the hash");
            // a skirmish on the user map plays (the id resolves through MapFiles.Find)
            var setup = new SkirmishSetup { Map = "User/shared.pwmap", Seed = 7 };
            setup.Slots.Add(new SkirmishSlot { Type = "human", Tribe = "Hu", Team = 1 });
            setup.Slots.Add(new SkirmishSlot { Type = "ai", Tribe = "Aje", Team = 2 });
            var s = SkirmishSession.Create(setup, SkirmishMaps.Resolve(c.Config, setup.Map), c.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
            for (int k = 0; k < 40; k++) s.Step();
            t.True(s.World.Players.Count >= 2 && s.World.Buildings.Count > 0, $"a skirmish on User/shared.pwmap: {s.World.Players.Count} players, {s.World.Buildings.Count} buildings");
        }
        finally { MapFiles.UserDir = was; }
    }

    /// <summary>multiplayer: the host's map hash in the lobby; a player whose copy is missing or different has a problem
    /// the start is refused for (map-check); the same copy clears it; another map makes everybody check again</summary>
    public static void EditorSaveMultiplayerMapCheck(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("configuration base not found");
        var cfg = c!.Config;
        var user = Tmp("mpmaps");
        Directory.CreateDirectory(user);
        var was = MapFiles.UserDir;
        MapFiles.UserDir = user;
        try
        {
            Edited().Save(Path.Combine(user, "Arena"));
            var hash = LobbyMap.Hash(cfg, "User/Arena");
            t.True(hash != null && hash.Length == 16, "the map's hash: " + hash);
            var setup = Skirmish.DefaultSetup("Hu", "Aje");
            setup.Map = "User/Arena"; setup.MapName = "Arena"; setup.MapHash = hash;
            for (int i = 0; i < setup.Slots.Count; i++) setup.Slots[i].Type = i < 2 ? "open" : "closed";
            t.True(LobbyMap.Problem(cfg, setup) == null, "the same map here: no problem");
            t.True(LobbyMap.Problem(cfg, new SkirmishSetup { Map = "User/Arena" }) == null, "no hash (a dedicated server's setup): not checked");
            var other = setup.Clone(); other.MapHash = "0123456789abcdef";
            t.True(LobbyMap.Problem(cfg, other)?.Contains("another version") == true, "another copy: " + LobbyMap.Problem(cfg, other));
            var none = setup.Clone(); none.Map = "User/Nowhere";
            t.True(LobbyMap.Problem(cfg, none)?.Contains("does not have the map") == true, "no copy: " + LobbyMap.Problem(cfg, none));
            // over the network: a host, its own player and a second player
            using var host = new NetHost(0, loopbackOnly: true) { Build = "t" };
            host.SetLobby(new LobbyState { Mode = "skirmish", Configuration = "base", Skirmish = setup });
            var a = new NetClient("Host"); a.Connect("127.0.0.1", host.Port, "base", "t", "", host.HostToken);
            var b = new NetClient("Zidell"); b.Connect("127.0.0.1", host.Port, "base", "t", "");
            void Until(Func<bool> ok, string what)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!ok() && sw.ElapsedMilliseconds < 5000) { host.Update(0.02); a.Poll(); b.Poll(); System.Threading.Thread.Sleep(5); }
                t.True(ok(), what);
            }
            Until(() => host.Lobby.Players.Count() == 2 && b.Lobby?.Skirmish?.MapHash == hash, "both in the lobby; the players see the map's hash");
            b.SendMapCheck(hash!, LobbyMap.Problem(cfg, other));
            Until(() => host.Lobby.Peers.FirstOrDefault(p => p.Name == "Zidell")?.Problem != null, "Zidell's other copy: a problem in the lobby");
            b.SetReady(true);
            Until(() => true, "");
            t.True(LobbyStart.Problem(host.Lobby)?.Contains("Zidell") == true && LobbyStart.Problem(host.Lobby)!.Contains("map"), "the start refused: " + LobbyStart.Problem(host.Lobby));
            b.SendMapCheck("stale", null);
            Until(() => true, "");
            t.True(host.Lobby.Peers.First(p => p.Name == "Zidell").Problem != null, "a check of another hash changes nothing");
            b.SendMapCheck(hash!, null);
            Until(() => host.Lobby.Peers.First(p => p.Name == "Zidell").Problem == null, "the same copy: the problem gone");
            b.SendMapCheck(hash!, "does not have the map");
            Until(() => host.Lobby.Peers.First(p => p.Name == "Zidell").Problem != null, "a problem again");
            var l2 = host.Lobby.Clone(); l2.Skirmish!.Map = ""; l2.Skirmish.MapHash = null;
            host.SetLobby(l2);
            Until(() => host.Lobby.Peers.First(p => p.Name == "Zidell").Problem == null, "another map (random): the old map's problem gone");
            a.Dispose(); b.Dispose();
        }
        finally { MapFiles.UserDir = was; }
    }

    /// <summary>a user's map with triggers played as a custom mission: listed, prepared once per content (a folder of its
    /// own in the cache), its placed objects all there - three workers of one name are three units -, its trigger wins</summary>
    public static void EditorSaveCustomMapPlays(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("configuration base not found");
        var user = Tmp("custom_user"); var cache = Tmp("custom_cache");
        Directory.CreateDirectory(user);
        var was = MapFiles.UserDir;
        MapFiles.UserDir = user;
        try
        {
            var d = Edited();
            var h = new EditHistory(d);
            var w = new List<MapObject>();
            for (int k = 0; k < 3; k++) w.Add(new MapObject { Type = "CHTR", Name = "hu_worker", Cls = "hu_worker", X = 120 + k * 3, Y = 120, Z = 20, Owner = 0, Q = new double[] { 0, 0, 0, 1 } });
            h.Execute(ObjectsAddRemove.Add(d, w));
            d.Save(Path.Combine(user, "Arena"));
            MapDataDoc.CreateFlat(new NewMapSpec { Size = 256, Name = "Plain" }).Save(Path.Combine(user, "Plain"));
            var list = CustomMission.List();
            t.True(list.Count == 1 && Path.GetFileName(list[0]) == "Arena", "Custom maps lists the map with triggers, not the plain one: " + string.Join(", ", list.Select(Path.GetFileName)));
            var folder = CustomMission.Prepare(list[0], cache, new[] { "Hu", "Aje", "Ninigi" }, tr => c!.Rules.StartOf(tr)?.Units ?? new List<string>());
            t.True(CustomMission.IsCustomFolder(folder) && !TestMission.IsTestFolder(folder), "a custom map's folder: " + folder);
            var stamp = Directory.GetLastWriteTimeUtc(folder);
            t.Equal(folder, CustomMission.Prepare(list[0], cache, new[] { "Hu" }), "prepared once: the same folder again");
            var world = PwMapReader.WorldOf(folder) as System.Text.Json.Nodes.JsonObject;
            var objs = (world?["objects"] as System.Text.Json.Nodes.JsonArray)?.OfType<System.Text.Json.Nodes.JsonObject>().ToList() ?? new();
            var workers = objs.Where(o => o["name"]?.GetValue<string>() == "hu_worker").ToList();
            t.True(workers.Count == 3 && workers.Select(o => o["guid"]!.GetValue<string>()).Distinct().Count() == 3, "three workers of one name: three objects with GUIDs of their own");
            var start = new GameStart { Configuration = "base", MissionFolder = folder, Mission = CustomMission.Id, Seed = 3 };
            var s = PwRemake.Sim.Campaign.MissionSession.Create(start, c!.Config, new PwRemake.Sim.Campaign.SessionOptions
            {
                Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true, LuaStd = PwRemake.Sim.Scripting.LuaMission.DefaultStdDir(),
            });
            t.True(s.Problems.Count == 0, "loaded without problems: " + string.Join("; ", s.Problems));
            var mine = s.Host.PlayerObjects(0).Where(o => s.Host.Info(o)?.Type == "CHTR").ToList();
            int wk = s.World.Units.Count(u => u.Alive && u.Owner?.Id == 0 && u.Name == "hu_worker");
            t.True(wk >= 3, $"the three placed workers are there ({wk} workers of player 0)");
            for (int k = 0; k < 40; k++) s.Step();
            t.True(!s.Engine!.Won, "2 s: not won");
            s.Host.Teleport(mine[0], 55, 60);
            for (int k = 0; k < 40 && !s.Engine.Won; k++) s.Step();
            t.True(s.Engine.Won, "a unit in Goal: the custom map's trigger wins");
        }
        finally { MapFiles.UserDir = was; }
    }
}
