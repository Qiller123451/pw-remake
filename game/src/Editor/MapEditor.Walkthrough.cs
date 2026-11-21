// --editor-walkthrough (docs/MAP_EDITOR.md "Tests", docs/DEBUG.md): the tutorial of docs/MAP_EDITOR.md played through
// the real game, from an empty map to a mission that is won - every step through the editor's own paths (the tools'
// commands, the history, the document, the view, Save, Open, Test map, the game session):
//   1. a new flat map (the editor's start without --map): a hill raised, ground painted, a forest planted, a head quarters
//      and three workers for each of the two players beside their start locations (players 1 and 2 Norsemen, no main
//      building of their own: the placed one is theirs), a region "Goal" with a trigger "a character of player 0 in Goal
//      -> the mission is won";
//   2. saved into My maps ("Walkthrough"), opened again: the same map (heights, objects, region, trigger) and nothing unsaved;
//   3. Test map: the map has triggers - it plays as a mission; at game time 2 s a worker of player 0 is sent to the goal
//      (a move command, as a click would give it), the trigger fires, the mission is won.
// EDITOR walkthrough lines in the log, "EDITOR walkthrough ok" at the end (or "... FAIL"). --editor-shots <dir>: a
// picture of the editor (the reopened map) and of the won game.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Editor.Triggers;
using PwRemake.Sim.Maps;

namespace PwRemake.Editor;

public partial class MapEditor
{
    static int s_walk;
    static string? s_walkPath;
    static (double X, double Y) s_goal;
    static int s_walkBad;
    static string? s_walkState;

