// The map editor's Terrain tools (docs/MAP_EDITOR.md "Terrain tools"; the maths: src/Sim/Editor/HeightBrush.cs and
// Terrain/HeightTools.cs): raise / lower, smooth, flatten, plateau, roughen, ramp, level area - and the water level.
// The brush tools share one brush (EditorSession.Brush: radius, strength, circle / square, falloff curve; [ ] and - =
// change radius and strength); each held stroke is one undo step.
//
//   Raise / lower   left: raise (or lower, as set), Ctrl: the other way
//   Smooth          left: smooth
//   Flatten         left: towards the height under the cursor where the stroke began (or the typed height); Ctrl+click
//                   picks the height under the cursor into the field
//   Plateau         left: a flat top (the ground under the click + the offset, or the typed height) with sides at the
//                   slope; dragging stamps along the way at the stroke's height; Ctrl+click picks the height
//   Roughen         left: noise of the set size
//   Ramp            click the start, click the end: a straight ramp between the two ground heights (width, soft border);
//                   Ctrl+click or another tool forgets the start
//   Level area      drag a rectangle: every height sample inside set to the typed height (or the corner's where the drag
//                   began)
//   Water           the map's water level: a number and a slider (the view follows at once), "No water"; a click on the
//                   map sets the level to the ground's height there
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.Sim.Editor;

namespace PwRemake.Editor;

/// <summary>the terrain tools' own settings (kept while the editor is away, as the brush)</summary>
public static class TerrainSettings
{
    /// <summary>flatten / plateau / level to the typed height instead of the ground's under the click</summary>
    public static bool Typed;
    public static double Height = 30;
    /// <summary>plateau: above the ground under the click (m)</summary>
    public static double PlateauOffset = 8;
    public static double Slope = 40;
    /// <summary>roughen: the size of its bumps (m)</summary>
    public static double NoiseSize = 12;
    public static double RampWidth = 12, RampBorder = 6;
}

public static class TerrainToolSet
{
    public static IEnumerable<EditorTool> Create() => new EditorTool[]
    {
        new RaiseTool(), new SmoothTool(), new FlattenTool(), new PlateauTool(), new RoughenTool(), new RampTool(), new LevelTool(), new WaterTool(),
        // the Paint group (PaintTools.cs)
        new GroundPaintTool(), new BiomePaintTool(), new WalkPaintTool(), new ForestPaintTool(), new PlantPaintTool(),
    };

    static readonly string[] FORMS = { "Circle", "Square" }, CURVES = { "Smooth", "Linear", "Dome", "Hard" };

    /// <summary>the shared brush's controls: radius, strength (its unit), shape, falloff</summary>
    public static void BrushUi(ToolUi ui, string strengthUnit = "m/s", bool strength = true, bool curve = true)
    {
        var b = EditorSession.Brush;
        ui.Slider("Radius", 1, 120, 1, () => b.Radius, v => b.Radius = v, v => $"{v:0} m");
        if (strength) ui.Slider("Strength", 0.5, 40, 0.5, () => b.Strength, v => b.Strength = v, v => $"{v:0.#} {strengthUnit}");
        ui.Choice(FORMS, (int)b.Shape.Form, k => b.Shape = b.Shape with { Form = (BrushForm)k }, 2);
        if (curve) ui.Choice(CURVES, (int)b.Shape.Curve, k => b.Shape = b.Shape with { Curve = (BrushCurve)k }, 4,
            new[] { "Soft: (1 - t²)²", "Linear: 1 - t", "Dome: full most of the way", "Hard: the whole brush at full strength" });
    }

    /// <summary>"height under the cursor / typed height" and the height field</summary>
    public static void HeightUi(ToolUi ui, string under)
    {
        ui.Choice(new[] { under, "Typed height" }, TerrainSettings.Typed ? 1 : 0, k => TerrainSettings.Typed = k == 1, 1);
        ui.Number("Height", HeightBrush.MinHeight, HeightBrush.MaxHeight, 0.1, TerrainSettings.Height, v => TerrainSettings.Height = v, " m");
    }
}

/// <summary>a held brush: a step every frame at the cursor</summary>
public abstract class BrushTool : EditorTool
{
    public override string Group => "Terrain";
    public override void Step(ToolContext c, ToolPoint? p, double dt) { if (p is { } q) c.Run(Make(c, q, dt)); }
    protected abstract IEditCommand? Make(ToolContext c, ToolPoint p, double dt);
    public override string Status(ToolContext c) { var b = EditorSession.Brush; return $"{Label}: radius {b.Radius:0} m, strength {b.Strength:0.#}"; }
}

