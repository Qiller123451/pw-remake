// The original HUD art the mission UI draws with (Data/Base/UI/hud/*, converted by the toolkit to assets/ui/hud_*.png
// and the atlas assets/ui/atlas.json): the news ticker's line background (top / centre / bottom strips), the tooltip
// frame (nine pieces) around the dialogue box, the quest button, the close button, the timers' pills, the ticker's
// icons and the portraits of the card sheets. Through the configuration's AssetSource; a missing file gives null
// and the views draw flat colours instead.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Mission;

public static class HudArt
{
    static readonly Logger L = PwRemake.Sim.Core.Log.For("UI.Mission");
    static AssetSource? s_assets;
    static readonly Dictionary<string, Texture2D?> s_tex = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, StyleBox> s_boxes = new();

    /// <summary>Use the art of this configuration; loads the atlas.</summary>
    public static void Init(AssetSource? assets)
    {
        s_assets = assets;
        s_tex.Clear();
        s_boxes.Clear();
        UiAtlas.Load(assets?.Resolve("assets/ui/atlas.json"), assets?.Resolve("assets/ui/atlas_sequel.json"));
        if (UiAtlas.Count == 0) L.Warn("no assets/ui/atlas.json: no portraits or ticker icons");
    }

    /// <summary>A texture below assets/ui/ ("hud_timers.png", "menu/menue__decoration__cinema_topsoft.png"), cached.</summary>
    public static Texture2D? Tex(string file)
    {
        if (s_tex.TryGetValue(file, out var t)) return t;
        t = null;
        var path = s_assets?.Resolve("assets/ui/" + file);
        if (path != null)
        {
            var img = Image.LoadFromFile(path);
            if (img != null && !img.IsEmpty()) t = ImageTexture.CreateFromImage(img);
        }
        if (t == null) L.WarnOnce("hud:" + file, $"missing HUD art assets/ui/{file}");
        s_tex[file] = t;
        return t;
    }

    /// <summary>A region of the atlas as a texture (level: the card size: 2 = 37 px, 3 = 51 px, 4 = 64 px).</summary>
    public static AtlasTexture? Atlas(string? name, int? level = null)
    {
        var e = UiAtlas.Entry(name, level);
        if (e == null) return null;
        var tex = Tex(e.Value.File);
        return tex == null ? null : new AtlasTexture { Atlas = tex, Region = new Rect2(e.Value.X, e.Value.Y, e.Value.W, e.Value.H) };
    }

    /// <summary>The ticker line's background: hud_newsticker_background_top / _center / _bottom (233 px wide strips
    /// with the gold bar at the right), the centre stretched (NewsTicker.usl).</summary>
    public static StyleBox Ticker(bool hover = false)
    {
        var key = hover ? "ticker_h" : "ticker";
        if (s_boxes.TryGetValue(key, out var b)) return b;
        string sfx = hover ? "_hover" : "";
        var top = Img($"hud_newsticker_background_top{sfx}.png");
        var mid = Img($"hud_newsticker_background_center{sfx}.png");
        var bot = Img($"hud_newsticker_background_bottom{sfx}.png");
        if (top == null || mid == null || bot == null)
            b = new StyleBoxFlat { BgColor = new Color(30 / 255f, 20 / 255f, 10 / 255f, 0.8f), BorderColor = new Color(0.63f, 0.48f, 0.25f), BorderWidthRight = 2, ContentMarginLeft = 8, ContentMarginRight = 10, ContentMarginTop = 4, ContentMarginBottom = 4 };
        else
        {
            int w = top.GetWidth(), h = top.GetHeight() + mid.GetHeight() + bot.GetHeight();
            var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
            img.BlitRect(top, new Rect2I(0, 0, w, top.GetHeight()), Vector2I.Zero);
            img.BlitRect(mid, new Rect2I(0, 0, w, mid.GetHeight()), new Vector2I(0, top.GetHeight()));
            img.BlitRect(bot, new Rect2I(0, 0, w, bot.GetHeight()), new Vector2I(0, top.GetHeight() + mid.GetHeight()));
            b = new StyleBoxTexture
            {
                Texture = ImageTexture.CreateFromImage(img),
                TextureMarginTop = top.GetHeight(), TextureMarginBottom = bot.GetHeight(), TextureMarginLeft = 12, TextureMarginRight = 14,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
                ContentMarginLeft = 8, ContentMarginRight = 16, ContentMarginTop = 5, ContentMarginBottom = 5,
            };
        }
        s_boxes[key] = b;
        return b;
    }

