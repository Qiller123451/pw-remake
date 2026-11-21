// The map editor's Paint tools (docs/MAP_EDITOR.md "Paint"; the maths: src/Sim/Editor/Terrain/PaintTools.cs and
// VegetationTools.cs): ground materials, biomes, walkability, forest blocks, plants. They use the shared brush (radius,
// circle / square, falloff); each held stroke is one undo step.
//
//   Ground        left: paint the chosen material (the setting's eight textures as swatches); Ctrl+click: pick the
//                 material under the cursor. A soft brush dithers the edge, "Hard" paints a clean one.
//   Biomes        left: paint the chosen biome (docs/BIOMES.md: a soft blob, strength per step); a biome the map has
//                 not got yet is added (the ground is drawn anew once, a second or two)
//   Walkability   left: paint walkable / blocked / auto; Ctrl: auto. The cells around the cursor are shown: blocked red,
//                 painted ones brighter
//   Forest        left: forest squares (32 m; density, the setting's five tree kinds, undergrowth); Ctrl: erase
//   Plants        left: scatter the chosen plants (density, spacing, in water or not); Ctrl: erase (only the chosen
//                 kinds, if set)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Maps;

namespace PwRemake.Editor;

public static class PaintSettings
{
    public static int Material = 0;
    public static string Biome = "Jungle";
    public static double BiomeStrength = 0.3;
    public static WalkOverride Walk = WalkOverride.Blocked;
    public static readonly ForestPaint Forest = new();
    public static readonly PlantScatter Plants = new() { Density = 6, Spacing = 2.5 };
    /// <summary>the setting the plant choice belongs to</summary>
    public static string PlantSetting = "";
}

public abstract class PaintBrushTool : EditorTool
{
    public override string Group => "Paint";
    public override void Step(ToolContext c, ToolPoint? p, double dt) { if (p is { } q) c.Run(Make(c, q, dt)); }
    protected abstract IEditCommand? Make(ToolContext c, ToolPoint p, double dt);
    public override string Status(ToolContext c) => $"{Label}: radius {EditorSession.Brush.Radius:0} m";
}

public sealed class GroundPaintTool : PaintBrushTool
{
    static readonly Dictionary<string, Texture2D?> s_swatches = new();
    bool _picked;
    public override string Label => "Ground";
    public override string Tip => "Paint the ground's materials (the setting's eight textures); Ctrl + click: pick the material under the cursor";

    public override void Begin(ToolContext c, ToolPoint p)
    {
        _picked = c.Ctrl;
        if (!c.Ctrl) return;
        int m = c.Doc.MaterialAt(p.Mx, p.My);
        if (m >= 0) { PaintSettings.Material = m; c.Note($"Material {m} picked"); c.Rebuild(); }
    }
    public override void Step(ToolContext c, ToolPoint? p, double dt) { if (!_picked) base.Step(c, p, dt); }
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt) => PaintTools.Materials(c.Doc, EditorSession.Brush, p.Mx, p.My, PaintSettings.Material);

    static Texture2D? Swatch(string setting, int k)
    {
        string key = $"{setting}/{k}";
        if (s_swatches.TryGetValue(key, out var t)) return t;
        Texture2D? tex = null;
        if (GameApp.Assets?.Find($"assets/terrain/{setting}/scape_{k}.jpg") is string path && Image.LoadFromFile(path) is { } img && !img.IsEmpty())
        {
            // (the middle of the tiling texture, small)
            int s = Math.Min(img.GetWidth(), img.GetHeight()) / 2;
            img = img.GetRegion(new Rect2I(img.GetWidth() / 4, img.GetHeight() / 4, s, s));
            img.Resize(48, 48, Image.Interpolation.Bilinear);
            tex = ImageTexture.CreateFromImage(img);
        }
        return s_swatches[key] = tex;
    }

    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        string setting = c?.Doc.Setting ?? "Jungle";
        var roles = BiomeSet.Default.Get(setting)?.Roles;
        var items = Enumerable.Range(0, 8).Select(k => (Swatch(setting, k), $"{k}", $"Material {k}{(roles != null && roles[k] is { Length: > 0 } r ? $": {r}" : "")} ({setting}/scape_{k})")).ToList();
        ui.Swatches(items, PaintSettings.Material, k => PaintSettings.Material = k);
        TerrainToolSet.BrushUi(ui, strength: false);
        ui.Note("The setting's eight ground textures. A soft brush gives a dithered edge (the ground blends the cells anyway), Hard a clean one. Ctrl + click picks the material under the cursor.");
    }
    public override string Status(ToolContext c) => $"ground: material {PaintSettings.Material}, radius {EditorSession.Brush.Radius:0} m";
}