public sealed class RaiseTool : BrushTool
{
    public override string Label => "Raise / lower";
    public override string Tip => "Left mouse: raise (or lower, as set); Ctrl + left mouse: the other way";
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt)
    {
        var b = EditorSession.Brush;
        bool lower = b.Lower;
        b.Lower ^= c.Ctrl;
        var e = b.Step(c.Doc, p.Mx, p.My, dt);
        b.Lower = lower;
        return e;
    }
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        TerrainToolSet.BrushUi(ui);
        var b = EditorSession.Brush;
        ui.Choice(new[] { "Raise", "Lower" }, b.Lower ? 1 : 0, k => b.Lower = k == 1, 2);
        ui.Note("Left mouse paints, Ctrl the other way. [ ] radius, - = strength.");
    }
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p) => base.Cursor(c, p) with { Other = EditorSession.Brush.Lower ^ c.Ctrl };
    public override string Status(ToolContext c) { var b = EditorSession.Brush; return $"{(b.Lower ^ c.Ctrl ? "lower" : "raise")}: radius {b.Radius:0} m, {b.Strength:0.#} m/s"; }
}

public sealed class SmoothTool : BrushTool
{
    public override string Label => "Smooth";
    public override string Tip => "Left mouse: even out the ground";
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt) => HeightTools.Smooth(c.Doc, EditorSession.Brush, p.Mx, p.My, dt);
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        TerrainToolSet.BrushUi(ui, "/s");
        ui.Note("Each sample moves towards its neighbours' mean; strength 4: 63 % of the way per second at the centre.");
    }
}

public sealed class FlattenTool : BrushTool
{
    double _target;
    public override string Label => "Flatten";
    public override string Tip => "Left mouse: towards the height where the stroke began (or the typed one); Ctrl + click: pick the height";
    public override void Begin(ToolContext c, ToolPoint p)
    {
        if (c.Ctrl) { TerrainSettings.Height = Math.Round(p.Y, 1); c.Note($"Height {TerrainSettings.Height:0.0} m picked"); c.Rebuild(); _target = double.NaN; return; }
        _target = TerrainSettings.Typed ? TerrainSettings.Height : p.Y;
    }
    public override void Step(ToolContext c, ToolPoint? p, double dt) { if (!double.IsNaN(_target)) base.Step(c, p, dt); }
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt) => HeightTools.Flatten(c.Doc, EditorSession.Brush, p.Mx, p.My, dt, _target);
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        TerrainToolSet.BrushUi(ui, "/s");
        TerrainToolSet.HeightUi(ui, "Height where the stroke begins");
        ui.Note("Ctrl + click picks the height under the cursor.");
    }
    public override string Status(ToolContext c) => $"flatten to {(TerrainSettings.Typed ? $"{TerrainSettings.Height:0.0} m" : "the stroke's start")}, radius {EditorSession.Brush.Radius:0} m";
}

public sealed class PlateauTool : BrushTool
{
    double _top = double.NaN;
    public override string Label => "Plateau";
    public override string Tip => "Left mouse: a flat top with sloped sides; drag to stamp along the way; Ctrl + click: pick the height";
    public override void Begin(ToolContext c, ToolPoint p)
    {
        if (c.Ctrl) { TerrainSettings.Height = Math.Round(p.Y, 1); c.Note($"Height {TerrainSettings.Height:0.0} m picked"); c.Rebuild(); _top = double.NaN; return; }
        _top = TerrainSettings.Typed ? TerrainSettings.Height : p.Y + TerrainSettings.PlateauOffset;
    }
    public override void Step(ToolContext c, ToolPoint? p, double dt) { if (!double.IsNaN(_top)) base.Step(c, p, dt); }
    public override void End(ToolContext c, ToolPoint? p) => _top = double.NaN;
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt) => HeightTools.Plateau(c.Doc, EditorSession.Brush, p.Mx, p.My, _top, TerrainSettings.Slope);
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        TerrainToolSet.BrushUi(ui, strength: false, curve: false);
        ui.Choice(new[] { "Ground under the click + offset", "Typed height" }, TerrainSettings.Typed ? 1 : 0, k => TerrainSettings.Typed = k == 1, 1);
        ui.Slider("Offset", -40, 40, 0.5, () => TerrainSettings.PlateauOffset, v => TerrainSettings.PlateauOffset = v, v => $"{v:+0.#;-0.#;0} m");
        ui.Number("Height", HeightBrush.MinHeight, HeightBrush.MaxHeight, 0.1, TerrainSettings.Height, v => TerrainSettings.Height = v, " m");
        ui.Slider("Slope", 10, 80, 1, () => TerrainSettings.Slope, v => TerrainSettings.Slope = v, v => $"{v:0}°");
        ui.Note($"The radius is the flat top; the sides fall at the slope until they meet the ground (at most {HeightTools.PlateauReach:0} m out). Ctrl + click picks the height.");
    }
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p)
    {
        var cur = base.Cursor(c, p);
        // (the sides' reach for the height difference to the ground under the cursor: a second outline)
        if (p is { } q)
        {
            double top = !double.IsNaN(_top) ? _top : TerrainSettings.Typed ? TerrainSettings.Height : q.Y + TerrainSettings.PlateauOffset;
            double reach = Math.Min(HeightTools.PlateauReach, Math.Abs(top - q.Y) / Math.Tan(TerrainSettings.Slope * Math.PI / 180));
            if (reach > 0.5) cur.Lines = Outline(q.X, q.Z, cur.Radius + reach, cur.Square, new Color(1, 0.85f, 0.3f, 0.45f));
        }
        return cur;
    }
    public override string Status(ToolContext c) => $"plateau: top {(TerrainSettings.Typed ? $"{TerrainSettings.Height:0.0} m" : $"ground {TerrainSettings.PlateauOffset:+0.#;-0.#;0} m")}, radius {EditorSession.Brush.Radius:0} m, slope {TerrainSettings.Slope:0}°";

    /// <summary>a circle or square outline as ground lines</summary>
    public static List<(double, double, double, double, Color)> Outline(double x, double z, double r, bool square, Color col)
    {
        var l = new List<(double, double, double, double, Color)>();
        if (square) { l.Add((x - r, z - r, x + r, z - r, col)); l.Add((x + r, z - r, x + r, z + r, col)); l.Add((x + r, z + r, x - r, z + r, col)); l.Add((x - r, z + r, x - r, z - r, col)); return l; }
        int seg = Math.Clamp((int)(r * 1.5), 24, 160);
        for (int s = 0; s < seg; s++)
        {
            double a0 = 2 * Math.PI * s / seg, a1 = 2 * Math.PI * (s + 1) / seg;
            l.Add((x + r * Math.Cos(a0), z + r * Math.Sin(a0), x + r * Math.Cos(a1), z + r * Math.Sin(a1), col));
        }
        return l;
    }
}

