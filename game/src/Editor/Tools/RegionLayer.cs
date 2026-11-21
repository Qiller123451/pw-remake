// The map editor's drawing of the mission data on the map (docs/MAP_EDITOR.md "Regions and mission data"): every
// region's shapes as outlines on the ground (rectangles, ovals; a shape switched off at the start dashed) with the
// region's name over it, the chosen region bright with the handles of its chosen shape; the question marks ("?"), the
// camera points (an arrow along their view), the start camera, and the members of the chosen group ringed. Shown while
// the Regions or Triggers group is open (or "always" in the region tool). Drawn anew when the mission data, the
// selection or the ground changes.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Maps;
using PwRemake.View.World;

namespace PwRemake.Editor;

public partial class RegionLayer : Node3D
{
    readonly WorldView _view;
    readonly MapSource _src;
    readonly IEditorMapDoc _doc;
    readonly ImmediateMesh _mesh = new();
    readonly Node3D _labels = new() { Name = "Labels" };
    readonly List<Label3D> _pool = new();
    bool _dirty = true;
    /// <summary>the handles' size (m), the region tool's grab distance</summary>
    public const double Handle = 2.5;

    static readonly Color[] Palette =
    {
        new(1f, 0.55f, 0.25f), new(0.35f, 0.8f, 1f), new(0.6f, 1f, 0.45f), new(1f, 0.45f, 0.75f), new(0.85f, 0.75f, 1f), new(1f, 0.9f, 0.4f),
    };