public sealed class BiomePaintTool : PaintBrushTool
{
    double _t;
    public override string Label => "Biomes";
    public override string Tip => "Paint a biome (another landscape: its ground, grass, light and ambience) - docs/BIOMES.md";
    public override void Begin(ToolContext c, ToolPoint p) => _t = 0;
    public override void Step(ToolContext c, ToolPoint? p, double dt)
    {
        // (a step every 0.06 s, as the map viewer's brush)
        if ((_t -= dt) > 0) return;
        _t = 0.06;
        base.Step(c, p, dt);
    }
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt) =>
        PaintTools.Biome(c.Doc, PaintSettings.Biome, p.Mx, p.My, EditorSession.Brush.Radius, PaintSettings.BiomeStrength);
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        var all = BiomeSet.Default.All;
        int cur = Math.Max(0, all.FindIndex(b => b.Id.Equals(PaintSettings.Biome, StringComparison.OrdinalIgnoreCase)));
        if (all.Count > 0) PaintSettings.Biome = all[cur].Id;
        ui.Options("Biome", all.Select(b => b.Name.Length > 0 ? b.Name : b.Id).ToList(), cur, k => PaintSettings.Biome = all[k].Id);
        ui.Slider("Radius", 4, 120, 1, () => EditorSession.Brush.Radius, v => EditorSession.Brush.Radius = v, v => $"{v:0} m");
        ui.Slider("Strength", 0.05, 1, 0.05, () => PaintSettings.BiomeStrength, v => PaintSettings.BiomeStrength = v, v => $"{v * 100:0} % per step");
        var l = c?.Doc.Biomes;
        ui.Note(l == null ? $"The map is all {c?.Doc.Setting}. Painting another biome adds a layer (the ground is drawn anew once)."
            : $"Biomes on the map: {string.Join(", ", l.Biomes)}. A biome not among them is added on the first stroke.");
    }
    public override string Status(ToolContext c) => $"biome {PaintSettings.Biome}: radius {EditorSession.Brush.Radius:0} m, {PaintSettings.BiomeStrength * 100:0} %";
}

public sealed class WalkPaintTool : PaintBrushTool
{
    static readonly string[] NAMES = { "Auto", "Walkable", "Blocked" };
    public override string Label => "Walkability";
    public override string Tip => "Paint where land units can walk: walkable, blocked, or auto (the slope and the water); Ctrl: auto";
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt) =>
        PaintTools.Walk(c.Doc, EditorSession.Brush, p.Mx, p.My, c.Ctrl ? WalkOverride.Auto : PaintSettings.Walk);
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        ui.Choice(NAMES, (int)PaintSettings.Walk, k => PaintSettings.Walk = (WalkOverride)k, 3,
            new[] { "As the ground gives it: the map's own grid where its ground is unchanged, else the slope (32°) and the water", "Always walkable", "Never walkable" });
        TerrainToolSet.BrushUi(ui, strength: false, curve: false);
        ui.Note("The 2 m cells round the cursor are shown: blocked red, painted ones brighter (walkable green). Ctrl paints auto.");
    }
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p)
    {
        var cur = base.Cursor(c, p);
        if (p is not { } q) return cur;
        var d = c.Doc;
        double reach = Math.Min(30, cur.Radius + 6);
        int c0 = (int)Math.Floor((q.Mx - reach - d.WalkOriginX) / d.WalkCell), c1 = (int)Math.Floor((q.Mx + reach - d.WalkOriginX) / d.WalkCell);
        int r0 = (int)Math.Floor((q.My - reach - d.WalkOriginY) / d.WalkCell), r1 = (int)Math.Floor((q.My + reach - d.WalkOriginY) / d.WalkCell);
        var lines = new List<(double, double, double, double, Color)>();
        for (int j = Math.Max(0, r0); j <= Math.Min(d.WalkH - 1, r1); j++)
            for (int i = Math.Max(0, c0); i <= Math.Min(d.WalkW - 1, c1); i++)
            {
                var o = d.GetWalkOverride(i, j);
                bool ok = d.WalkCellOk(i, j);
                if (ok && o == WalkOverride.Auto) continue;
                var col = !ok ? new Color(1, 0.25f, 0.2f, o == WalkOverride.Blocked ? 1f : 0.55f) : new Color(0.35f, 1, 0.35f, 0.9f);
                double mx0 = d.WalkOriginX + i * d.WalkCell + 0.3, my0 = d.WalkOriginY + j * d.WalkCell + 0.3, s = d.WalkCell - 0.6;
                var (x0, z0) = c.Source.ToGame(mx0, my0);
                lines.Add((x0, z0, x0 + s, z0 + s, col));
                lines.Add((x0 + s, z0, x0, z0 + s, col));
            }
        cur.Lines = lines;
        return cur;
    }
    public override string Status(ToolContext c) => $"walkability: {(c.Ctrl ? "auto" : NAMES[(int)PaintSettings.Walk].ToLowerInvariant())}, radius {EditorSession.Brush.Radius:0} m";
}