public sealed class RoughenTool : BrushTool
{
    public override string Label => "Roughen";
    public override string Tip => "Left mouse: add bumps (noise)";
    protected override IEditCommand? Make(ToolContext c, ToolPoint p, double dt) => HeightTools.Roughen(c.Doc, EditorSession.Brush, p.Mx, p.My, dt, TerrainSettings.NoiseSize);
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        TerrainToolSet.BrushUi(ui);
        ui.Slider("Bump size", 3, 60, 1, () => TerrainSettings.NoiseSize, v => TerrainSettings.NoiseSize = v, v => $"{v:0} m");
        ui.Note("The same place always gets the same pattern: brushing on makes it stronger.");
    }
}

public sealed class RampTool : EditorTool
{
    ToolPoint? _a;
    public override string Group => "Terrain";
    public override string Label => "Ramp";
    public override string Tip => "Click the start, click the end: a straight ramp between their heights; Ctrl + click forgets the start";
    public override void Reset() => _a = null;
    public override void Begin(ToolContext c, ToolPoint p)
    {
        if (c.Ctrl) { _a = null; c.Note("Ramp: start forgotten"); return; }
        if (_a is not { } a) { _a = p; c.Note($"Ramp from ({p.Mx:0}, {p.My:0}) at {p.Y:0.0} m - click the end"); return; }
        var e = HeightTools.Ramp(c.Doc, a.Mx, a.My, a.Y, p.Mx, p.My, p.Y, TerrainSettings.RampWidth, TerrainSettings.RampBorder);
        c.Run(e, merge: false);
        c.Note(e != null ? $"Ramp {a.Y:0.0} m -> {p.Y:0.0} m" : "Ramp: nothing to change");
        _a = null;
    }
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        ui.Slider("Width", 2, 60, 1, () => TerrainSettings.RampWidth, v => TerrainSettings.RampWidth = v, v => $"{v:0} m");
        ui.Slider("Border", 0, 40, 1, () => TerrainSettings.RampBorder, v => TerrainSettings.RampBorder = v, v => $"{v:0} m");
        ui.Note("Click the start, then the end: the ground between them becomes a straight ramp from the start's height to the end's; the border blends into the ground. Ctrl + click forgets the start.");
    }
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p)
    {
        var cur = new ToolCursor { Radius = 0 };
        if (_a is { } a && p is { } q)
        {
            double dx = q.X - a.X, dz = q.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
            if (len > 0.1)
            {
                double nx = -dz / len * TerrainSettings.RampWidth / 2, nz = dx / len * TerrainSettings.RampWidth / 2;
                var col = new Color(1, 0.85f, 0.3f, 0.95f);
                cur.Lines = new() { (a.X + nx, a.Z + nz, q.X + nx, q.Z + nz, col), (a.X - nx, a.Z - nz, q.X - nx, q.Z - nz, col), (a.X + nx, a.Z + nz, a.X - nx, a.Z - nz, col), (q.X + nx, q.Z + nz, q.X - nx, q.Z - nz, col) };
            }
        }
        return cur;
    }
    public override string Status(ToolContext c) => _a is { } a ? $"ramp from {a.Y:0.0} m: click the end" : "ramp: click the start";
}

