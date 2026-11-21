// Tests of the map editor's plain classes (src/Sim/Editor, docs/MAP_EDITOR.md): undo / redo and the dirty flag
// (EditHistory), merging a brush stroke into one undo step, the raise / lower brush's maths (HeightBrush), the ground
// pick, the in-memory document (MapDataDoc: a new flat map, an original map, its walk grid following the edits and
// coming back on undo) and Test map: a skirmish built from the edited map in memory (EditorPlaytest), which leaves the
// edited map unchanged.
using System;
using System.IO;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;

namespace PwRemake.Tests;

[TestClass]
public static class EditorTests
{
    const string Jungle2 = "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_2_jun_001.ula";

    static MapDataDoc Flat(int size = 256, double h = 20) => MapDataDoc.CreateFlat(new NewMapSpec { Size = size, BaseHeight = h, Water = 16, Setting = "Savanna" });

    /// <summary>an edit that sets one sample</summary>
    static HeightEdit Set(IEditorMapDoc d, int i, int j, float h) { var e = new HeightEdit(); e.Add(i, j, d.HeightsW, d.Height(i, j), h); return e; }

    public static void EditorNewFlatMap(TestContext t)
    {
        var d = Flat(300, 22);
        t.Equal(320, (int)(d.HeightsW * d.Cell), "the size rounded up to the 32 m blocks");
        t.Equal(160, d.HeightsW, "2 m samples");
        t.True(Enumerable.Range(0, d.HeightsW).All(i => d.Height(i, 7) == 22f), "flat at the base height");
        t.Equal(16.0, d.Water, "water level");
        t.Equal("Savanna", d.Setting, "setting");
        t.Equal(2, d.Md.Objects.Count(o => o.Type == "SLOC"), "two start locations");
        t.True(d.CanSave && d.SourcePath == null, "a new map: it can be saved, it has no place yet (Save asks a name)");
        t.True(d.WalkableAt(100, 100), "flat land above the water is walkable (no walk grid: the slope rule)");
        var nw = MapDataDoc.CreateFlat(new NewMapSpec { Size = 256, BaseHeight = 10, Water = 16 });
        t.True(!nw.WalkableAt(100, 100), "under the water: not walkable");
        t.True(NewMapSpec.Sizes(new[] { 512, 384, 512, 1000 }).SequenceEqual(new[] { 384, 512, 1024 }), "sizes: distinct, sorted, 32 m blocks");
        t.True(NewMapSpec.Sizes(Array.Empty<int>()).Count > 0, "sizes without maps: the usual ones");
    }

    public static void EditorUndoRedoDirty(TestContext t)
    {
        var d = Flat();
        var h = new EditHistory(d);
        int changes = 0; h.Changed += () => changes++;
        t.True(!h.Dirty && !h.CanUndo && !h.CanRedo, "fresh: clean, nothing to undo");
        h.Execute(Set(d, 10, 10, 30));
        t.Equal(30f, d.Height(10, 10), "done");
        t.True(h.Dirty && h.CanUndo, "dirty after an edit");
        h.Execute(Set(d, 11, 10, 31));
        t.Equal(2, h.UndoCount, "two separate edits (no merge)");
        t.True(h.Undo(), "undo");
        t.Equal(20f, d.Height(11, 10), "undone");
        t.Equal(30f, d.Height(10, 10), "the first stays");
        t.True(h.CanRedo && h.RedoName == "Raise / lower terrain", "redo offered");
        t.True(h.Redo(), "redo");
        t.Equal(31f, d.Height(11, 10), "redone");
        h.MarkSaved();
        t.True(!h.Dirty, "clean after save");
        h.Undo();
        t.True(h.Dirty, "undo after save: dirty");
        h.Redo();
        t.True(!h.Dirty, "redo back to the saved state: clean");
        h.Undo(); h.Undo();
        t.True(h.Dirty && !h.CanUndo, "everything undone");
        t.Equal(20f, d.Height(10, 10), "back to the start");
        h.Execute(Set(d, 5, 5, 25));
        t.True(!h.CanRedo, "a new edit drops the redo steps");
        t.True(h.Undo() && !h.Undo(), "undo with nothing left: false");
        t.True(changes >= 9, $"Changed raised ({changes})");
        // the oldest steps go beyond the limit; the saved state then cannot come back
        var d2 = Flat(); var h2 = new EditHistory(d2) { Limit = 3 };
        for (int k = 0; k < 5; k++) h2.Execute(Set(d2, k, 0, 40 + k));
        t.Equal(3, h2.UndoCount, "limit 3");
        while (h2.Undo()) { }
        t.True(h2.Dirty, "the first two edits cannot be undone: still dirty");
        t.Equal(41f, d2.Height(1, 0), "the dropped edits stay");
        t.Equal(20f, d2.Height(2, 0), "the kept ones undone");
        h2.Clear();
        t.True(!h2.Dirty && !h2.CanUndo, "Clear: clean");
    }

