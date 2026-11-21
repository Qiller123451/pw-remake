// Tests of the map editor's regions, mission data and trigger editor (phases 5 and 6, docs/MAP_EDITOR.md "Regions" and
// "Triggers"): triggers.lua read and written again on every converted mission of both configurations (the same file,
// the same triggers for the engine), code blocks kept, the catalogue of condition / action types against the engine and
// the original's attribute definitions, region / group / variable / quest edits with undo, validation, and a small map
// made in code - a region and a trigger "a unit enters the region -> the mission is won" - played headless through
// Test map's mission path.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Editor.Triggers;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.Core;

namespace PwRemake.Tests;

[TestClass]
public static class EditorTriggersTests
{
    static string Content() => Environment.GetEnvironmentVariable("PW_CONTENT") is { Length: > 0 } c ? c : "/home/claude/testcontent";

    /// <summary>every converted mission folder of both configurations</summary>
    static List<string> Missions(TestContext t)
    {
        var res = new List<string>();
        foreach (var cfg in new[] { "base", "mirage" })
        {
            var dir = Path.Combine(Content(), "configs", cfg, "missions");
            if (!Directory.Exists(dir)) continue;
            res.AddRange(Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "triggers.lua"))).OrderBy(d => d, StringComparer.Ordinal));
        }
        if (res.Count == 0) t.Skip("no converted missions (PW_CONTENT)");
        return res;
    }

    /// <summary>the engine's definitions of two sources are the same (everything but where they were defined)</summary>
    internal static string? SameDefs(IReadOnlyList<TriggerDef> a, IReadOnlyList<TriggerDef> b)
    {
        if (a.Count != b.Count) return $"{a.Count} triggers, then {b.Count}";
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            string w = $"trigger {i} \"{x.Name}\"";
            if (x.Name != y.Name || x.Guid != y.Guid || x.Folder != y.Folder || x.Description != y.Description || x.Expression != y.Expression) return w + ": name / guid / folder / description / expr";
            if (x.Once != y.Once || x.Enabled != y.Enabled || x.Random != y.Random || x.ByDifficulty != y.ByDifficulty || x.NodeOff != y.NodeOff) return w + ": flags";
            foreach (var (la, lb, what) in new[] { (x.Conditions, y.Conditions, "condition"), (x.Actions, y.Actions, "action") })
            {
                if (la.Count != lb.Count) return $"{w}: {la.Count} {what}s, then {lb.Count}";
                for (int k = 0; k < la.Count; k++)
                {
                    var n = la[k]; var m = lb[k];
                    if (n.Code != m.Code || n.Difficulty != m.Difficulty || n.Note != m.Note || n.Every != m.Every || (n.Function == null) != (m.Function == null)) return $"{w} {what} {k}: {n.Code}";
                    if (n.P.Count != m.P.Count || n.P.Any(kv => !m.P.TryGetValue(kv.Key, out var v) || v != kv.Value)) return $"{w} {what} {k} {n.Code}: parameters";
                }
            }
        }
        return null;
    }

    /// <summary>game/lua/std (from the test's folder or the working folder up)</summary>
    internal static string Std()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                var g = Path.Combine(d.FullName, "game", "lua", "std");
                if (Directory.Exists(g)) return g;
            }
        throw new SkipException("game/lua/std not found");
    }

    static List<TriggerDef> Defs(string src)
    {
        var lm = LuaMission.FromSource(src, Std());
        if (!lm.Ok) throw new Exception("the Lua did not load (the log has the error)");
        return lm.Script.Triggers;
    }

    // ============================================================================================ triggers.lua
    public static void EditorTriggersRoundTripAllMissions(TestContext t)
    {
        int files = 0, same = 0, triggers = 0, nodes = 0;
        foreach (var dir in Missions(t))
        {
            var src = File.ReadAllText(Path.Combine(dir, "triggers.lua"));
            var s = TriggerScript.Parse(src);
            var name = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(dir))) + "/" + Path.GetFileName(dir);
            t.True(s.Items.All(i => i is EdTrigger), $"{name}: every statement a trigger the editor expresses ({s.Items.OfType<EdCode>().FirstOrDefault()?.Title})");
            var w = s.Write();
            files++;
            if (w == src) same++;
            else
            {
                // (where they differ, for the log)
                var a = src.Split('\n'); var b = w.Split('\n');
                int k = 0; while (k < Math.Min(a.Length, b.Length) && a[k] == b[k]) k++;
                t.Log($"{name}: written text differs at line {k + 1}: '{(k < a.Length ? a[k] : "<end>")}' / '{(k < b.Length ? b[k] : "<end>")}'");
            }
            var d0 = Defs(src); var d1 = Defs(w);
            var why = SameDefs(d0, d1);
            t.True(why == null, $"{name}: the engine's triggers are the same after writing: {why}");
            // read again: the same model
            t.Equal(w, TriggerScript.Parse(w).Write(), $"{name}: writing is stable");
            triggers += d0.Count; nodes += d0.Sum(x => x.Conditions.Count + x.Actions.Count);
        }
        t.Log($"{files} missions, {triggers} triggers, {nodes} conditions and actions; {same} files byte for byte the same");
        t.True(same == files, "every converted triggers.lua is written back byte for byte");
    }

    // ============================================================================================ the catalogue
    static string SrcDir()
    {
        var std = Std();
        return Path.GetFullPath(Path.Combine(std, "..", "..", "..", "src", "Sim", "Scripting"));
    }

    public static void EditorTriggersCatalogComplete(TestContext t)
    {
        // every type of the engine, once, with a category
        foreach (var e in TriggerTypes.All)
        {
            var ts = TriggerCatalog.Find(e.Code, e.IsCondition);
            t.True(ts != null && ts.Name == e.Name && ts.Category.Length > 0, $"{e.Name} ({e.Code}) in the catalogue");
        }
        t.Equal(TriggerTypes.All.Count, TriggerCatalog.All.Count, "nothing else in the catalogue");
        t.True(TriggerCatalog.All.Where(x => !x.IsCode && x.Code != "TRUE" && x.Code != "QUIT").All(x => x.Params.Count > 0), "every type with parameters has them");
        // the parameters the engine reads (Conditions.cs / Actions.cs: P.S("key"), P["key"], PlayerSlot("key") ...) are in its type's list
        var rx = new Regex(@"(?:P\.\w+\(|P\[|PlayerSlot\(|Flag\()""(\w+)""", RegexOptions.CultureInvariant);
        int read = 0;
        var missing = new List<string>();
        foreach (var (file, cond) in new[] { ("Conditions.cs", true), ("Actions.cs", false) })
        {
            var src = File.ReadAllText(Path.Combine(SrcDir(), file));
            var parts = Regex.Split(src, @"public sealed class (?=[A-Z]{4}\b)");
            foreach (var part in parts.Skip(1))
            {
                var code = part[..4];
                var ts = TriggerCatalog.Find(code, cond);
                if (ts == null) continue;          // (NONE: the placeholder of unknown types)
                foreach (Match m in rx.Matches(part))
                {
                    var k = m.Groups[1].Value;
                    read++;
                    if (ts.Param(k) == null && k != "difficulty" && !k.EndsWith('_')) missing.Add($"{code}.{k}");
                }
            }
        }
        t.True(missing.Count == 0, "the engine reads these, the catalogue has them: " + string.Join(", ", missing.Distinct()));
        t.Log($"{read} parameter reads of the engine found in the catalogue");
        // the defaults are world.lua's trigger_defaults of every mission (the original's, without the editor's renderable)
        int checkedDefaults = 0;
        foreach (var dir in Missions(t))
        {
            var w = PwRemake.Sim.Maps.PwMapReader.WorldOf(dir);
            var td = w?["trigger_defaults"];
            if (td == null) continue;
            foreach (var cond in new[] { true, false })
                foreach (var (code, ps) in td[cond ? "conditions" : "actions"]!.AsObject())
                {
                    var ts = TriggerCatalog.Find(code, cond);
                    if (ts == null) continue;      // (a type the engine does not run)
                    var mine = ts.Defaults();
                    foreach (var (k, v) in ps!.AsObject())
                    {
                        if (k.StartsWith("renderable", StringComparison.Ordinal)) continue;
                        t.True(mine.TryGetValue(k, out var dv) && dv == v.Str(), $"{Path.GetFileName(dir)} {code}.{k} default {v} (catalogue: {(mine.TryGetValue(k, out var d2) ? d2 : "none")})");
                        checkedDefaults++;
                    }
                }
        }
        t.Log($"{checkedDefaults} defaults the same as the missions' trigger_defaults");
        // every parameter the missions use is one of its type's
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dir in Missions(t))
            foreach (var tr in TriggerScript.Parse(File.ReadAllText(Path.Combine(dir, "triggers.lua"))).Triggers)
                foreach (var cond in new[] { true, false })
                    foreach (var n in tr.List(cond))
                    {
                        var ts = TriggerCatalog.Find(n.Code, cond);
                        if (ts == null) { unknown.Add(n.Code); continue; }
                        foreach (var p in n.Params) if (ts.Param(p.Key) == null && p.Key != "note") unknown.Add($"{n.Code}.{p.Key}");
                    }
        t.True(unknown.Count == 0, "every parameter of the missions is in the catalogue: " + string.Join(", ", unknown));
        // search
        t.True(TriggerCatalog.Search("region", true).Any(x => x.Code == "REGN"), "search: region -> Region");
        t.True(TriggerCatalog.Search("spawn", false).Any(x => x.Code == "SPGR"), "search: spawn -> SpawnGroup");
        t.True(TriggerCatalog.Search("ai attack", false).Any(x => x.Code == "AIFT"), "search: two words");
        t.Equal(PType.Region, TriggerCatalog.Find("REGN", true)!.Param("rgn_guid")!.Type, "Region.rgn_guid picks a region");
        t.Equal(PType.Object, TriggerCatalog.Find("ACDO", false)!.Param("dst_obj_guid")!.Type, "ObjDo.dst_obj_guid picks an object");
        t.Equal(PType.Group, TriggerCatalog.Find("CKGR", true)!.Param("group_guid")!.Type, "GroupCount.group_guid picks a group");
        t.Equal(PType.Variable, TriggerCatalog.Find("VARS", false)!.Param("varname")!.Type, "SetVar.varname names a variable");
    }

    // ============================================================================================ the mission data
    static MissionDoc Load(string dir)
    {
        var texts = new Dictionary<string, System.Text.Json.Nodes.JsonObject>();
        foreach (var f in Directory.GetFiles(Path.Combine(dir, "texts"), "*.lua"))
            texts[Path.GetFileNameWithoutExtension(f)] = (System.Text.Json.Nodes.JsonObject)PwRemake.Sim.Maps.LuaData.ReadJsonFile(f, f)!;
        var md = MissionDoc.From(PwRemake.Sim.Maps.PwMapReader.WorldOf(dir), PwRemake.Sim.Maps.PwMapReader.MissionOf(dir) as System.Text.Json.Nodes.JsonObject,
            File.ReadAllText(Path.Combine(dir, "triggers.lua")), File.Exists(Path.Combine(dir, "custom.lua")) ? File.ReadAllText(Path.Combine(dir, "custom.lua")) : null, texts, null);
        // (the objects as the document gives them: world.lua's)
        var objs = (md.World["objects"] as System.Text.Json.Nodes.JsonArray)!.OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(o => new EdObject(o["guid"].Str(), o["name"].Str(), o["class"].Str(), o["type"].Str(), o["x"].Num(), o["y"].Num(), o["owner"] is System.Text.Json.Nodes.JsonValue ? o["owner"].Int() : null)).ToList();
        md.Objects = () => objs;
        return md;
    }

    public static void EditorTriggersWorldRoundTrip(TestContext t)
    {
        int n = 0, regions = 0, quests = 0, groups = 0, warnings = 0;
        foreach (var dir in Missions(t))
        {
            var name = Path.GetFileName(dir);
            var world = PwRemake.Sim.Maps.PwMapReader.WorldOf(dir)!;
            var mission = PwRemake.Sim.Maps.PwMapReader.MissionOf(dir)!;
            var md = Load(dir);
            var w2 = md.ToWorld();
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(world, w2))
                foreach (var (k, v) in world.AsObject())
                    if (!System.Text.Json.Nodes.JsonNode.DeepEquals(v, w2[k]))
                    {
                        var a = v?.ToJsonString() ?? ""; var b = w2[k]?.ToJsonString() ?? ""; int i = 0; while (i < Math.Min(a.Length, b.Length) && a[i] == b[i]) i++;
                        t.Log($"{name} {k}: ...{a.Substring(Math.Max(0, i - 80), Math.Min(200, a.Length - Math.Max(0, i - 80)))}\n   vs ...{b.Substring(Math.Max(0, i - 80), Math.Min(200, b.Length - Math.Max(0, i - 80)))}");
                    }
            t.True(System.Text.Json.Nodes.JsonNode.DeepEquals(world, w2), $"{name}: world.lua read and written again is the same data");
            t.True(System.Text.Json.Nodes.JsonNode.DeepEquals(mission, md.ToMission()), $"{name}: the mission table the same");
            t.True(System.Text.Json.Nodes.JsonNode.DeepEquals(world, md.Clone().ToWorld()), $"{name}: a clone writes the same");
            regions += md.Regions.Count; quests += md.Quests.Count; groups += md.Groups.Count;
            // one object list (every sixth mission: reading the map takes a moment): world.lua's objects bound to the
            // map's, written back the same; objects.lua without the editor's attributes; a move and a deletion follow
            if (n % 6 == 0)
            {
                var map = PwRemake.Sim.Maps.MapFiles.Read(dir);
                var before = map.Objects.Select(o => (o.Name, o.Attr.Count)).ToList();
                md.MapObjects = () => map.Objects;
                md.BindObjects(map.Objects);
                int bound = map.Objects.Count(o => o.Attr.ContainsKey(MissionDoc.GuidAttr));
                t.True(bound > 0, $"{name}: world objects in the one list ({bound})");
                t.True(System.Text.Json.Nodes.JsonNode.DeepEquals(world, md.ToWorld()), $"{name}: world.lua the same with the bound list");
                var file = MissionDoc.MapFileObjects(map.Objects);
                t.True(file.Select(o => (o.Name, o.Attr.Count)).SequenceEqual(before), $"{name}: objects.lua's objects as read (no editor attributes)");
                var mover = map.Objects.First(o => o.Attr.ContainsKey(MissionDoc.GuidAttr) && !o.Attr.ContainsKey(MissionDoc.MissionOnlyAttr));
                var gid = mover.Attr[MissionDoc.GuidAttr];
                mover.X += 5;
                var w3 = md.ToWorld()["objects"]!.AsArray();
                t.Near(world["objects"]!.AsArray().First(o => o!["guid"].Str() == gid)!["x"].Num() + 5, w3.First(o => o!["guid"].Str() == gid)!["x"].Num(), 1e-3, $"{name}: a move follows into world.lua");
                map.Objects.Remove(mover);
                t.True(md.ToWorld()["objects"]!.AsArray().All(o => o!["guid"].Str() != gid), $"{name}: a deleted object leaves world.lua");
                var nw = map.Objects.FirstOrDefault(o => !o.Attr.ContainsKey(MissionDoc.GuidAttr));
                if (nw == null) map.Objects.Add(nw = new PwRemake.Sim.Maps.MapObject { Type = "CHTR", Cls = "hu_worker", Name = "my_worker", X = 50, Y = 50, Q = new double[] { 0, 0, 0, 1 } });
                var (cmd, ng) = MissionEdits.EnsureGuid(nw);
                nw.Attr[MissionDoc.GuidAttr] = ng;
                t.True(cmd != null && md.ToWorld()["objects"]!.AsArray().Any(o => o!["guid"].Str() == ng && o["name"].Str() == nw.Name), $"{name}: an object given a GUID joins world.lua");
            }
            // the checks of the trigger editor: no errors on an original mission (the warnings: references the original
            // maps really lack - deleted regions, unused quests ...)
            var issues = TriggerValidation.Check(md);
            var errs = issues.Where(i => i.Severity == Severity.Error).ToList();
            t.True(errs.Count == 0, $"{name}: no errors ({string.Join("; ", errs.Take(3))})");
            warnings += issues.Count;
            n++;
        }
        t.Log($"{n} missions: {regions} regions, {groups} groups, {quests} quests the same after writing; {warnings} warnings of the checks");
    }

    public static void EditorTriggersValidationFinds(TestContext t)
    {
        var m = new MissionDoc();
        m.Regions.Add(new EdRegion { Guid = EdGuid.New(), Name = "Goal", Shapes = { new EdShape { X = 10, Y = 10, W = 20, H = 20 } } });
        m.Variables.Add(new EdVariable { Name = "waves", Value = "0" });
        m.Triggers = TriggerScript.Parse(@"
trigger ""a"" {
  enabled = true,
  expr = ""1 && 3"",
  conditions = { Region{ rgn_guid = ""nosuchregionaaaaaaaaaaaaaaaaaaaa"", obj_count = "">0"" }, Var{ varname = ""wavez"", value = 1 } },
  actions = { Win{}, SetTrigger{ trigger = ""b"", state = 1 }, Script(function(api) if x then end end) },
}
trigger ""a"" { conditions = { Region{ rgn_guid = ""Goal"" } }, actions = { Node(""ZZZZ""){} } }
trigger ""c"" { actions = { Script(function(api) if x then end) } }
");
        m.Custom = "local x = = 1";
        var iss = TriggerValidation.Check(m);
        bool Has(string s, Severity sev) => iss.Any(i => i.Severity == sev && i.Message.Contains(s));
        t.True(Has("names condition 3", Severity.Warning), "an expression naming condition 3 of 2");
        t.True(TriggerValidation.Check(new MissionDoc { Triggers = TriggerScript.Parse("trigger \"x\" { expr = \"1 &&\", conditions = { True{} } }") }).Any(i => i.Severity == Severity.Error && i.Message.Contains("expression")), "a broken expression");
        t.True(Has("no region nosuchregion", Severity.Warning), "a missing region");
        t.True(Has("no variable wavez", Severity.Warning), "a missing variable");
        t.True(Has("no trigger b", Severity.Warning), "a missing trigger");
        t.True(Has("Lua syntax", Severity.Error) && iss.Count(i => i.Message.Contains("Lua syntax")) == 2, "the broken Script (its trigger a custom block) and custom.lua (the good Script passes)");
        t.True(Has("unknown type", Severity.Error), "Node(ZZZZ)");
        t.True(Has("second trigger of this name", Severity.Warning), "two triggers named a");
        var bad = iss.First(i => i.Message.Contains("no region"));
        t.True(bad.Item == 0 && bad.Conditions == true && bad.Node == 0 && bad.Param == "rgn_guid", "an issue knows its place (jump)");
        t.True(!iss.Any(i => i.Message.Contains("Goal")), "a region named by its name is found");
    }

    public static void EditorTriggersCodeBlocksKept(TestContext t)
    {
        var src = @"-- my map

local SPOTS = { ""100 100 0"", ""200 200 0"" }

trigger ""start"" {
  -- the first trigger
  enabled = true, once = true,
  conditions = {
    Time{ duration = 2 },  -- two seconds
    Check(function(api) return api.vars.number(""x"") > 1 end, 1),
  },
  actions = {
    -- a label
    SpawnGroup{ classes_0 = ""hu_warrior 1 2"", pos = SPOTS[1], owner = 1 },
    Script(function(api)
      api.log(""hello, world"") -- a comment in code
    end),
    Node(""ABCD""){ x = 1 },
    SetVar{ varname = ""x"", operation = ""+"", value = 1, [""local""] = 1 },
  },
}

for i = 1, 2 do
  trigger(""wave "" .. i) { enabled = true, conditions = { Time{ duration = 10 * i } }, actions = { Win{} } }
end

trigger ""computed"" { enabled = debug_mode ~= nil, conditions = {}, actions = {} }
";
        var s = TriggerScript.Parse(src);
        t.Equal(4, s.Items.Count, "a trigger, two code blocks, the loop (the header is the file's comment)");
        t.True(s.Items[0] is EdCode c0 && c0.Source.Contains("local SPOTS"), "the local is a code block");
        var tr = (EdTrigger)s.Items[1];
        t.Equal("the first trigger", tr.Description, "the description from the comment");
        t.True(tr.Conditions[1].Form == NodeForm.Check && tr.Conditions[1].Source.Contains("api.vars.number"), "Check kept as code");
        t.Equal("two seconds", tr.Conditions[0].Trail, "the note after the node");
        t.True(tr.Actions[0].Get("pos")!.Kind == ParamKind.Expr && tr.Actions[0].Get("pos")!.Text == "SPOTS[1]", "an expression parameter kept as Lua");
        t.Equal("a label", tr.Actions[0].Label.Single(), "the label line");
        t.True(tr.Actions[1].Form == NodeForm.Script && tr.Actions[1].Source.Contains("-- a comment in code"), "the Script's code with its comment");
        t.True(tr.Actions[2].Code == "ABCD" && tr.Actions[2].Ctor == "Node(\"ABCD\")", "an unknown type");
        t.Equal("local", tr.Actions[3].Params.Last().Key, "a bracketed key");
        t.True(s.Items[2] is EdCode c2 && c2.Source.StartsWith("for i", StringComparison.Ordinal), "the loop is a code block");
        t.True(s.Items[3] is EdCode c3 && c3.TriggerName == "computed", "a trigger with a computed flag is a code block");
        var w = s.Write();
        var why = SameDefs(Defs("debug_mode = nil\n" + src), Defs("debug_mode = nil\n" + w));
        t.True(why == null, "the engine gets the same triggers from the written file: " + why);
        t.Equal(w, TriggerScript.Parse(w).Write(), "stable");
        // an edit of the trigger keeps the code around it
        tr.Name = "renamed"; tr.Actions.Add(EdNode.Of("QUIT", false));
        var d2 = Defs("debug_mode = nil\n" + s.Write());
        t.True(d2[0].Name == "renamed" && d2[0].Actions.Count == 5 && d2.Count == 4, "edited trigger and the code's triggers");
    }

    // ============================================================================================ edits and undo
    public static void EditorRegionsEditsUndo(TestContext t)
    {
        var d = MapDataDoc.CreateFlat(new NewMapSpec { Size = 256, BaseHeight = 20, Water = null });
        var h = new EditHistory(d);
        var m = d.Mission;
        int changes = 0; m.Changed += _ => changes++;
        h.Execute(MissionEdits.NewRegion(m, false, 50, 60, 30, 20));
        t.Equal(1, m.Regions.Count, "a region");
        t.Equal("Region_1", m.Regions[0].Name, "named Region_1");
        t.True(EdGuid.Is(m.Regions[0].Guid), "with a GUID");
        t.True(d.Md.Regions.Count == 1 && d.Md.Regions[0].Shapes[0].W == 30, "the map's regions follow (regions.lua)");
        // a drag: many steps, one undo step
        for (int k = 0; k < 10; k++) h.Execute(MissionEdits.MoveShape(m, 0, 0, 1, 2), merge: true);
        h.EndMerge();
        t.Equal(2, h.UndoCount, "the drag is one undo step");
        t.Equal(60.0, m.Regions[0].Shapes[0].X, "moved 10 m east");
        h.Execute(MissionEdits.SetShapeBox(m, 0, 0, 60, 80, 40, 40));
        h.Execute(MissionEdits.AddShape(m, 0, true, 120, 120, 30, 30));
        t.True(m.Regions[0].Contains(130, 135) && m.Regions[0].Contains(70, 90) && !m.Regions[0].Contains(10, 10), "two shapes, the oval's inside");
        t.True(!m.Regions[0].Contains(121, 121), "the oval's corner is outside");
        h.Execute(MissionEdits.Change<EdRegion>(m, 0, r => r.Shapes[1].Enabled = false, "Switch off"));
        t.True(!m.Regions[0].Contains(130, 135) && !d.Md.Regions[0].Shapes[1].Enabled, "a switched-off shape");
        // the triggers name it; a rename keeps the GUID reference
        h.Execute(MissionEdits.NewTrigger(m, "Root"));
        h.Execute(MissionEdits.ChangeTrigger(m, 0, tr => { var c = EdNode.Of("REGN", true); c.Set("rgn_guid", ParamValue.Str(m.Regions[0].Guid)); tr.Conditions.Add(c); tr.Actions.Add(EdNode.Of("QUIT", false)); }));
        h.Execute(MissionEdits.Rename<EdRegion>(m, 0, "Goal"));
        t.Equal("Goal", m.Regions[0].Name, "renamed");
        t.True(TriggerValidation.Check(m).All(i => i.Severity != Severity.Error && !i.Message.Contains("region")), "the trigger still finds it");
        // variables and quests: renames follow into the triggers
        h.Execute(MissionEdits.Add(m, new EdVariable { Name = "kills", Value = "0" }));
        h.Execute(MissionEdits.ChangeTrigger(m, 0, tr => { var c = EdNode.Of("CVAR", true); c.Set("varname", ParamValue.Str("kills")); c.Set("value", ParamValue.Of("3")); tr.Conditions.Add(c); }));
        h.Execute(MissionEdits.Rename<EdVariable>(m, 0, "dead_enemies"));
        t.Equal("dead_enemies", ((EdTrigger)m.Triggers.Items[0]).Conditions[1].Value("varname"), "the Var condition follows the rename");
        h.Execute(MissionEdits.NewQuest(m));
        t.True(m.Quests.Count == 1 && m.Text(m.Quests[0].KeyHeadline) == "Quest_1", "a quest with its text keys");
        h.Execute(MissionEdits.SetQuestText(m, 0, "headline", "Reach the goal"));
        t.True(m.Quests[0].Headline == "Reach the goal" && m.Text(m.Quests[0].KeyHeadline) == "Reach the goal", "the headline and its text");
        h.Execute(MissionEdits.Add(m, new EdGroup { Guid = EdGuid.New(), Name = "Starters", Members = { EdGuid.Of("Start 1") } }));
        h.Execute(MissionEdits.Add(m, new EdCamera { Name = "Overview", X = 128, Y = 128 }));
        h.Execute(MissionEdits.Add(m, new EdQuestionMark { Guid = EdGuid.New(), Name = "qm_1", X = 100, Y = 90 }));
        h.Execute(MissionEdits.SetStartCamera(m, 1.2, 100, 110));
        // the world written
        var w = m.ToWorld();
        t.Equal(1, w["regions"]!.AsArray().Count, "world.lua regions");
        t.True(w["regions"]![0]!["bbox"] is System.Text.Json.Nodes.JsonArray, "with its box");
        t.Equal("0", w["variables"]!["dead_enemies"]!["value"].Str(), "world.lua variables");
        t.True(w["trigger_defaults"]?["conditions"]?["REGN"]?["obj_owner"].Str() == "-2", "the catalogue's defaults for a new map");
        t.Equal(1, w["cameras"]!.AsArray().Count, "camera points");
        // undo everything: as new
        while (h.Undo()) { }
        t.True(m.Regions.Count == 0 && m.Variables.Count == 0 && m.Quests.Count == 0 && m.Groups.Count == 0 && m.Cameras.Count == 0 && m.QuestionMarks.Count == 0 && !m.Triggers.Items.Any(), "all undone");
        t.True(d.Md.Regions.Count == 0 && m.Texts.All(kv => kv.Value.Count == 0) && m.StartX == null, "the map's regions and texts too");
        t.True(!h.Dirty, "clean");
        while (h.Redo()) { }
        t.True(m.Regions.Count == 1 && m.Regions[0].Name == "Goal" && m.Quests[0].Headline == "Reach the goal" && m.StartX == 100, "redone");
        t.True(changes > 20, "the changes were announced");
        // a clone (Test map) is independent
        var c2 = (MapDataDoc)d.Clone();
        c2.Mission.Regions[0].Name = "other";
        t.Equal("Goal", m.Regions[0].Name, "the snapshot's mission data is a copy");
        t.True(c2.Mission.Regions.Count == 1 && c2.Md.Regions.Count == 1, "the copy has the regions");
    }

    // ============================================================================================ Test map with triggers
    /// <summary>a map made in code: a flat map, a region in a corner, a trigger "a unit of player 0 enters the region ->
    /// the mission is won", written as Test map writes it and played headless as a mission</summary>
    public static void EditorTriggersTestMapWins(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("configuration base or its data not found");
        var d = MapDataDoc.CreateFlat(new NewMapSpec { Size = 256, BaseHeight = 20, Water = null, Setting = "Savanna" });
        var h = new EditHistory(d);
        var m = d.Mission;
        h.Execute(MissionEdits.NewRegion(m, true, 20, 20, 30, 30, 20, "Goal"));
        h.Execute(MissionEdits.NewTrigger(m, "Root", "reach the goal"));
        h.Execute(MissionEdits.ChangeTrigger(m, 0, tr =>
        {
            var r = EdNode.Of("REGN", true);
            r.Set("rgn_guid", ParamValue.Str(m.Regions[0].Guid)); r.Set("obj_owner", ParamValue.Of("0")); r.Set("obj_type", ParamValue.Str("CHTR|")); r.Set("obj_count", ParamValue.Str(">0"));
            tr.Conditions.Add(r);
            tr.Actions.Add(EdNode.Of("QUIT", false));
        }));
        t.True(m.HasTriggers, "the map has triggers: Test map plays it as a mission");
        t.True(TriggerValidation.Check(m, Std()).All(i => i.Severity != Severity.Error), "no problems: " + string.Join("; ", TriggerValidation.Check(m, Std())));
        var folder = TestMission.Write(d.Clone(), TestMission.NewFolder(), new[] { "Hu", "Aje", "Ninigi" }, tr => c!.Rules.StartOf(tr)?.Units ?? new List<string>());
        t.True(TestMission.IsTestFolder(folder) && File.Exists(Path.Combine(folder, "triggers.lua")) && File.Exists(Path.Combine(folder, "world.lua")), "the mission folder");
        var start = new PwRemake.Sim.Game.GameStart { Configuration = "base", MissionFolder = folder, Mission = 99, Seed = 3 };
        var s = PwRemake.Sim.Campaign.MissionSession.Create(start, c!.Config, new PwRemake.Sim.Campaign.SessionOptions
        {
            Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true, LuaStd = Std(),
        });
        t.True(s.Problems.Count == 0, "loaded without problems: " + string.Join("; ", s.Problems));
        t.Equal(1, s.Engine!.Triggers.Count, "the trigger is in the engine");
        var mine = s.Host.PlayerObjects(0).Where(o => s.Host.Info(o)?.Type == "CHTR").ToList();
        t.True(mine.Count > 0, $"player 0 has characters ({mine.Count})");
        for (int k = 0; k < 60; k++) s.Step();
        t.True(!s.Engine.Won, "3 s: nobody in the region, not won");
        var (gx, gz) = s.Campaign.ToGame(35, 35);
        s.Host.Teleport(mine[0], 35, 35);
        for (int k = 0; k < 40 && !s.Engine.Won; k++) s.Step();
        t.True(s.Engine.Won, "a unit in the region: the mission is won");
        t.Log($"won at {s.World.Time:0.00} s; the unit at game ({gx:0.0}, {gz:0.0})");
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }
}
