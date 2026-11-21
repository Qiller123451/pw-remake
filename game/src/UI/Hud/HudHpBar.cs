// The hit point bar of a unit card (the info window's portrait, the army controller's cards) drawn as the original's
// blend layers: "ovl_hpbar_bg", then the slices of "ovl_hpbar" (HpBarRule: hslicebar / hslicehp; docs/HUD.md "Hit point
// bars"). The engine REPLACES the layer's red mask texels by the bar's colour - it does not multiply: ovl_hpbar's
// texels are (255, 64, 0) and (211, 25, 0), so a tint would turn every colour red, a blue one black (the bug before
// this: an orange player's bar red, a blue enemy's black). The mask is made once per atlas region: its mask texels
// white (their alpha kept), the rest dropped (ovl_hpbar has only mask texels), then drawn modulated by the colour.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.UI.Hud.Logic;
using PwRemake.UI.Mission;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Hud;

public static class HudHpBar
{
    static readonly Dictionary<AtlasEntry, ImageTexture?> s_mask = new();

    static ImageTexture? Mask(AtlasEntry e)
    {
        if (s_mask.TryGetValue(e, out var t)) return t;
        t = null;
        var img = HudArt.Tex(e.File)?.GetImage();
        if (img != null && !img.IsEmpty())
        {
            if (img.IsCompressed()) img.Decompress();
            var m = Image.CreateEmpty(e.W, e.H, false, Image.Format.Rgba8);
            for (int y = 0; y < e.H; y++)
                for (int x = 0; x < e.W; x++)
                {
                    int sx = e.X + x, sy = e.Y + y;
                    if (sx >= img.GetWidth() || sy >= img.GetHeight()) continue;
                    var c = img.GetPixel(sx, sy);
                    if (c.A > 0 && HpBarRule.IsMask(c.R8, c.G8, c.B8)) m.SetPixel(x, y, new Color(1, 1, 1, c.A));
                }
            t = ImageTexture.CreateFromImage(m);
        }
        s_mask[e] = t;
        return t;
    }

    /// <summary>the bar over a card's rectangle r (its layers' size) at card level lv: p % (HpBarRule.Slice) in rgb
    /// (null: hslicehp's health colour), under it the full width in full (null: none - the bg layer shows)</summary>
    public static void Draw(CanvasItem ci, Rect2 r, int lv, int p, int? rgb, int? full)
    {
        var bg = UiAtlas.Entry("ovl_hpbar_bg", lv);
        var bar = UiAtlas.Entry("ovl_hpbar", lv);
        int fill = rgb ?? HpBarRule.HealthRgb(p);
        var mask = bar is { } be ? Mask(be) : null;
        if (bg == null || bar == null || mask == null)
        {
            // (no layers: a 3 px bar at the card's bottom)
            var b = new Rect2(r.Position.X, r.End.Y - 4, r.Size.X, 3);
            DrawSolid(ci, b, full ?? 0, full != null ? 1f : 0.85f);
            ci.DrawRect(new Rect2(b.Position, new Vector2(b.Size.X * Math.Clamp(p, 0, 100) / 100f, b.Size.Y)), HudSprites.Rgb(fill));
            return;
        }
        var tb = HudArt.Tex(bg.Value.File);
        if (tb != null) ci.DrawTextureRectRegion(tb, r, new Rect2(bg.Value.X, bg.Value.Y, bg.Value.W, bg.Value.H));
        float sx = r.Size.X / bar.Value.W;
        void Slice(int pct, int c)
        {
            int w = HpBarRule.SliceWidth(bar.Value.W, pct);
            if (w <= 0) return;
            ci.DrawTextureRectRegion(mask, new Rect2(r.Position, new Vector2(w * sx, r.Size.Y)), new Rect2(0, 0, w, bar.Value.H), HudSprites.Rgb(c));
        }
        if (full != null) Slice(100, full.Value);
        Slice(p, fill);
    }

    static void DrawSolid(CanvasItem ci, Rect2 b, int rgb, float a) => ci.DrawRect(b, HudSprites.Rgb(rgb, a));
}