public sealed class ForestPaintTool : PaintBrushTool
{
    public override string Label => "Forest";
    public override string Tip => "Paint forest squares (32 m: the setting's trees and undergrowth); Ctrl: erase";
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt)
    {
        var fp = PaintSettings.Forest;
        bool erase = fp.Erase;
        fp.Erase = erase ^ c.Ctrl;
        var e = VegetationTools.PaintForest(c.Doc, EditorSession.Forest, EditorSession.Brush, p.Mx, p.My, fp);
        fp.Erase = erase;
        return e;
    }
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        var fp = PaintSettings.Forest;
        ui.Choice(new[] { "Plant", "Erase" }, fp.Erase ? 1 : 0, k => fp.Erase = k == 1, 2);
        ui.Slider("Radius", 1, 120, 1, () => EditorSession.Brush.Radius, v => EditorSession.Brush.Radius = v, v => $"{v:0} m");
        ui.Choice(new[] { "Circle", "Square" }, (int)EditorSession.Brush.Shape.Form, k => EditorSession.Brush.Shape = EditorSession.Brush.Shape with { Form = (BrushForm)k }, 2);
        ui.Slider("Density", 0.1, 1, 0.05, () => fp.Density, v => fp.Density = v, v => $"{v * 100:0} %");
        var kinds = EditorSession.Forest?.ForestKinds(c?.Doc.ForestSetting ?? "");
        if (kinds == null) ui.Note("No forest kinds for this setting in forest.json: the squares are painted, but nothing grows on them in this setting.");
        for (int k = 0; k < 5; k++)
        {
            int kk = k;
            string name = kinds != null && k < kinds.Trees.Count && kinds.Trees[k] is { } tk ? tk.Standard : $"kind {k + 1}";
            ui.Check(name, fp.Kinds[k], on => fp.Kinds[kk] = on, "Trees of this kind stand where the square's layout has them");
        }
        ui.Check("Undergrowth", fp.Undergrowth, on => fp.Undergrowth = on);
        ui.Note("A forest square is 32 m; the engine's layout puts its 15 trees and 16 undergrowth plants. Nothing grows at or below 16 m (under water). Ctrl erases.");
    }
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p)
    {
        var cur = base.Cursor(c, p);
        if (p is not { } q) return cur;
        // the forest squares near the cursor (with something standing) in green, the one under it in gold
        double C = Sim.Maps.Forest.CELL, reach = cur.Radius + C;
        var all = c.Doc.ForestSquares;
        var lines = new List<(double, double, double, double, Color)>();
        for (int y = Math.Max(all.J0, (int)Math.Floor((q.My - reach) / C)); y <= Math.Min(all.J1, (int)Math.Floor((q.My + reach) / C)); y++)
            for (int x = Math.Max(all.I0, (int)Math.Floor((q.Mx - reach) / C)); x <= Math.Min(all.I1, (int)Math.Floor((q.Mx + reach) / C)); x++)
            {
                bool here = x == (int)Math.Floor(q.Mx / C) && y == (int)Math.Floor(q.My / C);
                bool forest = c.Doc.ForestSquare(x, y) is { } b && b.Any(v => (v >> 5) is 2 or 3);
                if (!forest && !here) continue;
                var col = here ? new Color(1, 0.85f, 0.3f, 0.9f) : new Color(0.4f, 1, 0.4f, 0.6f);
                var (x0, z0) = c.Source.ToGame(x * C + 0.5, y * C + 0.5);
                double s = C - 1;
                lines.Add((x0, z0, x0 + s, z0, col)); lines.Add((x0 + s, z0, x0 + s, z0 + s, col)); lines.Add((x0 + s, z0 + s, x0, z0 + s, col)); lines.Add((x0, z0 + s, x0, z0, col));
            }
        cur.Lines = lines;
        cur.Other = PaintSettings.Forest.Erase ^ c.Ctrl;
        return cur;
    }
    public override string Status(ToolContext c) => $"forest: {(PaintSettings.Forest.Erase ^ c.Ctrl ? "erase" : $"plant {PaintSettings.Forest.Density * 100:0} %")}, radius {EditorSession.Brush.Radius:0} m";
}

