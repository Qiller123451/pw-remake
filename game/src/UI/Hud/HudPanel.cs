// The HUD's drawing helpers and the base of its panels.
//
// HudSprites: the atlas regions (assets/ui/atlas.json through UiAtlas / HudArt: hud.js spriteCss) and the plain HUD
// pictures (assets/ui/hud_*.png), drawn into a rectangle; the fonts (the original's: every font of UI/VisDef_GAM2.vis
// is "Trebuchet MS" bold - the browser remake had a Palatino page font for most texts; as multichannel distance fields,
// so the text stays sharp at any UI scale).
//
// HudPanel: a part of the HUD (top bar, info window, command bar, pyramid, minimap) is one Control that draws itself
// and keeps a list of hit regions (what the JS made as DOM elements with mouse handlers): hovering one shows its
// tooltip, clicks go to its handlers. Only the regions take the mouse; the rest of the panel lets it through to the
// world (hud.js: the HUD root has no pointer events, its parts do).
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.UI.Hud.Logic;
using PwRemake.UI.Mission;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Hud;

public static class HudSprites
{
    static readonly Dictionary<string, AtlasTexture?> s_atlas = new();

    /// <summary>an atlas region (level: the card size 0..4, -1, or the entry's own; null: the largest)</summary>
    public static AtlasTexture? Atlas(string? name, int? level = null)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var key = name + "|" + level;
        if (s_atlas.TryGetValue(key, out var t)) return t;
        t = HudArt.Atlas(name, level);
        s_atlas[key] = t;
        return t;
    }
    public static bool Has(string? name, int? level = null) => name != null && UiAtlas.Entry(name, level) != null;

    /// <summary>draws an atlas region into r (spriteCss: scaled to the box); false when the name is not in the atlas</summary>
    public static bool Draw(CanvasItem ci, string? name, int? level, Rect2 r, Color? mod = null)
    {
        var e = UiAtlas.Entry(name, level);
        if (e == null) return false;
        var tex = HudArt.Tex(e.Value.File);
        if (tex == null) return false;
        ci.DrawTextureRectRegion(tex, r, new Rect2(e.Value.X, e.Value.Y, e.Value.W, e.Value.H), mod ?? Colors.White);
        return true;
    }
    /// <summary>the top part of an atlas region (the epoch bar's layers: height in % of the region)</summary>
    public static void DrawTop(CanvasItem ci, string name, int? level, Rect2 r, double frac)
    {
        var e = UiAtlas.Entry(name, level);
        if (e == null || frac <= 0) return;
        var tex = HudArt.Tex(e.Value.File);
        if (tex == null) return;
        float f = (float)Math.Min(1, frac);
        ci.DrawTextureRectRegion(tex, new Rect2(r.Position, new Vector2(r.Size.X, r.Size.Y * f)), new Rect2(e.Value.X, e.Value.Y, e.Value.W, e.Value.H * f));
    }
    static readonly Dictionary<string, ImageTexture?> s_gray = new();
    // the card sheets' pixels, read once per sheet: a texture's GetImage is a read-back from the graphics card (a stall),
    // and every greyed card used to read (and decompress) its whole sheet again - the first opening of a build menu with
    // locked cards hitched (user report "lag when building for the first time")
    static readonly Dictionary<string, Image?> s_sheet = new();

    /// <summary>an atlas region greyed (CSS filter: grayscale(gray) brightness(bright)): the flyout's locked and dimmed cards</summary>
    public static bool DrawGray(CanvasItem ci, string? name, int? level, Rect2 r, float bright, float gray, Color? mod = null)
    {
        if (UiAtlas.Entry(name, level) == null) return false;
        var t = Gray(name, level, bright, gray);
        if (t == null) return Draw(ci, name, level, r, new Color(bright, bright, bright, mod?.A ?? 1));
        ci.DrawTextureRect(t, r, false, mod ?? Colors.White);
        return true;
    }

    /// <summary>the greyed copy of an atlas region (made once; null: not in the atlas or no sheet)</summary>
    public static ImageTexture? Gray(string? name, int? level, float bright, float gray)
    {
        var e = UiAtlas.Entry(name, level);
        if (e == null) return null;
        var key = $"{e.Value.File}|{e.Value.X}|{e.Value.Y}|{e.Value.W}|{e.Value.H}|{bright}|{gray}";
        if (s_gray.TryGetValue(key, out var t)) return t;
        t = null;
        if (!s_sheet.TryGetValue(e.Value.File, out var src))
        {
            src = HudArt.Tex(e.Value.File)?.GetImage();
            if (src != null && src.IsCompressed()) src.Decompress();
            s_sheet[e.Value.File] = src;
        }
        if (src != null)
        {
            var img = src.GetRegion(new Rect2I(e.Value.X, e.Value.Y, e.Value.W, e.Value.H));
            img.Convert(Image.Format.Rgba8);
            // (on the bytes: a GetPixel / SetPixel pair per pixel is two engine calls)
            var px = img.GetData();
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                float cr = px[i] / 255f, cg = px[i + 1] / 255f, cb = px[i + 2] / 255f;
                // (the CSS filter's luminance weights)
                float l = 0.2126f * cr + 0.7152f * cg + 0.0722f * cb;
                px[i] = (byte)Math.Clamp(Mathf.Lerp(cr, l, gray) * bright * 255f + 0.5f, 0, 255);
                px[i + 1] = (byte)Math.Clamp(Mathf.Lerp(cg, l, gray) * bright * 255f + 0.5f, 0, 255);
                px[i + 2] = (byte)Math.Clamp(Mathf.Lerp(cb, l, gray) * bright * 255f + 0.5f, 0, 255);
            }
            img.SetData(img.GetWidth(), img.GetHeight(), false, Image.Format.Rgba8, px);
            t = ImageTexture.CreateFromImage(img);
        }
        s_gray[key] = t;
        return t;
    }

    static StyleBox? s_flyout;
    static bool s_flyoutTried;
    /// <summary>the flyout window's frame: hud/flyoutwin_topleft ... bottomright as one nine-patch (the centre stretched)</summary>
    public static StyleBox? FlyoutBox()
    {
        if (s_flyoutTried) return s_flyout;
        s_flyoutTried = true;
        string[] n = { "topleft", "topcenter", "topright", "centerleft", "centercenter", "centerright", "bottomleft", "bottomcenter", "bottomright" };
        var parts = new Image[9];
        for (int i = 0; i < 9; i++)
        {
            var img = HudArt.Tex($"hud_flyoutwin_{n[i]}.png")?.GetImage();
            if (img == null) return null;
            if (img.IsCompressed()) img.Decompress();
            img.Convert(Image.Format.Rgba8);
            parts[i] = img;
        }
        // (the centre picture is 410 x 40: scaled to the corner's cell, it is stretched again by the style box)
        int c0 = parts[0].GetWidth(), c2 = parts[2].GetWidth(), r0 = parts[0].GetHeight(), r2 = parts[6].GetHeight();
        int c1 = 32, r1 = 32;
        var sheet = Image.CreateEmpty(c0 + c1 + c2, r0 + r1 + r2, false, Image.Format.Rgba8);
        int[] xs = { 0, c0, c0 + c1 }, ys = { 0, r0, r0 + r1 }, ws = { c0, c1, c2 }, hs = { r0, r1, r2 };
        for (int i = 0; i < 9; i++)
        {
            var p = parts[i];
            int w = ws[i % 3], h = hs[i / 3];
            if (p.GetWidth() != w || p.GetHeight() != h) p.Resize(w, h, Image.Interpolation.Bilinear);
            sheet.BlitRect(p, new Rect2I(0, 0, w, h), new Vector2I(xs[i % 3], ys[i / 3]));
        }
        s_flyout = new StyleBoxTexture
        {
            Texture = ImageTexture.CreateFromImage(sheet),
            TextureMarginLeft = c0, TextureMarginRight = c2, TextureMarginTop = r0, TextureMarginBottom = r2,
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
        };
        return s_flyout;
    }

    /// <summary>a HUD picture (assets/ui/&lt;file&gt;) into r</summary>
    public static bool Pic(CanvasItem ci, string file, Rect2 r, Color? mod = null)
    {
        var t = HudArt.Tex(file);
        if (t == null) return false;
        ci.DrawTextureRect(t, r, false, mod ?? Colors.White);
        return true;
    }

    // ------------------------------------------------------------------ fonts
    // (the original's font everywhere: VisDef_GAM2.vis Fonts - Button, Normal, ToolTip, Title ... all 'Trebuchet MS,..,bold';
    // "Serif" kept as the name of the HUD's body text, which the browser remake set in Palatino)
    static readonly string[] SERIF = { "Trebuchet MS", "Segoe UI", "DejaVu Sans", "sans-serif" };
    public static readonly Font Serif = new SystemFont { FontNames = SERIF, FontWeight = 700, MultichannelSignedDistanceField = true, MsdfPixelRange = 12, MsdfSize = 48 };
    public static readonly Font SerifBold = new SystemFont { FontNames = SERIF, FontWeight = 700, MultichannelSignedDistanceField = true, MsdfPixelRange = 12, MsdfSize = 48 };
    public static readonly Font Sans = new SystemFont { FontNames = new[] { "Trebuchet MS", "Segoe UI", "DejaVu Sans", "sans-serif" }, FontWeight = 700, MultichannelSignedDistanceField = true, MsdfPixelRange = 12, MsdfSize = 48 };
    public static readonly Font Mono = new SystemFont { FontNames = new[] { "Consolas", "DejaVu Sans Mono", "monospace" }, MultichannelSignedDistanceField = true };

    public static Color Hex(string h) => Color.FromHtml(h);
    public static Color Rgb(int c, float a = 1) => new(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f, a);

    /// <summary>a text with the HUD's shadow (text-shadow: 0 1px 2px #000)</summary>
    public static void Text(CanvasItem ci, Font f, Vector2 pos, string s, int size, Color c, HorizontalAlignment al = HorizontalAlignment.Left, float width = -1, float shadow = 0.9f)
    {
        ci.DrawString(f, pos + new Vector2(0, 1), s, al, width, size, new Color(0, 0, 0, shadow));
        ci.DrawString(f, pos + new Vector2(0.6f, 1.4f), s, al, width, size, new Color(0, 0, 0, shadow * 0.5f));
        ci.DrawString(f, pos, s, al, width, size, c);
    }
    /// <summary>a text cut to a width with "…" (text-overflow: ellipsis)</summary>
    public static string Fit(Font f, string s, int size, float width)
    {
        if (f.GetStringSize(s, HorizontalAlignment.Left, -1, size).X <= width) return s;
        while (s.Length > 1 && f.GetStringSize(s + "…", HorizontalAlignment.Left, -1, size).X > width) s = s[..^1];
        return s + "…";
    }
}

