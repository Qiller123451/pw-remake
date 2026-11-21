// The day-night indicator of a map with a day-night cycle (map.lua day_night, docs/MAP_FORMAT.md; the Outbreak mission):
// a small plate under the top bar with the sun, the moon or the sun at the horizon (dusk, dawn), the phase and the time
// to the next one - "Day · night in 3:12", "Night 2 · dawn in 1:20". A bar under it shows the whole cycle (day light,
// night dark) and where it stands. Drawn only; it reads the world's clock (World.DayNightNow).
using System;
using Godot;
using PwRemake.Sim.World;

namespace PwRemake.UI.Hud;

public partial class DayNightIndicator : Control
{
    readonly Func<DayNightState?> _now;
    readonly Func<DayNightSpec?> _spec;
    readonly Label _label;
    string _last = "";
    DayNightState _s;

    public DayNightIndicator(Func<DayNightState?> now, Func<DayNightSpec?> spec)
    {
        _now = now; _spec = spec;
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorLeft = 0.5f; AnchorRight = 0.5f;
        OffsetLeft = -110; OffsetRight = 110; OffsetTop = 74; OffsetBottom = 110;
        _label = new Label { Position = new Vector2(40, 3), Size = new Vector2(176, 22), MouseFilter = MouseFilterEnum.Ignore };
        _label.AddThemeFontSizeOverride("font_size", 15);
        _label.AddThemeColorOverride("font_color", new Color(0.95f, 0.92f, 0.82f));
        _label.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _label.AddThemeConstantOverride("shadow_offset_x", 1); _label.AddThemeConstantOverride("shadow_offset_y", 1);
        AddChild(_label);
    }

    static string Clock(double s) { int t = (int)Math.Ceiling(Math.Max(0, s)); return $"{t / 60}:{t % 60:00}"; }

    /// <summary>the line shown for a state (tests)</summary>
    public static string Text(DayNightState s) => s.Phase switch
    {
        DayPhase.Day => $"Day · night in {Clock(s.ToNight)}",
        DayPhase.Dusk => $"Dusk · night in {Clock(s.Left)}",
        DayPhase.Night => $"Night {s.NightNo} · dawn in {Clock(s.Left)}",
        _ => $"Dawn · day in {Clock(s.Left)}",
    };

    public override void _Process(double delta)
    {
        if (_now() is not { } s) { Visible = false; return; }
        Visible = true;
        _s = s;
        var t = Text(s);
        if (t != _last) { _last = t; _label.Text = t; }
        QueueRedraw();
    }

    public override void _Draw()
    {
        var s = _s;
        // the plate
        DrawRect(new Rect2(0, 0, 220, 36), new Color(0.05f, 0.05f, 0.07f, 0.62f));
        DrawRect(new Rect2(0, 0, 220, 36), new Color(0.75f, 0.62f, 0.38f, 0.8f), false, 1);
        // the icon: the sun (rays), the moon (a crescent), at dusk / dawn the sun half under the horizon
        var c = new Vector2(20, 16);
        var sun = new Color(1f, 0.82f, 0.35f);
        switch (s.Phase)
        {
            case DayPhase.Day:
                DrawCircle(c, 7, sun);
                for (int i = 0; i < 8; i++) { var a = i * Mathf.Pi / 4; DrawLine(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 9, c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 12, sun, 2); }
                break;
            case DayPhase.Night:
                DrawCircle(c, 8, new Color(0.85f, 0.9f, 1f));
                DrawCircle(c + new Vector2(4, -3), 7, new Color(0.08f, 0.08f, 0.12f));
                break;
            default:
                var hz = new Color(1f, 0.55f, 0.3f);
                DrawCircle(c + new Vector2(0, 3), 7, hz);
                DrawRect(new Rect2(c.X - 10, c.Y + 3, 20, 8), new Color(0.05f, 0.05f, 0.07f, 1f));
                DrawLine(new Vector2(c.X - 12, c.Y + 3), new Vector2(c.X + 12, c.Y + 3), hz, 2);
                break;
        }
        // the cycle as a bar: day light, dusk / dawn orange, night dark blue; the marker where it stands
        if (_spec() is not { } sp) return;
        double C = sp.Cycle;
        float x0 = 40, w = 172, y = 27;
        void Seg(double from, double len, Color col) => DrawRect(new Rect2(x0 + (float)(from / C) * w, y, (float)(len / C) * w, 4), col);
        Seg(0, sp.Day, new Color(0.9f, 0.8f, 0.45f));
        Seg(sp.Day, sp.Dusk, new Color(0.95f, 0.5f, 0.25f));
        Seg(sp.Day + sp.Dusk, sp.Night, new Color(0.18f, 0.24f, 0.5f));
        Seg(sp.Day + sp.Dusk + sp.Night, sp.Dawn, new Color(0.95f, 0.5f, 0.25f));
        double at = s.Phase switch
        {
            DayPhase.Day => sp.Day * (s.Into / Math.Max(1e-6, s.Into + s.Left)),
            DayPhase.Dusk => sp.Day + s.Into,
            DayPhase.Night => sp.Day + sp.Dusk + s.Into,
            _ => sp.Day + sp.Dusk + sp.Night + s.Into,
        };
        float mx = x0 + (float)(at / C) * w;
        DrawLine(new Vector2(mx, y - 2), new Vector2(mx, y + 6), Colors.White, 2);
    }
}
