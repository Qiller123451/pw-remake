// The map editor's cursor on the ground: the 2 m height grid around the point under the mouse (its lines follow the
// ground, the sample under the cursor marked) and the brush's circle (the radius it changes). Redrawn every frame as
// lines (ImmediateMesh), drawn over the terrain.
using System;
using System.Collections.Generic;
using Godot;

namespace PwRemake.Editor;

public partial class EditorCursor : MeshInstance3D
{
    readonly ImmediateMesh _m = new();
    /// <summary>the ground's height at game (x, z)</summary>
    public Func<double, double, double> HeightAt = (x, z) => 0;
    /// <summary>the grid's cell (m) and the map's offset of sample 0 in game coordinates</summary>
    public double Cell = 2, GridX0, GridZ0;
    public Color GridColor = new(1f, 1f, 0.75f, 0.35f), BrushColor = new(1f, 0.85f, 0.3f, 0.95f), LowerColor = new(0.45f, 0.75f, 1f, 0.95f);

    public EditorCursor()
    {
        Name = "EditorCursor";
        Mesh = _m;
        CastShadow = ShadowCastingSetting.Off;
        MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 10,
        };
    }

    /// <summary>draws the cursor at game (x, z) with a brush of radius r (0: none) - a circle, or a square (phase 2's
    /// brush shapes) - and a tool's own lines on the ground (game x0, z0 -> x1, z1: a ramp, a rectangle, blocked cells);
    /// hidden when p is null (the tool's lines still drawn)</summary>
    public void Show(Vector3? p, double r, bool lower, bool square = false, IReadOnlyList<(double X0, double Z0, double X1, double Z1, Color C)>? lines = null)
    {
        _m.ClearSurfaces();
        if (p is null && (lines == null || lines.Count == 0)) return;
        _m.SurfaceBegin(Mesh.PrimitiveType.Lines);
        Vector3 At(double x, double z) => new((float)x, (float)(HeightAt(x, z) + 0.15), (float)z);
        void Line(Vector3 a, Vector3 b, Color col) { _m.SurfaceSetColor(col); _m.SurfaceAddVertex(a); _m.SurfaceSetColor(col); _m.SurfaceAddVertex(b); }
        if (lines != null)
            foreach (var (x0, z0, x1, z1, lc) in lines)
            {
                // (along the ground in 1 m pieces)
                double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                int pieces = Math.Clamp((int)Math.Ceiling(len), 1, 400);
                for (int k = 0; k < pieces; k++)
                {
                    double a = (double)k / pieces, b = (double)(k + 1) / pieces;
                    Line(At(x0 + (x1 - x0) * a, z0 + (z1 - z0) * a), At(x0 + (x1 - x0) * b, z0 + (z1 - z0) * b), lc);
                }
            }
        if (p is not { } c) { _m.SurfaceEnd(); return; }
        // the grid: 8 cells to each side of the sample nearest the cursor, each line in 1 m pieces along the ground
        int n = 8;
        double ci = Math.Round((c.X - GridX0) / Cell), cj = Math.Round((c.Z - GridZ0) / Cell);
        double gx = GridX0 + ci * Cell, gz = GridZ0 + cj * Cell, ext = n * Cell;
        for (int k = -n; k <= n; k++)
        {
            float a = (float)(GridColor.A * (1 - Math.Abs(k) / (double)(n + 1)));
            var col = new Color(GridColor, a);
            for (double t = -ext; t < ext; t += Cell / 2)
            {
                double fade = 1 - Math.Abs(t + Cell / 4) / ext;
                var cc = new Color(col, (float)(a * fade));
                Line(At(gx + t, gz + k * Cell), At(gx + t + Cell / 2, gz + k * Cell), cc);
                Line(At(gx + k * Cell, gz + t), At(gx + k * Cell, gz + t + Cell / 2), cc);
            }
        }
        // the sample under the cursor: a small cross
        var hc = new Color(1, 1, 1, 0.9f);
        Line(At(gx - 0.6, gz), At(gx + 0.6, gz), hc);
        Line(At(gx, gz - 0.6), At(gx, gz + 0.6), hc);
        // the brush's circle (and a fainter one at half the radius: the falloff's middle)
        if (r > 0 && square)
        {
            var bc = lower ? LowerColor : BrushColor;
            for (int ring = 0; ring < 2; ring++)
            {
                double rr = ring == 0 ? r : r / 2;
                var col = ring == 0 ? bc : new Color(bc, bc.A * 0.4f);
                int seg = Math.Clamp((int)(rr * 2), 4, 120);
                (double, double)[] cs = { (-rr, -rr), (rr, -rr), (rr, rr), (-rr, rr), (-rr, -rr) };
                for (int e = 0; e < 4; e++)
                    for (int s = 0; s < seg; s++)
                    {
                        double a = (double)s / seg, b2 = (double)(s + 1) / seg;
                        var (ax, az) = cs[e]; var (bx, bz) = cs[e + 1];
                        Line(At(c.X + ax + (bx - ax) * a, c.Z + az + (bz - az) * a), At(c.X + ax + (bx - ax) * b2, c.Z + az + (bz - az) * b2), col);
                    }
            }
        }
        else if (r > 0)
        {
            var bc = lower ? LowerColor : BrushColor;
            int seg = Math.Clamp((int)(r * 2), 32, 160);
            for (int ring = 0; ring < 2; ring++)
            {
                double rr = ring == 0 ? r : r / 2;
                var col = ring == 0 ? bc : new Color(bc, bc.A * 0.4f);
                for (int s = 0; s < seg; s++)
                {
                    double a0 = 2 * Math.PI * s / seg, a1 = 2 * Math.PI * (s + 1) / seg;
                    Line(At(c.X + rr * Math.Cos(a0), c.Z + rr * Math.Sin(a0)), At(c.X + rr * Math.Cos(a1), c.Z + rr * Math.Sin(a1)), col);
                }
            }
        }
        _m.SurfaceEnd();
    }
}
