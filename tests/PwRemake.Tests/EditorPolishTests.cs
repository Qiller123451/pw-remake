// Tests of the map editor's open ends after phase 7 (docs/MAP_EDITOR.md): objects placed into a campaign mission come
// alive when it is played (Test map / custom map) without the mission's own objects twice; map.lua's `requires` (the
// configuration a map needs) decided from the classes, options and tribes it uses, and the lists that leave a map of
// another configuration out (skirmish, multiplayer, custom maps; the editor's Open shows it greyed); the trigger
// check that loads the file as the game does, the same for every caller. (regions.lua of an unchanged mission saved
// byte for byte: EditorSaveTests.EditorSaveMissionSameBytes; the map sent to a player in the lobby:
// MultiplayerTests.MapTransferToPlayerWithoutMap.)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Editor.Triggers;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;

namespace PwRemake.Tests;

[TestClass]
public static class EditorPolishTests
{
    static string Content() => Environment.GetEnvironmentVariable("PW_CONTENT") is { Length: > 0 } c ? c : "/home/claude/testcontent";
    static string Tmp(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "pw-edpolish-tests", name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(Path.GetDirectoryName(d)!);
        return d;
    }

    static PwRemake.Sim.Campaign.MissionSession Play(CampaignTests.Cfg c, string folder) =>
        PwRemake.Sim.Campaign.MissionSession.Create(new GameStart { Configuration = "base", MissionFolder = folder, Mission = 99, Seed = 3 }, c.Config, new PwRemake.Sim.Campaign.SessionOptions
        {
            Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true, LuaStd = EditorTriggersTests.Std(),
        });

    // ============================================================================================ 1. placed objects in a mission
    /// <summary>mission 11 (the arena) opened in the editor, a fireplace (the Ninigi's main building) and a warrior of the
    /// human player placed without a trigger or group naming them: Test map's mission folder has them in world.lua, the
    /// mission plays with both alive - and every object of the mission once (the same counts as without them, plus two)</summary>
    public static void EditorPolishMissionPlacedObjects(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        var mf = Path.Combine(Content(), "configs", "base", "missions", "11_single_11");
        if (c == null || !File.Exists(Path.Combine(mf, "map.lua"))) t.Skip("configuration base or mission 11 not found");
        var playable = new[] { "Hu", "Aje", "Ninigi" };
        Func<string, IReadOnlyList<string>> start = tr => c!.Rules.StartOf(tr)?.Units ?? new List<string>();
        // the mission as it is
        var d0 = MapDataDoc.Open(mf);
        var f0 = TestMission.Write(d0.Clone(), Tmp("m11_plain"), playable, start);
        var s0 = Play(c!, f0);
        t.True(s0.Problems.Count == 0, "the mission as it is loads: " + string.Join("; ", s0.Problems));
        int units0 = s0.World.Units.Count(u => u.Alive), bld0 = s0.World.Buildings.Count(b => b.Alive);
        int world0 = (PwMapReader.WorldOf(f0)?["objects"] as JsonArray)?.Count ?? 0;
        // + a main building and a warrior of player 0 (the human: Ninigi), named by nothing
        var d = MapDataDoc.Open(mf);
        var h = new EditHistory(d);
        var hq = new MapObject { Type = "BLDG", Name = "ninigi_fireplace", Cls = "ninigi_fireplace", X = 250, Y = 222, Z = d.HeightAt(250, 222), Owner = 0, Q = new double[] { 0, 0, 0, 1 } };
        var war = new MapObject { Type = "CHTR", Name = "ninigi_warrior", Cls = "ninigi_warrior", X = 276, Y = 256, Z = d.HeightAt(276, 256), Owner = 0, Q = new double[] { 0, 0, 0, 1 } };
        h.Execute(ObjectsAddRemove.Add(d, new[] { hq, war }));
        t.True(!hq.Attr.ContainsKey(MissionDoc.GuidAttr) && !war.Attr.ContainsKey(MissionDoc.GuidAttr), "the placed objects have no GUID (no trigger or group names them)");
        var f1 = TestMission.Write(d.Clone(), Tmp("m11_placed"), playable, start);
        var wo = (PwMapReader.WorldOf(f1)?["objects"] as JsonArray)!.OfType<JsonObject>().ToList();
        t.Equal(world0 + 2, wo.Count, "world.lua: the mission's objects and the two placed ones");
        t.True(wo.Count(o => o["name"].Str() == "ninigi_fireplace") == 1 && wo.Count(o => o["name"].Str() == "ninigi_warrior") == 1, "each placed object once");
        t.True(wo.Select(o => o["guid"].Str()).Distinct().Count() == wo.Count, "every GUID once");
        var s1 = Play(c!, f1);
        t.True(s1.Problems.Count == 0, "loads without problems: " + string.Join("; ", s1.Problems));
        var (hx, hz) = s1.Campaign.ToGame(250, 222);
        var b = s1.World.Buildings.FirstOrDefault(x => x.Alive && x.Name == "ninigi_fireplace" && Math.Abs(x.Pos.X - hx) < 4 && Math.Abs(x.Pos.Z - hz) < 4);
        t.True(b != null && b.Owner?.Id == 0, $"the placed fireplace stands for player 0 ({b?.Pos.X:0.0}, {b?.Pos.Z:0.0}; wanted {hx:0.0}, {hz:0.0})");
        var (ux, uz) = s1.Campaign.ToGame(276, 256);
        var u = s1.World.Units.FirstOrDefault(x => x.Alive && x.Name == "ninigi_warrior" && Math.Abs(x.Pos.X - ux) < 6 && Math.Abs(x.Pos.Z - uz) < 6);
        t.True(u != null && u.Owner?.Id == 0, $"the placed warrior is a unit of player 0 ({u?.Pos.X:0.0}, {u?.Pos.Z:0.0})");
        t.Equal(units0 + 1, s1.World.Units.Count(x => x.Alive), "units: the mission's once, plus the warrior");
        t.Equal(bld0 + 1, s1.World.Buildings.Count(x => x.Alive), "buildings: the mission's once, plus the fireplace");
        for (int k = 0; k < 40; k++) s1.Step();
        t.True(b!.Alive && u!.Alive, "both alive after 2 s");
        // the same through Custom maps (a saved copy in My maps, prepared as a mission)
        var user = Tmp("m11_user"); var cache = Tmp("m11_cache");
        Directory.CreateDirectory(user);
        var was = MapFiles.UserDir;
        MapFiles.UserDir = user;
        try
        {
            d.Save(Path.Combine(user, "Arena with a base"));
            var cf = CustomMission.Prepare(Path.Combine(user, "Arena with a base"), cache, playable, start);
            var s2 = Play(c!, cf);
            t.True(s2.World.Buildings.Any(x => x.Alive && x.Name == "ninigi_fireplace" && x.Owner?.Id == 0) && s2.World.Units.Count(x => x.Alive) == units0 + 1, "the saved mission as a custom map: the same");
        }
        finally { MapFiles.UserDir = was; }
        t.Log($"mission 11: {units0} units, {bld0} buildings; with the placed ones {s1.World.Units.Count(x => x.Alive)}, {s1.World.Buildings.Count(x => x.Alive)}");
    }