public sealed class PlantPaintTool : PaintBrushTool
{
    // the plant models of a setting (by their names: the converted models of the installation)
    static readonly Regex PlantName = new("(bush|fern|flower|grass|reed|shrub|bamboo|underwood|seaweed|wildrice|plant|smallstones|mushroom|_deco$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Dictionary<string, string[]> PREFIX = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jungle"] = new[] { "jungle_" }, ["Savanna"] = new[] { "savanna_" }, ["Northland"] = new[] { "northland_" },
        ["Icewaste"] = new[] { "icewaste_" }, ["Ashvalley"] = new[] { "ash_", "ashvalley_" },
    };
    static int s_seed = 1;
    SimRandom _rng = new(1);

    public override string Label => "Plants";
    public override string Tip => "Scatter plants (bushes, ferns, flowers, reeds, small stones ...); Ctrl: erase";

    /// <summary>the plants the setting offers: the converted models of its plants, then those the map uses</summary>
    public static List<string> KindsOf(IEditorMapDoc d)
    {
        var res = new List<string>();
        var pre = PREFIX.TryGetValue(d.Setting, out var p) ? p : new[] { d.Setting.ToLowerInvariant() + "_" };
        foreach (var name in EditorSession.Models.Manifest.Models.Keys.OrderBy(n => n, StringComparer.Ordinal))
            if (pre.Any(x => name.StartsWith(x, StringComparison.Ordinal)) && PlantName.IsMatch(name) && !name.Contains("fruit")) res.Add(name);
        foreach (var q in d.Plants.Select(q => q.Name.ToLowerInvariant()).Distinct().OrderBy(n => n, StringComparer.Ordinal))
            if (!res.Contains(q) && EditorSession.Models.Has(q)) res.Add(q);
        return res;
    }

    public override void Begin(ToolContext c, ToolPoint p) => _rng = new SimRandom((ulong)s_seed++);
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt)
    {
        var ps = PaintSettings.Plants;
        bool erase = ps.Erase;
        ps.Erase = erase ^ c.Ctrl;
        var e = VegetationTools.ScatterPlants(c.Doc, EditorSession.Brush, p.Mx, p.My, dt, ps, _rng);
        ps.Erase = erase;
        return e;
    }

    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        var ps = PaintSettings.Plants;
        ui.Choice(new[] { "Scatter", "Erase" }, ps.Erase ? 1 : 0, k => ps.Erase = k == 1, 2);
        TerrainToolSet.BrushUi(ui, strength: false);
        ui.Slider("Density", 0.5, 40, 0.5, () => ps.Density, v => ps.Density = v, v => $"{v:0.#} / 100 m² / s");
        ui.Slider("Spacing", 0.5, 12, 0.5, () => ps.Spacing, v => ps.Spacing = v, v => $"{v:0.#} m");
        ui.Check("In the water too", ps.InWater, on => ps.InWater = on);
        ui.Check("Erase only the chosen kinds", ps.Only, on => ps.Only = on);
        if (c == null) return;
        var kinds = KindsOf(c.Doc);
        if (PaintSettings.PlantSetting != c.Doc.Setting)
        {
            // (a new setting: the bushes, ferns and shrubs chosen at first)
            PaintSettings.PlantSetting = c.Doc.Setting;
            ps.Kinds.Clear();
            ps.Kinds.AddRange(kinds.Where(k => Regex.IsMatch(k, "bush|fern|shrub|underwood")).Take(6));
            if (ps.Kinds.Count == 0) ps.Kinds.AddRange(kinds.Take(4));
        }
        var list = new ItemList { SelectMode = ItemList.SelectModeEnum.Multi, CustomMinimumSize = new Vector2(260, 190), FocusMode = Control.FocusModeEnum.None };
        foreach (var k in kinds) list.AddItem(k);
        for (int i = 0; i < kinds.Count; i++) if (ps.Kinds.Contains(kinds[i])) list.Select(i, false);
        list.MultiSelected += (_, _) =>
        {
            ps.Kinds.Clear();
            foreach (int i in list.GetSelectedItems()) ps.Kinds.Add(kinds[i]);
        };
        ui.Note(kinds.Count > 0 ? "The plants (Ctrl / Shift + click: several):" : "No plant models for this setting in the converted data.");
        ui.Box.AddChild(list);
        ui.Note("Each step tries points under the brush; a plant stands where no other is closer than the spacing. Ctrl erases.");
    }
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p) => base.Cursor(c, p) with { Other = PaintSettings.Plants.Erase ^ c.Ctrl };
    public override string Status(ToolContext c) => $"plants: {(PaintSettings.Plants.Erase ^ c.Ctrl ? "erase" : $"scatter {PaintSettings.Plants.Kinds.Count} kinds")}, radius {EditorSession.Brush.Radius:0} m · {c.Doc.Plants.Count} plants on the map";
}
