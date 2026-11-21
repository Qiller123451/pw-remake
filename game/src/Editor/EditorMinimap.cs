// The map editor's minimap: the whole map from above - the ground shaded by its slopes in the setting's colour, the
// water blue (darker where deeper), the start locations - with the camera's view (the point looked at and the
// direction). It follows the edits (redrawn at most five times a second while the document changes). A click or a
// drag moves the camera there. The map turns with the camera like the game's minimap with "Rotate Minimap"
// (MinimapModel.Angle = the camera's yaw): what is ahead in the view is up on the map. The control is as wide as the
// picture's diagonal, so the turned map is never cut off.
using System;
using Godot;
using PwRemake.Sim.Editor;

namespace PwRemake.Editor;

public partial class EditorMinimap : Control
{
    /// <summary>the picture (N x N texels, the longer side of the map) and the control (its diagonal)</summary>
    const int N = 192, SIDE = 272;
    IEditorMapDoc? _doc;
    Image _img = Image.CreateEmpty(N, N, false, Image.Format.Rgb8);
    ImageTexture? _tex;
    bool _dirty = true, _drag;
    double _wait;
    /// <summary>the camera: the point looked at (game x, z) and the yaw; game = map - origin</summary>
    public Func<(double X, double Z, double Yaw)>? CameraPose;
    /// <summary>a click: look at game (x, z)</summary>
    public Action<double, double>? LookAt;
    /// <summary>map -> game offset (MapSource.Origin)</summary>
    public double Ox, Oy;
    /// <summary>start locations in game coordinates</summary>
    public System.Collections.Generic.List<(double X, double Z)> Starts = new();

    public EditorMinimap()
    {
        CustomMinimumSize = new Vector2(SIDE, SIDE);
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "The map: click or drag to move the camera there";
    }

    public void SetDoc(IEditorMapDoc doc) { _doc = doc; _dirty = true; _wait = 0; }
    public void Invalidate() => _dirty = true;

    static Color Base(string setting) => setting switch
    {
        "Savanna" => new Color(0.62f, 0.55f, 0.32f), "Icewaste" => new Color(0.82f, 0.85f, 0.9f), "Northland" => new Color(0.42f, 0.5f, 0.3f),
        "Ashvalley" => new Color(0.4f, 0.36f, 0.33f), "Cave1" or "Cave2" or "Cave3" => new Color(0.38f, 0.33f, 0.28f), _ => new Color(0.3f, 0.48f, 0.22f),
    };

    void Redraw()
    {
        var d = _doc;
        if (d == null) return;
        double w = d.HeightsW * d.Cell, h = d.HeightsH * d.Cell, s = Math.Max(w, h) / N;
        var bc = Base(d.Setting);
        double? water = d.Water;
        for (int py = 0; py < N; py++)
            for (int px = 0; px < N; px++)
            {
                double mx = d.OriginX + (px + 0.5) * s, my = d.OriginY + (py + 0.5) * s;
                if (mx > d.OriginX + w || my > d.OriginY + h) { _img.SetPixel(px, py, Colors.Black); continue; }
                double z = d.HeightAt(mx, my);
                // light from the north-west: the slope towards it brightens
                double sl = (d.HeightAt(mx - s, my - s) - d.HeightAt(mx + s, my + s)) / (2 * s);
                float k = (float)Math.Clamp(0.85 + sl * 1.6 + (z - 20) * 0.004, 0.35, 1.35);
                var c = new Color(bc.R * k, bc.G * k, bc.B * k);
                if (water is double wl && z < wl)
                {
                    float dep = (float)Math.Clamp((wl - z) / 8, 0, 1);
                    c = new Color(0.16f, 0.32f, 0.5f).Lerp(new Color(0.06f, 0.14f, 0.3f), dep).Lerp(c, 0.15f);
                }
                _img.SetPixel(px, py, c);
            }
        if (_tex == null) _tex = ImageTexture.CreateFromImage(_img); else _tex.Update(_img);
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_dirty && (_wait -= delta) <= 0) { _dirty = false; _wait = 0.2; Redraw(); }
        QueueRedraw();
    }

    /// <summary>the turn of the map (radians): the camera's yaw - its view direction points up</summary>
    double Angle => CameraPose?.Invoke().Yaw ?? 0;
    /// <summary>metres per texel, and the map's part of the picture (texels)</summary>
    new (double S, float W, float H) Scale()
    {
        var d = _doc!;
        double w = d.HeightsW * d.Cell, h = d.HeightsH * d.Cell, s = Math.Max(w, h) / N;
        return (s, (float)(w / s), (float)(h / s));
    }

    /// <summary>control pixel -> game (x, z): turned back, then the picture's texel</summary>
    (double X, double Z) GameOf(Vector2 p)
    {
        var d = _doc!;
        var (s, cw, ch) = Scale();
        var q = (p - Size / 2).Rotated((float)-Angle) + new Vector2(cw / 2, ch / 2);
        return (d.OriginX + q.X * s - Ox, d.OriginY + q.Y * s - Oy);
    }

    /// <summary>game (x, z) -> control pixel: the picture's texel round the map's centre, turned</summary>
    Vector2 PixelOf(double x, double z)
    {
        var d = _doc!;
        var (s, cw, ch) = Scale();
        var q = new Vector2((float)((x + Ox - d.OriginX) / s), (float)((z + Oy - d.OriginY) / s)) - new Vector2(cw / 2, ch / 2);
        return Size / 2 + q.Rotated((float)Angle);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (_doc == null) return;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _drag = mb.Pressed;
            if (mb.Pressed) { var (x, z) = GameOf(mb.Position); LookAt?.Invoke(x, z); }
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && _drag) { var (x, z) = GameOf(mm.Position); LookAt?.Invoke(x, z); AcceptEvent(); }
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        DrawRect(r, new Color(0.1f, 0.08f, 0.05f));
        if (_doc != null && _tex != null)
        {
            // the map's part of the picture, centred and turned (the texels past the map's shorter side are left out)
            var (_, cw, ch) = Scale();
            DrawSetTransform(Size / 2, (float)Angle, Vector2.One);
            DrawTextureRectRegion(_tex, new Rect2(-cw / 2, -ch / 2, cw, ch), new Rect2(0, 0, cw, ch));
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        }
        DrawRect(r, PwRemake.UI.PwTheme.Dim, false, 1);
        if (_doc == null) return;
        foreach (var (x, z) in Starts) { var p = PixelOf(x, z); DrawCircle(p, 4, new Color(1f, 0.85f, 0.2f)); DrawArc(p, 4, 0, Mathf.Tau, 12, Colors.Black, 1); }
        if (CameraPose?.Invoke() is { } cp)
        {
            var p = PixelOf(cp.X, cp.Z);
            var fwd = new Vector2((float)-Math.Sin(cp.Yaw), (float)-Math.Cos(cp.Yaw)).Rotated((float)Angle);   // (up: the map turns with the camera)
            DrawCircle(p, 3, Colors.White);
            DrawLine(p, p + fwd * 12, Colors.White, 2);
        }
    }
}