public sealed class LevelTool : EditorTool
{
    ToolPoint? _a, _b;
    public override string Group => "Terrain";
    public override string Label => "Level area";
    public override string Tip => "Drag a rectangle: the ground inside at one height";
    public override void Reset() { _a = _b = null; }
    public override void Begin(ToolContext c, ToolPoint p) { _a = _b = p; }
    public override void Step(ToolContext c, ToolPoint? p, double dt) { if (p != null) _b = p; }
    public override void End(ToolContext c, ToolPoint? p)
    {
        if (p != null) _b = p;
        if (_a is { } a && _b is { } b)
        {
            double h = TerrainSettings.Typed ? TerrainSettings.Height : a.Y;
            var e = HeightTools.Level(c.Doc, a.Mx, a.My, b.Mx, b.My, h);
            c.Run(e, merge: false);
            c.Note(e != null ? $"Levelled {Math.Abs(b.Mx - a.Mx):0} x {Math.Abs(b.My - a.My):0} m at {h:0.0} m" : "Level: nothing to change");
        }
        _a = _b = null;
    }
    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        TerrainToolSet.HeightUi(ui, "Height where the drag begins");
        ui.Note("Drag a rectangle on the map: every height sample inside is set to the height.");
    }
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p)
    {
        var cur = new ToolCursor();
        if (_a is { } a && _b is { } b)
        {
            var col = new Color(1, 0.85f, 0.3f, 0.95f);
            cur.Lines = new() { (a.X, a.Z, b.X, a.Z, col), (b.X, a.Z, b.X, b.Z, col), (b.X, b.Z, a.X, b.Z, col), (a.X, b.Z, a.X, a.Z, col) };
        }
        return cur;
    }
    public override string Status(ToolContext c) => _a is { } a && _b is { } b ? $"level {Math.Abs(b.Mx - a.Mx):0} x {Math.Abs(b.My - a.My):0} m" : "level: drag a rectangle";
}

public sealed class WaterTool : EditorTool
{
    public override string Group => "Terrain";
    public override string Label => "Water";
    public override string Tip => "The map's water level; a click on the map sets it to the ground's height there";
    SpinBox? _num;
    HSlider? _slider;
    CheckBox? _on;

    public override void Begin(ToolContext c, ToolPoint p)
    {
        c.Run(PaintTools.Water(c.Doc, Math.Round(p.Y, 1)), merge: false);
        c.Note($"Water at {p.Y:0.0} m");
        Sync(c.Doc);
    }

    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        ui.Caption(Label);
        var d = c?.Doc;
        double level = d?.Water ?? 16;
        _on = ui.Check("Water", d?.Water != null, on => { if (c != null) { c.Run(PaintTools.Water(c.Doc, on ? (_num?.Value ?? 16) : null), merge: false); Sync(c.Doc); } });
        _num = ui.Number("Level", HeightBrush.MinHeight, 200, 0.1, level, v => { if (c != null && c.Doc.Water is double w && w != v) { c.Run(PaintTools.Water(c.Doc, v), merge: false); Sync(c.Doc); } }, " m");
        _slider = null;          // (the slider of an earlier panel - a screen freed by Test map - is gone)
        _slider = ui.Slider("Drag", 0, 100, 0.1, () => _slider?.Value ?? Math.Min(100, level), v =>
        {
            if (c == null || c.Doc.Water == null) return;
            c.Run(PaintTools.Water(c.Doc, Math.Round(v, 1)), merge: true);          // (a drag: one undo step)
            _num?.SetValueNoSignal(Math.Round(v, 1));
        }, v => $"{v:0.0} m");
        _slider.DragEnded += _ => c?.History.EndMerge();
        ui.Note("Most original maps have their water at 16 m. Units cannot walk below the level (the walk grid follows); forests do not grow at or below 16 m. Click on the map: the water at the ground's height there.");
    }

    void Sync(IEditorMapDoc d)
    {
        _on?.SetPressedNoSignal(d.Water != null);
        if (d.Water is double w) { _num?.SetValueNoSignal(w); _slider?.SetValueNoSignal(Math.Min(100, w)); }
    }

    public override ToolCursor Cursor(ToolContext c, ToolPoint? p) => new() { Radius = 0 };
    public override string Status(ToolContext c) => $"water: {(c.Doc.Water is double w ? $"{w:0.0} m" : "none")} - click: the level at the ground there";
}