    /// <summary>The HUD's tooltip frame (hud_tooltip_topleft ... bottomright: 12 / 25 px corners, tiled edges and
    /// centre) as one nine-patch - the frame of the dialogue box and the question marks' tooltips.</summary>
    public static StyleBox Tooltip()
    {
        if (s_boxes.TryGetValue("tooltip", out var b)) return b;
        string[] n = { "topleft", "topcenter", "topright", "centerleft", "centercenter", "centerright", "bottomleft", "bottomcenter", "bottomright" };
        var parts = new Image?[9];
        for (int i = 0; i < 9; i++) parts[i] = Img($"hud_tooltip_{n[i]}.png");
        if (Array.Exists(parts, p => p == null))
        {
            var f = new StyleBoxFlat { BgColor = new Color(43 / 255f, 33 / 255f, 23 / 255f, 0.92f), BorderColor = new Color(0.63f, 0.48f, 0.25f) };
            f.SetBorderWidthAll(1); f.SetContentMarginAll(8);
            b = f;
        }
        else
        {
            // a 3 x 3 sheet: columns as wide as the pieces of the top row, rows as high as the left column's
            int c0 = parts[0]!.GetWidth(), c1 = parts[1]!.GetWidth(), c2 = parts[2]!.GetWidth();
            int r0 = parts[0]!.GetHeight(), r1 = parts[3]!.GetHeight(), r2 = parts[6]!.GetHeight();
            var img = Image.CreateEmpty(c0 + c1 + c2, r0 + r1 + r2, false, Image.Format.Rgba8);
            int[] xs = { 0, c0, c0 + c1 }, ys = { 0, r0, r0 + r1 };
            for (int i = 0; i < 9; i++)
            {
                var p = parts[i]!;
                img.BlitRect(p, new Rect2I(0, 0, p.GetWidth(), p.GetHeight()), new Vector2I(xs[i % 3], ys[i / 3]));
            }
            b = new StyleBoxTexture
            {
                Texture = ImageTexture.CreateFromImage(img),
                TextureMarginLeft = c0, TextureMarginRight = c2, TextureMarginTop = r0, TextureMarginBottom = r2,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                ContentMarginLeft = 9, ContentMarginRight = 14, ContentMarginTop = 9, ContentMarginBottom = 9,
            };
        }
        s_boxes["tooltip"] = b;
        return b;
    }

    static Image? Img(string file)
    {
        var t = Tex(file);
        if (t == null) return null;
        var i = t.GetImage();
        if (i.IsCompressed()) i.Decompress();
        i.Convert(Image.Format.Rgba8);
        return i;
    }

    public static Color Rgb(int c, float a = 1) => new(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f, a);

    /// <summary>The sans-serif face of the HUD texts (the browser remake's "Trebuchet MS").</summary>
    public static Font Sans { get; } = new SystemFont { FontNames = new[] { "Trebuchet MS", "Segoe UI", "DejaVu Sans", "sans-serif" } };
    public static Font SansBold { get; } = new SystemFont { FontNames = new[] { "Trebuchet MS", "Segoe UI", "DejaVu Sans", "sans-serif" }, FontWeight = 700 };

    public static LabelSettings Text(int size, Color color, bool bold = false, float shadow = 0.85f) => new()
    {
        Font = bold ? SansBold : Sans, FontSize = size, FontColor = color, ShadowColor = new Color(0, 0, 0, shadow), ShadowOffset = new Vector2(1, 1), ShadowSize = 2,
    };
}