/// <summary>A region of a panel that takes the mouse.</summary>
public sealed class Hit
{
    public Rect2 R;
    public string Id = "";
    public Func<Tip?>? Tip;
    /// <summary>left click (shift, ctrl, alt)</summary>
    public Action<bool, bool, bool>? Click;
    public Action<bool, bool, bool>? Right;
    public Action<bool, bool, bool>? Double;
    /// <summary>left button pressed (drag starts)</summary>
    public Action<Vector2>? Down;
    /// <summary>the tooltip's anchor is this rect (else R)</summary>
    public Rect2? TipAnchor;
}

public abstract partial class HudPanel : Control
{
    protected readonly Hud Hud;
    protected readonly List<Hit> Hits = new();
    /// <summary>areas that take the mouse without doing anything (the panel's picture)</summary>
    protected readonly List<Rect2> Solid = new();
    protected Hit? Hover;

    protected HudPanel(Hud hud)
    {
        Hud = hud;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = false;
    }

    public override bool _HasPoint(Vector2 p)
    {
        foreach (var h in Hits) if (h.R.HasPoint(p)) return true;
        foreach (var r in Solid) if (r.HasPoint(p)) return true;
        return false;
    }

    protected Hit? HitAt(Vector2 p)
    {
        for (int i = Hits.Count - 1; i >= 0; i--) if (Hits[i].R.HasPoint(p)) return Hits[i];
        return null;
    }