    public static void EditorStrokeMergesIntoOneUndo(TestContext t)
    {
        var d = Flat();
        var h = new EditHistory(d);
        var b = new HeightBrush { Radius = 10, Strength = 5 };
        double mx = 128, my = 128;
        var before = (float[])d.Md.Heights.Clone();
        for (int k = 0; k < 10; k++) h.Execute(b.Step(d, mx + k, my, 0.1)!, merge: true);
        t.Equal(1, h.UndoCount, "one stroke = one undo step");
        t.True(d.Height(64, 64) > 20.5f, $"raised ({d.Height(64, 64)})");
        h.EndMerge();
        b.Lower = true;
        for (int k = 0; k < 5; k++) h.Execute(b.Step(d, mx, my + 20, 0.1)!, merge: true);
        t.Equal(2, h.UndoCount, "a second stroke after EndMerge");
        t.True(d.Height(64, 74) < 20f, "lowered");
        h.Undo();
        t.Equal(20f, d.Height(64, 74), "the second stroke undone");
        h.Undo();
        t.True(d.Md.Heights.SequenceEqual(before), "both strokes undone: every sample as before");
        h.Redo(); h.Redo();
        t.True(d.Height(64, 74) < 20f && d.Height(64, 64) > 20.5f, "both redone");
        // a merged edit keeps the first old and the last new height of a sample
        var e1 = new HeightEdit(); e1.Add(3, 4, d.HeightsW, 1, 2);
        var e2 = new HeightEdit(); e2.Add(3, 4, d.HeightsW, 2, 5); e2.Add(6, 4, d.HeightsW, 7, 8);
        t.True(e1.TryMerge(e2), "merged");
        t.True(e1.Of(3, 4) == (1f, 5f) && e1.Of(6, 4) == (7f, 8f) && e1.Count == 2, "first old, last new");
        t.Equal(new GridRect(3, 4, 6, 4), e1.Rect, "the rectangle of both");
    }

