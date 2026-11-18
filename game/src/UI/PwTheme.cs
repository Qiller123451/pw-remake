// The look of the menus, from the original game's menu art (Data/Base/UI/menue/**, converted by the toolkit into
// assets/ui/menu/*.png): the button pieces (left / centre / right, per state), the window frame (corners and
// tiles), check boxes, the colours and fonts of the browser remake's menus (index.src.html: --pw-text ...).
// Every texture comes through the configuration's AssetSource; a missing file falls back to flat colours, so the
// menus work before any configuration exists (the setup screen).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.UI;

public static class PwTheme
{
    static readonly Logger L = Log.For("UI.Theme");

    public static readonly Color Text = new(224 / 255f, 185 / 255f, 120 / 255f);
    public static readonly Color Dim = new(152 / 255f, 133 / 255f, 99 / 255f);
    public static readonly Color Title = new(60 / 255f, 21 / 255f, 0);
    public static readonly Color Hi = new(1f, 1f, 200 / 255f);
    public static readonly Color Won = new(200 / 255f, 240 / 255f, 160 / 255f);
    public static readonly Color Lost = new(1f, 154 / 255f, 122 / 255f);

    static AssetSource? s_assets;
    /// <summary>the other configurations' files: the original's menu art is the same in every one, and a mod's data
    /// folder may have been built without some of it (as the loading screen's pictures)</summary>
    static List<AssetSource> s_more = new();
    static readonly Dictionary<string, Texture2D?> s_tex = new();
    static Theme? s_theme;

    public static Font BodyFont { get; private set; } = new SystemFont();
    public static Font ButtonFont { get; private set; } = new SystemFont();

    /// <summary>Use the menu art of this configuration (null: flat colours only).</summary>
    public static void Init(AssetSource? assets, IEnumerable<AssetSource>? others = null)
    {
        s_assets = assets;
        s_more = others?.ToList() ?? new();
        s_tex.Clear();
        s_theme = null;
        // (the original's menus and windows: every font of UI/VisDef_GAM2.vis is 'Trebuchet MS,<size>,bold')
        BodyFont = new SystemFont { FontNames = new[] { "Trebuchet MS", "Segoe UI", "DejaVu Sans", "sans-serif" }, FontWeight = 700 };
        ButtonFont = new SystemFont { FontNames = new[] { "Trebuchet MS", "Segoe UI", "sans-serif" }, FontWeight = 700 };
    }

    /// <summary>A texture of the menu art by its short name (menue__&lt;name&gt;.png), cached; null if missing.</summary>
    public static Texture2D? Tex(string name)
    {
        if (s_tex.TryGetValue(name, out var t)) return t;
        t = null;
        string rel = name.Contains('/') ? name : $"assets/ui/menu/menue__{name}" + (name.EndsWith(".jpg") || name.EndsWith(".png") ? "" : ".png");
        var path = s_assets?.Resolve(rel);
        foreach (var o in s_more) { if (path != null) break; path = o.Resolve(rel); }
        if (path != null)
        {
            var img = Image.LoadFromFile(path);
            if (img != null && !img.IsEmpty()) t = ImageTexture.CreateFromImage(img);
            else L.WarnOnce(rel, $"cannot read image {path}");
        }
        s_tex[name] = t;
        return t;
    }

    public static Image? Img(string name) => Tex(name)?.GetImage();

    /// <summary>left + centre + right of a button state as one texture with margins (the centre stretches).</summary>
    static StyleBox ButtonBox(string state, Color fallback)
    {
        var l = Img($"controls__button_{state}_left");
        var c = Img($"controls__button_{state}_center");
        var r = Img($"controls__button_{state}_right");
        if (l == null || c == null || r == null)
            return new StyleBoxFlat { BgColor = fallback, BorderColor = Dim, BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 16, ContentMarginRight = 16 };
        int h = Math.Max(l.GetHeight(), Math.Max(c.GetHeight(), r.GetHeight()));
        int w = l.GetWidth() + c.GetWidth() + r.GetWidth();
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        foreach (var part in new[] { l, c, r }) part.Convert(Image.Format.Rgba8);
        img.BlitRect(l, new Rect2I(0, 0, l.GetWidth(), l.GetHeight()), Vector2I.Zero);
        img.BlitRect(c, new Rect2I(0, 0, c.GetWidth(), c.GetHeight()), new Vector2I(l.GetWidth(), 0));
        img.BlitRect(r, new Rect2I(0, 0, r.GetWidth(), r.GetHeight()), new Vector2I(l.GetWidth() + c.GetWidth(), 0));
        var box = new StyleBoxTexture
        {
            Texture = ImageTexture.CreateFromImage(img),
            TextureMarginLeft = l.GetWidth(), TextureMarginRight = r.GetWidth(),
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 2, ContentMarginBottom = 2,
        };
        return box;
    }