    // ============================================================================================ 2. map.lua requires
    static RequirementContext Ctx(CampaignTests.Cfg baseC, CampaignTests.Cfg mod) => new()
    {
        Mod = mod.Config.Mod, BaseClasses = ObjectPalette.ClassNamesOf(baseC.Data), BaseTribes = baseC.Config.PlayableTribes, ModClasses = ObjectPalette.ClassNamesOf(mod.Data),
    };

    /// <summary>what makes a map need MIRAGE (a MIRAGE building, a trigger spawning MIRAGE's units, a MIRAGE option, a slot that
    /// plays SEAS) and what does not (base classes only, the base game's editor); map.lua keeps it; the official game's
    /// skirmish list, its custom maps and its editor's Open (greyed, with the reason) - MIRAGE's lists show it</summary>
    public static void EditorPolishRequires(TestContext t)
    {
        var bc = CampaignTests.LoadCfgPublic("base"); var mc = CampaignTests.LoadCfgPublic("mirage");
        if (bc == null || mc == null) t.Skip("configurations base and mirage needed");
        var ctx = Ctx(bc!, mc!);
        t.True(ctx.BaseClasses!.Count > 100 && !ctx.BaseClasses.Contains("aje_amazon_temple") && ctx.ModClasses!.Contains("aje_amazon_temple") && ctx.BaseClasses.Contains("seas_headquarters"), "the class lists (the SEAS are in the official game too: its campaign's enemy)");
        MapDataDoc Flat(string name) => MapDataDoc.CreateFlat(new NewMapSpec { Size = 256, BaseHeight = 20, Water = null, Setting = "Savanna", Name = name });
        // base classes only: nothing required, also from MIRAGE's editor
        var plain = Flat("Plain");
        new EditHistory(plain).Execute(ObjectsAddRemove.Add(plain, new[] { new MapObject { Type = "BLDG", Name = "hu_warehouse", Cls = "hu_warehouse", X = 100, Y = 100, Z = 20, Owner = 0, Q = new double[] { 0, 0, 0, 1 } } }));
        t.Equal("", MapRequirement.Decide(plain, ctx), "a map of base classes made with MIRAGE: no requirement");
        t.Equal("", MapRequirement.Decide(plain, new RequirementContext { Mod = "" }), "the official game's editor: never");
        // a SEAS building
        var seas = Flat("SEAS base");          // (a MIRAGE building)
        new EditHistory(seas).Execute(ObjectsAddRemove.Add(seas, new[] { new MapObject { Type = "BLDG", Name = "aje_amazon_temple", Cls = "aje_amazon_temple", X = 100, Y = 100, Z = 20, Owner = 0, Q = new double[] { 0, 0, 0, 1 } } }));
        t.Equal("MIRAGE", MapRequirement.Decide(seas, ctx), "MIRAGE's amazon temple: MIRAGE");
        t.True(MapRequirement.Reasons(seas, ctx).Any(r => r.Contains("aje_amazon_temple")), "the reason names the class: " + string.Join("; ", MapRequirement.Reasons(seas, ctx)));
        // a MIRAGE option
        var opt = Flat("Flyers");
        var P0 = opt.GetPlayers(); var P = P0.Clone(); P.FlyingEnabled = true;
        new EditHistory(opt).Execute(new PlayersEdit(P0, P));
        t.Equal("MIRAGE", MapRequirement.Decide(opt, ctx), "flying units on: MIRAGE");
        // a slot that plays SEAS
        var tr = Flat("SEAS player");
        var Q0 = tr.GetPlayers(); var Q = Q0.Clone(); Q.Slots[1].Tribe = "SEAS"; Q.Slots[1].Tribes = new List<string> { "SEAS" };
        new EditHistory(tr).Execute(new PlayersEdit(Q0, Q));
        t.Equal("MIRAGE", MapRequirement.Decide(tr, ctx), "player 2 plays SEAS: MIRAGE");
        // a trigger that spawns SEAS units
        var trig = Flat("SEAS wave");
        var th = new EditHistory(trig);
        th.Execute(MissionEdits.NewTrigger(trig.Mission, "Root", "wave"));
        th.Execute(MissionEdits.ChangeTrigger(trig.Mission, 0, x =>
        {
            x.Conditions.Add(EdNode.Of("TIME", true));
            var sp = EdNode.Of("SPGR", false); sp.Set("classes_0", ParamValue.Str("aje_scythian 2 4")); sp.Set("owner", ParamValue.Of("1")); x.Actions.Add(sp);
        }));
        t.Equal("MIRAGE", MapRequirement.Decide(trig, ctx), "a trigger spawning MIRAGE's aje_scythian: MIRAGE");
        // map.lua keeps it; the lists
        var user = Tmp("requires_user");
        Directory.CreateDirectory(user);
        var was = MapFiles.UserDir;
        MapFiles.UserDir = user;
        try
        {
            seas.Requires = MapRequirement.Decide(seas, ctx); seas.Save(Path.Combine(user, "SEAS base"));
            plain.Requires = MapRequirement.Decide(plain, ctx); plain.Save(Path.Combine(user, "Plain"));
            trig.Requires = MapRequirement.Decide(trig, ctx); trig.Save(Path.Combine(user, "SEAS wave"));
            t.True(File.ReadAllText(Path.Combine(user, "SEAS base", "map.lua")).Contains("requires = \"MIRAGE\""), "map.lua: requires = \"MIRAGE\"");
            t.True(!File.ReadAllText(Path.Combine(user, "Plain", "map.lua")).Contains("requires"), "a map for any configuration has no requires");
            t.Equal("MIRAGE", MapDataDoc.Open(Path.Combine(user, "SEAS base")).Requires, "read back");
            t.Equal("MIRAGE", SkirmishMaps.Header(Path.Combine(user, "SEAS base")).Requires, "the map list's header has it");
            t.True(MapFiles.RequirementProblem(bc!.Config, "MIRAGE") is { } why && why.Contains("needs MIRAGE"), "the official game: " + MapFiles.RequirementProblem(bc.Config, "MIRAGE"));
            t.True(MapFiles.RequirementProblem(mc!.Config, "MIRAGE") == null && MapFiles.RequirementProblem(mc.Config, "mirage") == null && MapFiles.RequirementProblem(bc.Config, "") == null, "MIRAGE (by its mod or id); no requirement: any");
            var baseMine = SkirmishMaps.List(bc.Config).Where(m => m.Folder == SkirmishMaps.UserGroup).Select(m => m.Name).ToList();
            var modMine = SkirmishMaps.List(mc.Config).Where(m => m.Folder == SkirmishMaps.UserGroup).Select(m => m.Name).ToList();
            t.True(baseMine.Contains("Plain") && !baseMine.Contains("SEAS base"), "the official game's skirmish / multiplayer list: not the MIRAGE map: " + string.Join(", ", baseMine));
            t.True(modMine.Contains("Plain") && modMine.Contains("SEAS base"), "MIRAGE's list: both");
            var greyed = SkirmishMaps.List(bc.Config, true, unfit: true).FirstOrDefault(m => m.Name == "SEAS base");
            t.True(greyed?.Unfit?.Contains("MIRAGE") == true, "the editor's Open (unfit listed): the reason " + greyed?.Unfit);
            t.True(!CustomMission.List(bc.Config).Any(p => p.EndsWith("SEAS wave")) && CustomMission.List(mc.Config).Any(p => p.EndsWith("SEAS wave")), "custom maps: the MIRAGE map with triggers only in MIRAGE");
        }
        finally { MapFiles.UserDir = was; }
    }