    public static void EditorBrushMaths(TestContext t)
    {
        t.Equal(1.0, HeightBrush.Falloff(0, 10), "1 at the centre");
        t.Equal(0.0, HeightBrush.Falloff(10, 10), "0 at the radius");
        t.Equal(0.0, HeightBrush.Falloff(12, 10), "0 beyond");
        t.Near(0.5625, HeightBrush.Falloff(5, 10), 1e-12, "(1 - 0.25)^2 halfway");
        t.True(HeightBrush.Falloff(3, 10) > HeightBrush.Falloff(4, 10), "falls off");
        var d = Flat();
        var b = new HeightBrush { Radius = 8, Strength = 10 };
        var e = b.Step(d, 100, 100, 0.5)!;            // sample (50, 50) at the centre
        t.True(e.Of(50, 50) == (20f, 25f), "the centre: strength x dt");
        t.Near(20 + 5 * HeightBrush.Falloff(4, 8), e.Of(52, 50)!.Value.New, 1e-5, "4 m away: the falloff");
        t.True(e.Of(54, 50) == null && e.Of(46, 50) == null, "nothing at the radius (8 m)");
        t.Equal(e.Of(52, 50)!.Value.New, e.Of(48, 50)!.Value.New, "symmetric");
        t.Equal(20f, d.Height(50, 50), "a step is not done by itself");
        t.Equal(new GridRect(47, 47, 53, 53), e.Rect, "the touched samples");
        t.True(b.Step(d, -100, -100, 0.5) == null, "outside the map: nothing");
        t.True(b.Step(d, 100, 100, 0) == null, "no time: nothing");
        // footprint clamped at the edge
        t.Equal(new GridRect(0, 0, 4, 4), HeightBrush.Footprint(d, 0, 0, 8), "the corner");
        // the height limits
        var hi = MapDataDoc.CreateFlat(new NewMapSpec { Size = 128, BaseHeight = HeightBrush.MaxHeight - 1 });
        var he = new HeightBrush { Radius = 4, Strength = 100 }.Step(hi, 64, 64, 1)!;
        t.Equal(HeightBrush.MaxHeight, he.Of(32, 32)!.Value.New, "clamped at the top");
        t.True(new HeightBrush { Radius = 4, Strength = 100 }.Step(MapDataDoc.CreateFlat(new NewMapSpec { Size = 128, BaseHeight = HeightBrush.MaxHeight }), 64, 64, 1) == null, "at the top already: no edit");
        var lo = new HeightBrush { Radius = 4, Strength = 100, Lower = true }.Step(d, 64, 64, 1)!;
        t.Equal(HeightBrush.MinHeight, lo.Of(32, 32)!.Value.New, "clamped at the bottom");
    }

    public static void EditorGroundPick(TestContext t)
    {
        // a ground at 10 m, a hill rising to the east (x > 50)
        double H(double x, double z) => x > 50 ? 10 + (x - 50) : 10;
        var p = GroundPick.Ray(0, 110, 0, 0, -1, 0, H)!.Value;
        t.Near(10, p.Y, 1e-3, "straight down onto the flat");
        var q = GroundPick.Ray(0, 50, 0, 1, -0.2, 0, H)!.Value;    // 50 m up, slanting down to the east
        t.Near(H(q.X, q.Z), q.Y, 1e-3, "on the ground");
        t.True(q.X > 50 && q.X < 200, $"meets the hill ({q.X:0.0})");
        t.True(GroundPick.Ray(0, 50, 0, 0, 1, 0, H) == null, "upwards: the sky");
    }