    /// <summary>Pieces side by side as one texture (left, tiles ..., right): the original's controls are cut into
    /// left / centre / right parts; null when one is missing.</summary>
    public static ImageTexture? Strip(params string[] parts)
    {
        var imgs = parts.Select(Img).ToList();
        if (imgs.Any(i => i == null)) return null;
        int h = imgs.Max(i => i!.GetHeight()), w = imgs.Sum(i => i!.GetWidth()), x = 0;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        foreach (var i in imgs)
        {
            i!.Convert(Image.Format.Rgba8);
            img.BlitRect(i, new Rect2I(0, 0, i.GetWidth(), i.GetHeight()), new Vector2I(x, 0));
            x += i.GetWidth();
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Pieces one above the other as one texture (top, centre, bottom); null when one is missing.</summary>
    public static ImageTexture? Column(params string[] parts)
    {
        var imgs = parts.Select(Img).ToList();
        if (imgs.Any(i => i == null)) return null;
        int w = imgs.Max(i => i!.GetWidth()), h = imgs.Sum(i => i!.GetHeight()), y = 0;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        foreach (var i in imgs)
        {
            i!.Convert(Image.Format.Rgba8);
            img.BlitRect(i, new Rect2I(0, 0, i.GetWidth(), i.GetHeight()), new Vector2I(0, y));
            y += i.GetHeight();
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>A stretched box of a strip (left part, stretched middle, right part).</summary>
    static StyleBoxTexture? StripBox(float left, float right, params string[] parts)
    {
        var t = Strip(parts);
        if (t == null) return null;
        return new StyleBoxTexture
        {
            Texture = t, TextureMarginLeft = left, TextureMarginRight = right,
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
        };
    }

    /// <summary>The Godot theme of all menus (buttons, labels, lists, check boxes, option buttons, line edits).</summary>
    public static Theme Theme
    {
        get
        {
            if (s_theme != null) return s_theme;
            var th = new Theme { DefaultFont = BodyFont, DefaultFontSize = 15 };
            th.SetStylebox("normal", "Button", ButtonBox("idle", new Color(0.18f, 0.12f, 0.06f)));
            th.SetStylebox("hover", "Button", ButtonBox("hover", new Color(0.26f, 0.18f, 0.08f)));
            th.SetStylebox("pressed", "Button", ButtonBox("down", new Color(0.12f, 0.08f, 0.04f)));
            th.SetStylebox("disabled", "Button", ButtonBox("disabled", new Color(0.15f, 0.15f, 0.15f)));
            th.SetStylebox("focus", "Button", new StyleBoxEmpty());
            th.SetFont("font", "Button", ButtonFont);
            th.SetFontSize("font_size", "Button", 17);
            foreach (var k in new[] { "font_color", "font_focus_color" }) th.SetColor(k, "Button", Text);
            th.SetColor("font_hover_color", "Button", Hi);
            th.SetColor("font_pressed_color", "Button", Hi);
            th.SetColor("font_disabled_color", "Button", Dim);
            th.SetConstant("outline_size", "Button", 0);
            th.SetColor("font_color", "Label", Text);
            th.SetColor("font_shadow_color", "Label", new Color(0, 0, 0, 0.8f));
            th.SetConstant("shadow_offset_x", "Label", 1);
            th.SetConstant("shadow_offset_y", "Label", 1);
            th.SetColor("default_color", "RichTextLabel", Text);

            var fieldBox = new StyleBoxFlat { BgColor = new Color(0.08f, 0.05f, 0.02f, 0.85f), BorderColor = new Color(Dim, 0.6f), ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 3, ContentMarginBottom = 3 };
            fieldBox.SetBorderWidthAll(1);
            th.SetStylebox("panel", "ItemList", fieldBox);
            th.SetStylebox("focus", "ItemList", new StyleBoxEmpty());
            th.SetColor("font_color", "ItemList", Text);
            th.SetColor("font_selected_color", "ItemList", Hi);
            th.SetColor("font_hovered_color", "ItemList", Hi);
            var sel = new StyleBoxFlat { BgColor = new Color(0.45f, 0.28f, 0.08f, 0.6f) };
            th.SetStylebox("selected", "ItemList", sel);
            th.SetStylebox("selected_focus", "ItemList", sel);
            th.SetStylebox("normal", "LineEdit", fieldBox);
            th.SetStylebox("focus", "LineEdit", fieldBox);
            th.SetColor("font_color", "LineEdit", Hi);
            th.SetColor("font_placeholder_color", "LineEdit", new Color(Dim, 0.7f));
            th.SetStylebox("normal", "OptionButton", fieldBox);
            th.SetStylebox("hover", "OptionButton", fieldBox);
            th.SetStylebox("pressed", "OptionButton", fieldBox);
            th.SetStylebox("focus", "OptionButton", new StyleBoxEmpty());
            th.SetFont("font", "OptionButton", BodyFont);
            th.SetFontSize("font_size", "OptionButton", 14);
            th.SetColor("font_color", "OptionButton", Hi);
            th.SetStylebox("panel", "PopupMenu", fieldBox);
            th.SetColor("font_color", "PopupMenu", Text);
            th.SetColor("font_hover_color", "PopupMenu", Hi);
            th.SetStylebox("normal", "SpinBox", fieldBox);
            var chkOn = Tex("controls__checkbox_checked");
            var chkOff = Tex("controls__checkbox_empty");
            if (chkOn != null && chkOff != null)
            {
                th.SetIcon("checked", "CheckBox", chkOn);
                th.SetIcon("unchecked", "CheckBox", chkOff);
            }
            var radOn = Tex("controls__radio_active");
            var radOff = Tex("controls__radio_inactive");
            if (radOn != null && radOff != null)
            {
                th.SetIcon("radio_checked", "CheckBox", radOn);
                th.SetIcon("radio_unchecked", "CheckBox", radOff);
            }
            th.SetColor("font_color", "CheckBox", Text);
            th.SetColor("font_hover_color", "CheckBox", Hi);
            th.SetColor("font_pressed_color", "CheckBox", Text);
            foreach (var st in new[] { "normal", "hover", "pressed", "focus", "hover_pressed" })
                th.SetStylebox(st, "CheckBox", new StyleBoxEmpty());
            th.SetStylebox("panel", "TooltipPanel", fieldBox);
            th.SetColor("font_color", "TooltipLabel", Text);
            // the drop lists' button (controls/dropdownbutton_*) and field (dropfield_backtile)
            if (Tex("controls__dropdownbutton_idle") is { } dd) th.SetIcon("arrow", "OptionButton", dd);
            if (Tex("controls__dropfield_backtile") is { } df)
            {
                var drop = new StyleBoxTexture { Texture = df, AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                    ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2 };
                foreach (var st in new[] { "normal", "hover", "pressed" }) th.SetStylebox(st, "OptionButton", drop);
            }
            // sliders (controls/slider_hrz_*: the rail's ends, its empty and filled parts, the mover)
            var rail = StripBox(9, 9, "controls__slider_hrz_left", "controls__slider_hrz_rightbar", "controls__slider_hrz_right");
            var fill = StripBox(9, 0, "controls__slider_hrz_left", "controls__slider_hrz_leftbar");
            var mover = Tex("controls__slider_hrz_mover");
            if (rail != null && fill != null && mover != null)
            {
                // (a slider draws its rail at the box's minimum height: the art's 21 px)
                foreach (var b in new[] { rail, fill }) { b.ContentMarginTop = 10.5f; b.ContentMarginBottom = 10.5f; }
                th.SetStylebox("slider", "HSlider", rail);
                th.SetStylebox("grabber_area", "HSlider", fill);
                th.SetStylebox("grabber_area_highlight", "HSlider", fill);
                th.SetIcon("grabber", "HSlider", mover);
                th.SetIcon("grabber_highlight", "HSlider", mover);
                th.SetIcon("grabber_disabled", "HSlider", Tex("controls__slider_hrz_mover_disabled") ?? mover);
                th.SetConstant("center_grabber", "HSlider", 1);
            }
            // vertical scroll bars (controls/scrollbar_vrt_*: the rail, the mover's top / centre / bottom)
            var vrail = Tex("controls__scrollbar_vrt_rail_idle");
            var vmover = Column("controls__scrollbar_vrt_mover_top", "controls__scrollbar_vrt_mover_center", "controls__scrollbar_vrt_mover_bottom");
            if (vrail != null && vmover != null)
            {
                th.SetStylebox("scroll", "VScrollBar", new StyleBoxTexture { Texture = vrail, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile, ContentMarginLeft = vrail.GetWidth() / 2f, ContentMarginRight = vrail.GetWidth() / 2f });
                th.SetStylebox("scroll_focus", "VScrollBar", new StyleBoxTexture { Texture = vrail, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile, ContentMarginLeft = vrail.GetWidth() / 2f, ContentMarginRight = vrail.GetWidth() / 2f });
                var g = new StyleBoxTexture { Texture = vmover, TextureMarginTop = 4, TextureMarginBottom = 4, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch, ContentMarginTop = 4, ContentMarginBottom = 4 };
                foreach (var st in new[] { "grabber", "grabber_highlight", "grabber_pressed" }) th.SetStylebox(st, "VScrollBar", g);
            }
            // tabs (controls/tabctrl_*: the active tab open to its page, the others behind)
            var act = StripBox(8, 8, "controls__tabctrl_acttab_left", "controls__tabctrl_acttab_center", "controls__tabctrl_acttab_right");
            var inact = StripBox(8, 8, "controls__tabctrl_inacttab_left", "controls__tabctrl_inacttab_center", "controls__tabctrl_inacttab_right");
            th.SetTypeVariation("PwTab", "Button");
            if (act != null && inact != null)
            {
                foreach (var b in new[] { act, inact }) { b.ContentMarginLeft = 14; b.ContentMarginRight = 14; b.ContentMarginTop = 3; b.ContentMarginBottom = 2; }
                th.SetStylebox("normal", "PwTab", inact);
                th.SetStylebox("hover", "PwTab", inact);
                th.SetStylebox("pressed", "PwTab", act);
                th.SetStylebox("hover_pressed", "PwTab", act);
                th.SetStylebox("disabled", "PwTab", inact);
            }
            th.SetFontSize("font_size", "PwTab", 15);
            th.SetColor("font_color", "PwTab", Dim);
            th.SetColor("font_pressed_color", "PwTab", Hi);
            th.SetColor("font_hover_pressed_color", "PwTab", Hi);
            s_theme = th;
            return th;
        }
    }

    // ---- helpers to build menus in code ----

    public static Label Heading(string text, int size = 22, Color? color = null) => new()
    {
        Text = text, HorizontalAlignment = HorizontalAlignment.Center,
        LabelSettings = new LabelSettings { Font = BodyFont, FontSize = size, FontColor = color ?? Hi, ShadowColor = new Color(0, 0, 0, 0.8f), ShadowOffset = new Vector2(1, 1) },
    };

    public static Label Para(string text, int size = 14, Color? color = null, HorizontalAlignment align = HorizontalAlignment.Left) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = align,
        LabelSettings = new LabelSettings { Font = BodyFont, FontSize = size, FontColor = color ?? Dim },
    };

    public static Button Btn(string text, Action onPressed, float minWidth = 240)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(minWidth, 30), FocusMode = Control.FocusModeEnum.None };
        b.Pressed += onPressed;
        return b;
    }
}