    // ============================================================================================ 3. the one trigger check
    /// <summary>the check every caller of the editor uses (CheckAll: the trigger editor's list and Check, the Triggers
    /// panel, before Test map) loads the file as the game does: a custom.lua that is good Lua but fails when it runs is
    /// an error there - the syntax checks alone pass it; the load is done once per text</summary>
    public static void EditorPolishTriggerCheckLoads(TestContext t)
    {
        var m = new MissionDoc();
        m.Regions.Add(new EdRegion { Guid = EdGuid.New(), Name = "Goal", Shapes = { new EdShape { X = 10, Y = 10, W = 20, H = 20 } } });
        m.Triggers = TriggerScript.Parse("trigger \"a\" { guid = \"0123456789abcdef0123456789abcdef\", enabled = true, conditions = { True{} }, actions = { Win{} } }\n");
        m.Custom = "local x = nil\nx.field = 1\n";                    // (good syntax, an error when it runs)
        t.True(TriggerValidation.Check(m).All(i => i.Severity != Severity.Error), "the checks without loading pass it");
        var std = EditorTriggersTests.Std();
        var wasStd = Environment.GetEnvironmentVariable("PW_LUA_STD");
        Environment.SetEnvironmentVariable("PW_LUA_STD", std);
        try
        {
            var all = TriggerValidation.CheckAll(m);
            t.True(all.Any(i => i.Severity == Severity.Error && i.Message.Contains("do not load")), "CheckAll loads the file: " + string.Join("; ", all));
            m.Custom = "local x = 1\n";
            t.True(TriggerValidation.CheckAll(m).All(i => i.Severity != Severity.Error), "fixed: no problems");
            // a big mission's triggers: loaded once, then from the check's memory
            var big = Directory.Exists(Path.Combine(Content(), "configs", "mirage", "missions")) ? Directory.GetDirectories(Path.Combine(Content(), "configs", "mirage", "missions"))
                .Where(x => File.Exists(Path.Combine(x, "triggers.lua"))).OrderByDescending(x => new FileInfo(Path.Combine(x, "triggers.lua")).Length).FirstOrDefault() : null;
            if (big != null)
            {
                var d = MapDataDoc.Open(big);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var i1 = TriggerValidation.CheckAll(d.Mission);
                long first = sw.ElapsedMilliseconds; sw.Restart();
                var i2 = TriggerValidation.CheckAll(d.Mission);
                long again = sw.ElapsedMilliseconds;
                t.True(!i1.Any(i => i.Severity == Severity.Error), $"{Path.GetFileName(big)} loads: {string.Join("; ", i1.Where(i => i.Severity == Severity.Error).Take(3))}");
                t.True(i1.Count == i2.Count, "the same problems again");
                t.Log($"{Path.GetFileName(big)} ({new FileInfo(Path.Combine(big, "triggers.lua")).Length / 1024} KB triggers): the check {first} ms, again {again} ms");
            }
        }
        finally { Environment.SetEnvironmentVariable("PW_LUA_STD", wasStd); }
    }
}