    public static void EditorOriginalMapWalkGrid(TestContext t)
    {
        if (!File.Exists(Jungle2)) t.Skip("multi_2_jun_001 not staged");
        var d = MapDataDoc.Open(Jungle2);
        t.True(d.Md.Walk != null, "the map has its walk grid");
        var walk0 = (byte[])d.Md.Walk!.Cells.Clone();
        var h0 = (float[])d.Md.Heights.Clone();
        // a flat walkable spot near the centre
        double mx = d.HeightsW, my = d.HeightsH;
        for (int k = 0; k < 400 && !(d.WalkableAt(mx, my) && d.HeightAt(mx, my) > (d.Water ?? 0) + 2); k++) { mx += 7; if (mx > d.HeightsW * 2 - 40) { mx = 40; my += 9; } }
        t.True(d.WalkableAt(mx, my), $"a walkable spot ({mx:0}, {my:0})");
        int changes = 0; DocChange last = default;
        d.Changed += c => { changes++; last = c; };
        var h = new EditHistory(d);
        var b = new HeightBrush { Radius = 6, Strength = 60 };
        for (int k = 0; k < 6; k++) h.Execute(b.Step(d, mx, my, 0.1)!, merge: true);
        t.True(changes == 6 && last.Kind == DocChangeKind.Heights && !last.Rect.IsEmpty, "a Heights change per step");
        t.True(d.HeightAt(mx, my) > h0[(int)(my / 2) * d.HeightsW + (int)(mx / 2)] + 30, "a steep hill");
        int blocked = Enumerable.Range(0, walk0.Length).Count(k => walk0[k] == 1 && d.Md.Walk.Cells[k] == 0);
        t.True(blocked > 0, $"its steep sides are not walkable any more ({blocked} cells)");
        // the map source the view and Test map use sees the edit (it reads the same samples)
        var c = CampaignTests.LoadCfgPublic("base");
        if (c != null)
        {
            var S = d.ToSource(new RulesMapAdapter(c.Rules), c.Models.Has, c.Forest);
            var (x, z) = S.ToGame(mx, my);
            t.Near(d.HeightAt(mx, my), S.Height(x, z), 1e-4, "the map source has the edited height");
        }
        h.Undo();
        t.True(d.Md.Heights.SequenceEqual(h0), "undo: the heights as opened");
        t.True(d.Md.Walk.Cells.SequenceEqual(walk0), "undo: the map's own walk grid exactly");
        // a snapshot is a copy
        var snap = (MapDataDoc)d.Clone();
        snap.SetHeight(3, 3, 99);
        t.True(d.Height(3, 3) != 99 && snap.Md.Walk != d.Md.Walk && snap.Md.Objects.Count == d.Md.Objects.Count, "Clone: own grids, the same objects");
    }

    public static void EditorTestMapSkirmish(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no configuration base");
        var d = MapDataDoc.CreateFlat(new NewMapSpec { Size = 384, BaseHeight = 20, Water = 16, Setting = "Jungle", Name = "Editor test" });
        var h = new EditHistory(d);
        var b = new HeightBrush { Radius = 20, Strength = 10 };
        for (int k = 0; k < 10; k++) h.Execute(b.Step(d, 192, 192, 0.1)!, merge: true);   // a 10 m hill in the middle
        var heights = (float[])d.Md.Heights.Clone();
        string key = EditorPlaytest.Register(d);
        t.True(EditorPlaytest.IsKey(key) && !EditorPlaytest.IsKey("Base/Maps/x.ula"), "the key");
        var setup = Skirmish.DefaultSetup("Hu", "Aje");
        setup.Map = key; setup.MapName = d.Name; setup.Points = -1; setup.Seed = 3;
        var S = EditorPlaytest.Source(key, new RulesMapAdapter(c!.Rules), c.Models.Has, c.Forest);
        var sk = SkirmishSession.Create(setup, null, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, null, SimFlags.Game, S);
        var W = sk.World;
        t.Equal("Editor test", sk.Map.Name, "the editor's map");
        t.Near(30, sk.Built.Map.Hf.At(0, 0), 0.05, "the world stands on the edited ground (the hill)");
        t.Near(20, sk.Built.Map.Hf.At(120, 0), 1e-4, "flat away from it");
        t.Equal(2, W.Players.Count, "you and one computer player");
        t.Equal(2, sk.Homes.Count, "each with its home (the Norsemen's fireplace, the Dustriders' collector)");
        for (int i = 0; i < 400; i++) sk.Step();
        t.True(d.Md.Heights.SequenceEqual(heights), "the edited map is unchanged by the game (buildings levelled the snapshot's ground)");
        t.True(EditorPlaytest.Doc(key) != null && EditorPlaytest.Doc("editor:0") == null, "only the registered key");
        EditorPlaytest.Clear();
        try { EditorPlaytest.Source(key, new RulesMapAdapter(c.Rules), c.Models.Has, c.Forest); t.True(false, "a forgotten key throws"); }
        catch (InvalidOperationException) { }
    }
}