    void WalkT(bool ok, string what) { L.Info($"EDITOR walkthrough {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) s_walkBad++; }

    /// <summary>called when the view is loaded: the walkthrough's next step</summary>
    void WalkthroughStep()
    {
        if (s_walk == 0) { s_walk = 1; WalkBuild(); }
        else if (s_walk == 1) { s_walk = 2; WalkReopened(); }
    }

    /// <summary>a picture of the screen (--editor-shots &lt;dir&gt;); Boot's viewport - the editor may be gone (Test map)</summary>
    static void WalkShot(string name)
    {
        if (!GameApp.UserArgs().TryGetValue("editor-shots", out var dir)) return;
        Directory.CreateDirectory(dir);
        var img = Boot.Instance.GetViewport().GetTexture().GetImage();
        var path = Path.Combine(dir, name);
        if (path.EndsWith(".jpg", StringComparison.Ordinal)) img.SaveJpg(path, 0.88f); else img.SavePng(path);
        L.Info($"EDITOR walkthrough picture {path}");
    }

    /// <summary>step 1: the map made and saved, then opened again</summary>
    void WalkBuild()
    {
        var d = Doc;
        var slocs = d.Objects.Where(o => o.Type == "SLOC").OrderBy(o => o.Owner).ToList();
        WalkT(slocs.Count == 2 && !History.Dirty, $"a new map: {d.Name}, {d.HeightsW * d.Cell} m, {slocs.Count} start locations");
        if (slocs.Count < 2) { WalkEnd(); return; }
        var s0 = slocs[0]; var s1 = slocs[1];
        double cx = (s0.X + s1.X) / 2, cy = (s0.Y + s1.Y) / 2;
        // a hill between the players (the raise brush, one stroke)
        var b = EditorSession.Brush;
        double r0 = b.Radius, st0 = b.Strength; bool lo = b.Lower;
        b.Radius = 40; b.Strength = 8; b.Lower = false;
        double h0 = d.HeightAt(cx, cy);
        for (int k = 0; k < 20; k++) History.Execute(b.Step(d, cx + k * 0.5, cy, 0.25)!, merge: true);
        History.EndMerge();
        WalkT(d.HeightAt(cx, cy) > h0 + 15, $"a hill: {h0:0.0} -> {d.HeightAt(cx, cy):0.0} m");
        // the ground painted round the hill, a forest beside it
        History.Execute(PaintTools.Materials(d, new HeightBrush { Radius = 30 }, cx - 60, cy + 60, 2)!);
        WalkT(d.MaterialAt(cx - 60, cy + 60) == 2, "ground painted (material 2)");
        int f0 = d.Objects.Count;
        var fe = VegetationTools.PaintForest(d, EditorSession.Forest, new HeightBrush { Radius = 45 }, cx + 70, cy - 70, new ForestPaint());
        if (fe != null) History.Execute(fe);
        WalkT(d is MapDataDoc md0 && md0.Md.Forest.Blocks.Count > 0, $"a forest planted ({(d as MapDataDoc)?.Md.Forest.Blocks.Count} squares)");
        b.Radius = r0; b.Strength = st0; b.Lower = lo;
        // the players: Norsemen both, no main building of their own (the placed head quarters is theirs)
        History.Execute(PlayersEdit.Make(d, p => { for (int i = 0; i < 2; i++) { p.Slots[i].Tribe = "Hu"; p.Slots[i].IncludeBuildings = false; } p.Slots[1].Control = "computer"; }));
        // a head quarters and three workers for each player beside its start location
        var start = EditorSession.Rules.StartOf("Hu");
        var hq = ObjectToolState.EntryOf(start?.Base ?? "hu_headquarters"); var wk = ObjectToolState.EntryOf("hu_worker");
        WalkT(hq != null && wk != null, $"the palette has the Norsemen's main building ({hq?.Cls}) and hu_worker");
        if (hq == null || wk == null) { WalkEnd(); return; }
        var made = new List<MapObject>();
        foreach (var (s, i) in new[] { (s0, 0), (s1, 1) })
        {
            double dir = i == 0 ? 1 : -1;
            made.Add(ObjectOps.Create(hq, d, s.X - dir * 22, s.Y - dir * 22, 0.8, i, 0, EditorSession.Rules));
            for (int k = 0; k < 3; k++) made.Add(ObjectOps.Create(wk, d, s.X + dir * (6 + k * 3), s.Y + dir * 4, 0, i, 0, EditorSession.Rules));
        }
        History.Execute(ObjectsAddRemove.Add(d, made, "Place the bases"));
        WalkT(d.Objects.Count == f0 + 8 && d.Objects.Count(o => o.Cls == "hu_worker" && o.Owner == 1) == 3, $"placed: {d.Objects.Count - f0} objects (a head quarters and 3 workers each)");
        // the region "Goal" 40 m from player 1's start (toward the middle) and the trigger
        var m = Mis;
        double ux = (cx - s0.X), uy = (cy - s0.Y), ul = Math.Sqrt(ux * ux + uy * uy);
        s_goal = (s0.X + ux / ul * 40, s0.Y + uy / ul * 40);
        History.Execute(MissionEdits.NewRegion(m, false, s_goal.X - 10, s_goal.Y - 10, 20, 20, d.HeightAt(s_goal.X, s_goal.Y), "Goal"));
        History.Execute(MissionEdits.NewTrigger(m, "Root", "Win at the goal"));
        int ti = m.Triggers.Items.Count - 1;
        string goalGuid = m.Regions.First(x => x.Name == "Goal").Guid;
        History.Execute(MissionEdits.ChangeTrigger(m, ti, t =>
        {
            t.Description = "A character of player 0 in Goal: the mission is won";
            var c = EdNode.Of("REGN", true);
            c.Set("rgn_guid", ParamValue.Str(goalGuid)); c.Set("obj_owner", ParamValue.Of("0")); c.Set("obj_type", ParamValue.Str("CHTR|")); c.Set("obj_count", ParamValue.Str(">0"));
            t.Conditions.Add(c);
            t.Actions.Add(EdNode.Of("QUIT", false));
        }));
        var issues = TriggerValidation.CheckAll(m);
        WalkT(m.HasTriggers && issues.All(x => x.Severity != PwRemake.Sim.Editor.Triggers.Severity.Error), $"the region and the trigger ({m.Triggers.Triggers.Count()} triggers, {issues.Count} remarks)");
        // save into My maps, then open it again (as Open does)
        s_walkPath = Path.Combine(EditorSession.UserMapsDir, "Walkthrough");
        if (Directory.Exists(s_walkPath)) Directory.Delete(s_walkPath, true);
        SaveTo(s_walkPath);
        WalkT(Directory.Exists(s_walkPath) && File.Exists(Path.Combine(s_walkPath, "triggers.lua")) && !History.Dirty && Doc.SourcePath == s_walkPath, $"saved: {s_walkPath}");
        s_walkState = WalkState(Doc);
        OpenPath(s_walkPath);
    }

    static string WalkState(IEditorMapDoc d)
    {
        var md = ((MapDataDoc)d).Md;
        return $"{md.Heights.Sum(h => (double)h):0.000}|{md.Mats.Sum(x => x)}|{md.Forest.Blocks.Count}|{string.Join(";", md.Objects.Select(o => $"{o.Cls}:{o.Owner}:{o.X:0.000}:{o.Y:0.000}"))}|" +
               $"{string.Join(";", d.Mission.Regions.Select(r => r.Name))}|{d.Mission.Triggers.Write().Length}";
    }

    /// <summary>step 2: the map opened again - the same; a picture; Test map</summary>
    void WalkReopened()
    {
        WalkT(Doc.SourcePath == s_walkPath && !History.Dirty && History.UndoCount == 0, "opened again from My maps, nothing unsaved");
        WalkT(WalkState(Doc) == s_walkState, "the same map as saved (heights, ground, forest, objects, region, trigger)");
        WalkT(SkirmishMapsHas("User/Walkthrough"), "the skirmish list's My maps has it");
        // the picture: the region shown, the camera over the first player's base and the hill
        EditorSession.ToolGroup = "Regions";
        BuildPalette(); BuildProperties();
        var s0 = Doc.Objects.Where(o => o.Type == "SLOC").OrderBy(o => o.Owner).First();
        var s1 = Doc.Objects.Where(o => o.Type == "SLOC").OrderBy(o => o.Owner).Last();
        var (gx, gz) = _src!.ToGame(s0.X + (s1.X - s0.X) * 0.33, s0.Y + (s1.Y - s0.Y) * 0.33);
        _cam!.X = gx; _cam.Z = gz; _cam.Dist = _cam.TDist = 230;
        GetTree().CreateTimer(2.0).Timeout += () =>
        {
            WalkShot("map_editor_walkthrough.jpg");
            WalkTestMap();
        };
    }

    static bool SkirmishMapsHas(string id)
    {
        try { return PwRemake.Sim.Game.SkirmishMaps.List(EditorSession.Config).Any(m => m.Path == id); }
        catch (Exception) { return false; }
    }

    /// <summary>step 3: Test map (a mission: the map has triggers); a worker sent to the goal; won</summary>
    void WalkTestMap()
    {
        var tree = GetTree();
        bool sent = false; double wonAt = -1;
        PwRemake.Sim.World.Unit? walker = null;
        var shots = _args.ContainsKey("editor-shots");
        var real = System.Diagnostics.Stopwatch.StartNew();
        void Watch()
        {
            if (real.Elapsed.TotalSeconds > 240) { tree.ProcessFrame -= Watch; WalkT(false, "the mission was not won within 240 s"); WalkEnd(); return; }
            if (GameSession.Current is not { } s || s.Mission is not { } ms || !s.EditorTest) return;
            if (!sent && s.World.Time >= 2)
            {
                sent = true;
                var mine = s.World.Units.Where(u => u.Alive && u.Owner?.Id == 0).ToList();
                var (x, z) = ms.Campaign.ToGame(s_goal.X, s_goal.Y);
                WalkT(mine.Count > 0, $"Test map runs as a mission: {s.World.Players.Count} players, player 0 has {mine.Count} units, {s.World.Buildings.Count(b => b.Owner?.Id == 0)} buildings");
                WalkT(ms.Engine?.Triggers.Count == 1 && !ms.Won, "the trigger is in the engine, not fired yet");
                if (mine.Count == 0) { tree.ProcessFrame -= Watch; WalkEnd(); return; }
                bool InGoal(PwRemake.Sim.World.Entity e) => Math.Abs(e.Pos.X - x) <= 10 && Math.Abs(e.Pos.Z - z) <= 10;
                WalkT(!mine.Any(InGoal), "nobody of player 0 in the goal yet");
                var u = walker = mine.OrderBy(e => (e.Pos.X - x) * (e.Pos.X - x) + (e.Pos.Z - z) * (e.Pos.Z - z)).First();
                s.Submit(new PwRemake.Sim.Commands.GroupMoveCommand { Player = 0, Units = new[] { u.Id }, X = x, Z = z });
                L.Info($"EDITOR walkthrough: {u.Name} {u.Id} sent to the goal at game ({x:0}, {z:0}), {Math.Sqrt((u.Pos.X - x) * (u.Pos.X - x) + (u.Pos.Z - z) * (u.Pos.Z - z)):0} m away");
            }
            if (sent && wonAt < 0 && (ms.Won || ms.Engine?.Won == true))
            {
                wonAt = s.World.Time;
                var (gx, gz) = ms.Campaign.ToGame(s_goal.X, s_goal.Y);
                bool there = walker != null && Math.Abs(walker.Pos.X - gx) <= 10.5 && Math.Abs(walker.Pos.Z - gz) <= 10.5;
                WalkT(there, $"the trigger fired when the worker reached the goal: the mission is won at game time {wonAt:0.0} s (the worker {Math.Sqrt((walker!.Pos.X - gx) * (walker.Pos.X - gx) + (walker.Pos.Z - gz) * (walker.Pos.Z - gz)):0.0} m from its centre)");
                if (!shots) { tree.ProcessFrame -= Watch; WalkEnd(); return; }
                var r2 = System.Diagnostics.Stopwatch.StartNew();
                void Later() { if (r2.Elapsed.TotalSeconds < 8) return; tree.ProcessFrame -= Later; WalkShot("map_editor_walkthrough_won.jpg"); WalkEnd(); }
                tree.ProcessFrame -= Watch;
                tree.ProcessFrame += Later;
            }
        }
        tree.ProcessFrame += Watch;
        TestMap();
    }

    static void WalkEnd()
    {
        Log.For("Editor").Info($"EDITOR walkthrough {(s_walkBad == 0 ? "ok" : s_walkBad + " failed")}");
        Log.Flush();
        Boot.Instance.Quit();
    }
}