    /// <summary>the hit regions are made anew while drawing</summary>
    protected Hit AddHit(Rect2 r, string id, Func<Tip?>? tip = null)
    {
        var h = new Hit { R = r, Id = id, Tip = tip };
        Hits.Add(h);
        return h;
    }

    public override void _Draw()
    {
        var P = PwRemake.View.PerfProbe.P;
        long t0 = P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0, b0 = P != null ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var hov = Hover?.Id;
        Hits.Clear(); Solid.Clear();
        Paint();
        Hover = hov == null ? null : Hits.Find(h => h.Id == hov);
        // (--perf: each panel's drawing, "hud:draw <name>")
        P?.Add(PwRemake.Sim.World.StepProfile.Key("hud:draw ", GetType().Name), System.Diagnostics.Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - b0);
    }

    protected abstract void Paint();
    protected bool IsHover(string id) => Hover != null && Hover.Id == id;

    /// <summary>a screen rectangle of a local one (the tooltip's anchor)</summary>
    public Rect2 ScreenRect(Rect2 r) => GetGlobalTransformWithCanvas() * r;

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            var h = HitAt(mm.Position);
            if (h != Hover)
            {
                Hover = h;
                if (h?.Tip != null) { var t = h.Tip(); if (t != null) Hud.ShowTip(t, ScreenRect(h.TipAnchor ?? h.R)); else Hud.HideTip(); }
                else Hud.HideTip();
                QueueRedraw();
            }
            OnMotion(mm);
            return;
        }
        if (e is InputEventMouseButton mb)
        {
            var h = HitAt(mb.Position);
            bool s = mb.ShiftPressed, c = mb.CtrlPressed || mb.MetaPressed, a = mb.AltPressed;
            if (mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.DoubleClick && h?.Double != null) { h.Double(s, c, a); AcceptEvent(); return; }
                if (h?.Down != null) h.Down(mb.GlobalPosition);
                OnLeftDown(mb, h);
            }
            else if (!mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                if (!OnLeftUp(mb, h) && h?.Click != null) { h.Click(s, c, a); QueueRedraw(); }
            }
            else if (!mb.Pressed && mb.ButtonIndex == MouseButton.Right && h?.Right != null) { h.Right(s, c, a); QueueRedraw(); }
            AcceptEvent();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && Hover != null) { Hover = null; Hud.HideTip(); QueueRedraw(); }
    }

    protected virtual void OnMotion(InputEventMouseMotion e) { }
    protected virtual void OnLeftDown(InputEventMouseButton e, Hit? h) { }
    /// <summary>true: handled (no click)</summary>
    protected virtual bool OnLeftUp(InputEventMouseButton e, Hit? h) => false;
}