    public RegionLayer(WorldView view, MapSource src, IEditorMapDoc doc)
    {
        Name = "EditorRegions";
        _view = view; _src = src; _doc = doc;
        var mi = new MeshInstance3D
        {
            Mesh = _mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 9,
            },
        };
        AddChild(mi);
        AddChild(_labels);
        doc.Mission.Changed += OnMission;
        doc.Changed += OnDoc;
    }

    public override void _ExitTree()
    {
        _doc.Mission.Changed -= OnMission;
        _doc.Changed -= OnDoc;
    }

    void OnMission(MissionPart p) => _dirty = true;
    void OnDoc(DocChange c) { if (c.Kind is DocChangeKind.Heights or DocChangeKind.Objects or DocChangeKind.All) _dirty = true; }
    public void Invalidate() => _dirty = true;

    /// <summary>shown or hidden (the open tool group); drawn anew when something changed</summary>
    public void Update(bool show)
    {
        Visible = show;
        if (!show || !_dirty) return;
        _dirty = false;
        Draw();
    }

    Vector3 At(double mx, double my, double lift = 0.35)
    {
        var (x, z) = _src.ToGame(mx, my);
        return new Vector3((float)x, (float)(_view.Ground.At(x, z) + lift), (float)z);
    }

    void Line(Vector3 a, Vector3 b, Color c) { _mesh.SurfaceSetColor(c); _mesh.SurfaceAddVertex(a); _mesh.SurfaceSetColor(c); _mesh.SurfaceAddVertex(b); }

    /// <summary>a line along the ground from map (x0, y0) to (x1, y1), in pieces of about 2 m; dashed: every other one</summary>
    void GroundLine(double x0, double y0, double x1, double y1, Color c, bool dashed)
    {
        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        int n = Math.Clamp((int)(len / 2), 1, 400);
        for (int k = 0; k < n; k++)
        {
            if (dashed && k % 2 == 1) continue;
            double t0 = k / (double)n, t1 = (k + 1) / (double)n;
            Line(At(x0 + (x1 - x0) * t0, y0 + (y1 - y0) * t0), At(x0 + (x1 - x0) * t1, y0 + (y1 - y0) * t1), c);
        }
    }

    public void Shape(EdShape s, Color c, bool dashed = false)
    {
        if (!s.Oval)
        {
            GroundLine(s.X, s.Y, s.X + s.W, s.Y, c, dashed); GroundLine(s.X + s.W, s.Y, s.X + s.W, s.Y + s.H, c, dashed);
            GroundLine(s.X + s.W, s.Y + s.H, s.X, s.Y + s.H, c, dashed); GroundLine(s.X, s.Y + s.H, s.X, s.Y, c, dashed);
            return;
        }
        double cx = s.X + s.W / 2, cy = s.Y + s.H / 2, rx = s.W / 2, ry = s.H / 2;
        int seg = Math.Clamp((int)((rx + ry) * 1.5), 24, 160);
        for (int k = 0; k < seg; k++)
        {
            if (dashed && k % 2 == 1) continue;
            double a0 = 2 * Math.PI * k / seg, a1 = 2 * Math.PI * (k + 1) / seg;
            Line(At(cx + rx * Math.Cos(a0), cy + ry * Math.Sin(a0)), At(cx + rx * Math.Cos(a1), cy + ry * Math.Sin(a1)), c);
        }
    }

    void Square(double mx, double my, double r, Color c)
    {
        GroundLine(mx - r, my - r, mx + r, my - r, c, false); GroundLine(mx + r, my - r, mx + r, my + r, c, false);
        GroundLine(mx + r, my + r, mx - r, my + r, c, false); GroundLine(mx - r, my + r, mx - r, my - r, c, false);
    }

    void Ring(double mx, double my, double r, Color c)
    {
        for (int k = 0; k < 20; k++)
        {
            double a0 = 2 * Math.PI * k / 20, a1 = 2 * Math.PI * (k + 1) / 20;
            Line(At(mx + r * Math.Cos(a0), my + r * Math.Sin(a0)), At(mx + r * Math.Cos(a1), my + r * Math.Sin(a1)), c);
        }
    }

    static Color ColorOf(EdRegion r, int i)
    {
        // (the original's colour "aarrggbb"; white = none chosen: the palette's)
        var s = r.Extra?["color"].Str() ?? "";
        if (s.Length == 8 && s != "ffffffff" && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1);
        return Palette[i % Palette.Length];
    }

    int _used;
    Label3D Label(string text, Vector3 at, Color c, int size = 26)
    {
        if (_used >= _pool.Count)
        {
            var l = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, PixelSize = 0.0011f, NoDepthTest = true, FontSize = size, OutlineSize = 8,
                OutlineModulate = new Color(0, 0, 0, 0.85f), RenderPriority = 11, OutlineRenderPriority = 10,
            };
            _pool.Add(l); _labels.AddChild(l);
        }
        var lb = _pool[_used++];
        lb.Visible = true; lb.Text = text; lb.Position = at; lb.Modulate = c; lb.FontSize = size;
        return lb;
    }

    void Draw()
    {
        _mesh.ClearSurfaces();
        _used = 0;
        var m = _doc.Mission;
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        bool any = false;
        int sel = RegionState.Region, selShape = RegionState.Shape;
        for (int i = 0; i < m.Regions.Count; i++)
        {
            var r = m.Regions[i];
            bool chosen = i == sel;
            var c = chosen ? new Color(1f, 0.95f, 0.3f) : ColorOf(r, i);
            for (int k = 0; k < r.Shapes.Count; k++)
            {
                var s = r.Shapes[k];
                Shape(s, chosen && k == selShape ? new Color(1, 1, 1) : new Color(c, s.Enabled ? 0.95f : 0.6f), !s.Enabled);
                any = true;
                if (chosen && k == selShape)
                    foreach (var (hx, hy) in Handles(s)) Square(hx, hy, Handle / 2, new Color(1, 1, 1));
            }
            if (r.Box() is { } b) Label(r.Name, At((b[0] + b[2]) / 2, (b[1] + b[3]) / 2, 2.5), chosen ? new Color(1, 0.95f, 0.4f) : new Color(c, 1), chosen ? 30 : 24);
        }
        // the question marks, camera points, start camera, the chosen group's members
        foreach (var q in m.QuestionMarks) { Ring(q.X, q.Y, 1.5, new Color(1, 0.85f, 0.2f)); Label("?", At(q.X, q.Y, 4), new Color(1, 0.85f, 0.2f), 40); Label(q.Name, At(q.X, q.Y, 1.5), new Color(1, 0.85f, 0.2f), 18); any = true; }
        foreach (var cam in m.Cameras.Append(m.StartX is double sx && m.StartY is double sy ? new EdCamera { Name = "start camera", X = sx, Y = sy, Angle = m.StartAngle } : null))
        {
            if (cam == null) continue;
            // (the view's direction: yaw = pi/2 - angle in game space, docs: MissionSession.StartCamera)
            double yaw = Math.PI / 2 - cam.Angle, dx = -Math.Sin(yaw), dy = -Math.Cos(yaw);
            var col = cam.Name == "start camera" ? new Color(0.4f, 1f, 0.5f) : new Color(0.5f, 0.8f, 1f);
            Ring(cam.X, cam.Y, 2, col);
            GroundLine(cam.X, cam.Y, cam.X + dx * 12, cam.Y + dy * 12, col, false);
            GroundLine(cam.X + dx * 12, cam.Y + dy * 12, cam.X + dx * 9 - dy * 2, cam.Y + dy * 9 + dx * 2, col, false);
            GroundLine(cam.X + dx * 12, cam.Y + dy * 12, cam.X + dx * 9 + dy * 2, cam.Y + dy * 9 - dx * 2, col, false);
            Label(cam.Name, At(cam.X, cam.Y, 3), col, 20);
            any = true;
        }
        if (RegionState.Group >= 0 && RegionState.Group < m.Groups.Count)
        {
            var g = m.Groups[RegionState.Group];
            var objs = m.Objects();
            var members = new HashSet<string>(g.Members, StringComparer.Ordinal);
            foreach (var o in objs) if (members.Contains(o.Guid)) { Ring(o.X, o.Y, 2.2, new Color(0.3f, 1f, 0.9f)); any = true; }
        }
        if (!any) Line(Vector3.Zero, Vector3.Zero, new Color(0, 0, 0, 0));       // (an empty surface is not allowed)
        _mesh.SurfaceEnd();
        for (int k = _used; k < _pool.Count; k++) _pool[k].Visible = false;
    }

    /// <summary>a shape's eight handles (corners and edge middles), in the order RegionTool resizes them</summary>
    public static IEnumerable<(double X, double Y)> Handles(EdShape s)
    {
        double x0 = s.X, y0 = s.Y, x1 = s.X + s.W, y1 = s.Y + s.H, xm = (x0 + x1) / 2, ym = (y0 + y1) / 2;
        yield return (x0, y0); yield return (xm, y0); yield return (x1, y0); yield return (x1, ym);
        yield return (x1, y1); yield return (xm, y1); yield return (x0, y1); yield return (x0, ym);
    }
}
